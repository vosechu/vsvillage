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

    private VillagerAStarNew _pathfinder;

    private List<VillagerPathNode> _path;

    private int _pathIdx;

    private Vec3d _target;

    private bool _stuck;

    private Vec3d _lastPos;

    private long _stuckCheckTime;

    private int _timesStuck;

    private long _arrivedAt;

    public AiTaskTravellingTraderStand(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        _pathfinder = new VillagerAStarNew(entity.World.GetCachingBlockAccessor(synchronize: false, relight: false));
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
        if (entity.Pos.SquareDistanceTo(_target) < 2.25)
        {
            Arrive(beh);
            return;
        }
        _pathfinder.blockAccessor.Begin();
        _pathfinder.SetEntityCollisionBox(entity);
        BlockPos start = _pathfinder.GetStartPos(entity.Pos.XYZ);
        _path = _pathfinder.FindPath(start, stallPos, 20000);
        _pathfinder.blockAccessor.Commit();
        if (_path == null || _path.Count == 0)
        {
            entity.World.Logger.Warning($"[TT:{entity.EntityId}] TraderStand: no path to stall {stallPos}. Will retry.");
            _stuck = true;
        }
        else
        {
            entity.World.Logger.Debug($"[TT:{entity.EntityId}] TraderStand: path found ({_path.Count} nodes) to {stallPos}.");
            _pathIdx = 0;
            _lastPos = entity.Pos.XYZ.Clone();
            _stuckCheckTime = entity.World.ElapsedMilliseconds;
        }
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
        CloseOpenDoors();
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
            entity.World.Logger.Notification($"[TT:{entity.EntityId}] TraderStand: arrived at market stall.");
        }
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
                ToggleDoor(open: true, _path[_pathIdx].BlockPos);
            }
            if (node.IsDoor)
            {
                BlockPos dp = node.BlockPos.Copy();
                entity.World.RegisterCallback(delegate
                {
                    ToggleDoor(open: false, dp);
                }, 5000);
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
        if (now - _stuckCheckTime < 3000)
        {
            return;
        }
        Vec3d myPos = entity.Pos.XYZ;
        if (_lastPos != null && (double)myPos.DistanceTo(_lastPos) < 0.4)
        {
            if (++_timesStuck >= 4)
            {
                entity.World.Logger.Warning($"[TT:{entity.EntityId}] TraderStand: stuck after {_timesStuck} checks — giving up.");
                _stuck = true;
                _timesStuck = 0;
            }
        }
        else
        {
            _timesStuck = 0;
        }
        _lastPos = myPos.Clone();
        _stuckCheckTime = now;
    }

    private void ToggleDoor(bool open, BlockPos pos)
    {
        Block b = entity.World.BlockAccessor.GetBlock(pos);
        if (b?.Code == null || (!b.Code.Path.Contains("door") && !b.Code.Path.Contains("gate")))
        {
            return;
        }
        BlockSelection sel = new BlockSelection
        {
            Block = b,
            Position = pos,
            HitPosition = new Vec3d(0.5, 0.5, 0.5),
            Face = BlockFacing.NORTH
        };
        TreeAttribute attrs = new TreeAttribute();
        attrs.SetBool("opened", open);
        try
        {
            b.Activate(entity.World, new Caller
            {
                Entity = entity,
                Type = EnumCallerType.Entity,
                Pos = entity.Pos.XYZ
            }, sel, attrs);
        }
        catch (Exception ex)
        {
            entity.World.Logger.Error($"[TT:{entity.EntityId}] TraderStand: door toggle failed: {ex.Message}");
        }
    }

    private void CloseOpenDoors()
    {
        if (_path == null)
        {
            return;
        }
        foreach (VillagerPathNode node in _path)
        {
            if (node.IsDoor)
            {
                Block b = entity.World.BlockAccessor.GetBlock(node.BlockPos);
                if (b?.Code != null && (b.Code.Path.Contains("opened") || b.Code.Path.Contains("open")))
                {
                    ToggleDoor(open: false, node.BlockPos);
                }
            }
        }
    }
}