using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

public class AiTaskTravellingTraderStand : AiTaskBase
{
    private const float MoveSpeed = 0.009f;

    private const double ArrivalSq = 2.25;

    private const int StallCooldownMs = 60000;

    private const int StuckCheckIntervalMs = 3000;

    private const int MaxTimesStuck = 4;

    private const int MaxFailuresBeforeTeleport = 5;

    private VillagerAStarNew _pathfinder;

    private List<VillagerPathNode> _path;

    private int _pathIdx;

    private Vec3d _target;

    private bool _stuck;

    private Vec3d _lastPos;

    private long _stuckCheckTime;

    private int _timesStuck;

    private long _arrivedAt;

    // Survives FinishExecute, unlike _timesStuck. The task re-fires every 1-2s and re-runs the
    // same failing A*, so without a cross-run count an unreachable stall retries forever.
    private int _consecutiveFailures;

    public AiTaskTravellingTraderStand(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        _pathfinder = new VillagerAStarNew(entity.World.GetCachingBlockAccessor(synchronize: false, relight: false), entity.World, entity);
    }

    public override bool ShouldExecute()
    {
        if (entity.Api.Side != EnumAppSide.Server)
        {
            return false;
        }
        EntityBehaviorTravellingTrader beh = entity.GetBehavior<EntityBehaviorTravellingTrader>();
        if (beh == null)
        {
            return false;
        }
        // Departure (priority 8.5) outranks this task, but never race it: teleporting a
        // leaving trader back to the stall would undo the departure entirely.
        if (beh.IsLeaving) return false;

        BlockPos stall = beh.MarketStallPos;
        if (stall == null)
        {
            entity.World.Logger.Debug($"[TT:{entity.EntityId}] TraderStand: no stall pos set.");
            return false;
        }
        if (beh.IsAtStall)
        {
            return entity.World.ElapsedMilliseconds >= _arrivedAt + 60000;
        }
        return cooldownUntilMs <= entity.World.ElapsedMilliseconds;
    }

    public override void StartExecute()
    {
        base.StartExecute();
        _stuck = false;
        _path = null;
        _pathIdx = 0;
        _timesStuck = 0;
        _target = null;
        EntityBehaviorTravellingTrader beh = entity.GetBehavior<EntityBehaviorTravellingTrader>();
        BlockPos stallPos = beh?.MarketStallPos;
        if (stallPos == null)
        {
            _stuck = true;
            return;
        }
        _target = stallPos.ToVec3d().Add(0.5, 0.0, 0.5);
        if (entity.Pos.SquareDistanceTo(_target) < ArrivalSq)
        {
            Arrive(beh);
            return;
        }

        if (TryPath(stallPos)) return;

        _consecutiveFailures++;
        entity.World.Logger.Warning($"[TT:{entity.EntityId}] TraderStand: no path to stall {stallPos} (attempt {_consecutiveFailures}).");

        if (_consecutiveFailures >= MaxFailuresBeforeTeleport && TryTeleportToStall(stallPos))
        {
            _consecutiveFailures = 0;
            if (entity.Pos.SquareDistanceTo(_target) < ArrivalSq)
            {
                Arrive(beh);
                return;
            }
            if (TryPath(stallPos)) return;
        }

        _stuck = true;
    }

    public override bool ContinueExecute(float dt)
    {
        if (_stuck || _path == null || _target == null)
        {
            return false;
        }
        CheckIfStuck();
        if (_stuck)
        {
            return false;
        }
        if (_pathIdx >= _path.Count || entity.Pos.SquareDistanceTo(_target) < 2.25)
        {
            Arrive(entity.GetBehavior<EntityBehaviorTravellingTrader>());
            return false;
        }
        StepPath();
        return true;
    }

    public override void FinishExecute(bool cancelled)
    {
        base.FinishExecute(cancelled);
        entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
        entity.Controls.StopAllMovement();
        entity.Pos.Motion.Set(0.0, 0.0, 0.0);
        if (animMeta != null)
        {
            entity.AnimManager.StopAnimation(animMeta.Code);
        }
        DoorPathHelper.CloseOpenDoorsAlongPath(entity, _path);
        _path = null;
        _target = null;
        _timesStuck = 0;
    }

    private void Arrive(EntityBehaviorTravellingTrader beh)
    {
        entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
        entity.Controls.StopAllMovement();
        entity.Pos.Motion.Set(0.0, 0.0, 0.0);
        if (animMeta != null)
        {
            entity.AnimManager.StopAnimation(animMeta.Code);
        }
        _arrivedAt = entity.World.ElapsedMilliseconds;
        if (beh != null && !beh.IsAtStall)
        {
            beh.IsAtStall = true;
            entity.World.Logger.Debug($"[TT:{entity.EntityId}] TraderStand: arrived at market stall.");
        }
    }

    private bool TryPath(BlockPos stallPos)
    {
        try
        {
            _pathfinder.blockAccessor.Begin();
            _pathfinder.SetEntityCollisionBox(entity);
            BlockPos start = _pathfinder.GetStartPos(entity.Pos.XYZ);
            _path = _pathfinder.FindPath(start, stallPos, 20000);
        }
        finally
        {
            _pathfinder.blockAccessor.Commit();
        }

        if (_path == null || _path.Count == 0) return false;

        entity.World.Logger.Debug($"[TT:{entity.EntityId}] TraderStand: path found ({_path.Count} nodes) to {stallPos}.");
        _pathIdx = 0;
        _lastPos = entity.Pos.XYZ.Clone();
        _stuckCheckTime = entity.World.ElapsedMilliseconds;
        _consecutiveFailures = 0;
        return true;
    }

    // This task extends AiTaskBase, so it inherits none of the waypoint/teleport recovery tiers
    // AiTaskGotoAndInteract has. Without this an unreachable stall strands the trader all visit.
    private bool TryTeleportToStall(BlockPos stallPos)
    {
        IBlockAccessor ba = entity.World.BlockAccessor;
        for (int dy = 0; dy >= -2; dy--)
        {
            foreach (BlockFacing facing in BlockFacing.HORIZONTALS)
            {
                BlockPos cand = stallPos.AddCopy(facing.Normali.X, dy, facing.Normali.Z);
                if (!IsStandable(ba, cand)) continue;

                entity.TeleportTo(cand.ToVec3d().Add(0.5, 0.0, 0.5));
                entity.World.Logger.Warning($"[TT:{entity.EntityId}] TraderStand: stall {stallPos} unreachable, teleported to {cand}.");
                return true;
            }
        }
        entity.World.Logger.Warning($"[TT:{entity.EntityId}] TraderStand: no standable spot beside stall {stallPos}; cannot recover.");
        return false;
    }

    // Requires a solid floor below as well as clear body and head. KNOWN_ISSUES flags the missing
    // floor check on the villager recovery teleport; do not repeat that here.
    private static bool IsStandable(IBlockAccessor ba, BlockPos pos)
    {
        Block at = ba.GetBlock(pos);
        Block above = ba.GetBlock(pos.UpCopy());
        Block below = ba.GetBlock(pos.DownCopy());
        if (at == null || above == null || below == null) return false;

        bool bodyClear = at.CollisionBoxes == null || at.CollisionBoxes.Length == 0;
        bool headClear = above.CollisionBoxes == null || above.CollisionBoxes.Length == 0;
        bool grounded  = below.CollisionBoxes != null && below.CollisionBoxes.Length != 0;
        return bodyClear && headClear && grounded;
    }

    private void StepPath()
    {
        if (_path == null || _pathIdx >= _path.Count)
        {
            _stuck = true;
            return;
        }
        VillagerPathNode node = _path[_pathIdx];
        Vec3d centre = node.BlockPos.ToVec3d().Add(0.5, 0.0, 0.5);
        Vec3d myPos = entity.Pos.XYZ;
        double dx = myPos.X - centre.X;
        double dz = myPos.Z - centre.Z;
        if (Math.Sqrt(dx * dx + dz * dz) < 0.5)
        {
            _pathIdx++;
            if (_pathIdx < _path.Count && _path[_pathIdx].IsDoor)
            {
                DoorPathHelper.ToggleDoor(entity, _path[_pathIdx].BlockPos, opened: true);
            }
            if (node.IsDoor)
            {
                BlockPos dp = node.BlockPos.Copy();
                DoorPathHelper.ScheduleDoorClose(entity, dp, 5000);
            }
        }
        if (_pathIdx < _path.Count)
        {
            Vec3d next = _path[_pathIdx].BlockPos.ToVec3d().Add(0.5, 0.0, 0.5);
            Vec3d dir = next.Clone().Sub(myPos);
            dir.Y = 0.0;
            dir = dir.Normalize();
            entity.Pos.Yaw = (float)Math.Atan2(dir.X, dir.Z);
            entity.Controls.WalkVector.Set(dir.X * MoveSpeed, 0.0, dir.Z * MoveSpeed);
            if (animMeta != null && !entity.AnimManager.IsAnimationActive(animMeta.Code))
            {
                entity.AnimManager.StartAnimation(animMeta);
            }
        }
    }

    private void CheckIfStuck()
    {
        long now = entity.World.ElapsedMilliseconds;
        if (now - _stuckCheckTime < StuckCheckIntervalMs)
        {
            return;
        }
        Vec3d myPos = entity.Pos.XYZ;
        if (_lastPos != null && (double)myPos.DistanceTo(_lastPos) < 0.4)
        {
            if (++_timesStuck >= MaxTimesStuck)
            {
                entity.World.Logger.Warning($"[TT:{entity.EntityId}] TraderStand: stuck after {_timesStuck} checks - giving up.");
                _stuck = true;
                _timesStuck = 0;
                // Wedged on terrain counts toward the same budget as an unpathable stall,
                // so a trader stuck against geometry still teleports out eventually.
                _consecutiveFailures++;
            }
        }
        else
        {
            _timesStuck = 0;
        }
        _lastPos = myPos.Clone();
        _stuckCheckTime = now;
    }

}