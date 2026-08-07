using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

// Anglers leave their workstation in the morning, sit on a fishingspot block (or, failing that,
// any shoreline inside the village boundary) and fish until their morning window ends.
// They then walk back to the workstation so the regular work-idle task can take over.
public class AiTaskVillagerAnglerFish : AiTaskBase
{
    private enum Phase
    {
        GoToFishingSpot,
        Fishing,
        GoHome
    }

    private const double ArrivalDistanceSq = 2.25;
    private const string SitAnimationCode = "sitedgefish";

    private readonly float moveSpeed;
    private readonly int pathfindNodes;
    private readonly int villageBoundaryPadding;
    private readonly long spotCacheMs;
    private readonly VillagerAStarNew pathfinder;

    private Phase phase;
    private BlockPos workstationPos;
    private BlockPos fishingSpotBlock;
    private Vec3d returnSpotPos;
    private float fishingYaw;
    private Vec3d targetPos;
    private List<VillagerPathNode> currentPath;
    private int currentPathIndex;
    private bool stuck;
    private Vec3d lastPosition;
    private long stuckCheckTime;
    private int timesStuck;
    private long lastRepathTime;
    private long lastCompletedDay = long.MinValue;
    private FishingSpot cachedSpot;
    private long cacheExpiresMs = -1L;
    private long lastFailureLogMs = -1L;
    private string lastFailureReason;
    private bool isFallbackSpot;
    private static readonly HashSet<BlockPos> ReservedSpots = new HashSet<BlockPos>();

    public AiTaskVillagerAnglerFish(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        moveSpeed = taskConfig["movespeed"].AsFloat(0.08f);
        pathfindNodes = taskConfig["pathfindNodes"].AsInt(9000);
        villageBoundaryPadding = taskConfig["villageBoundaryPadding"].AsInt(2);
        spotCacheMs = taskConfig["spotCacheSeconds"].AsInt(120) * 1000L;
        pathfinder = new VillagerAStarNew(entity.World.GetCachingBlockAccessor(synchronize: false, relight: false), entity.World, entity);
    }

    public override bool ShouldExecute()
    {
        if (!IsAngler()) return false;
        if (!IntervalUtil.matchesCurrentTime(duringDayTimeFrames, entity.World)) return false;

        long currentDay = (long)Math.Floor(entity.World.Calendar.TotalDays);
        if (currentDay == lastCompletedDay) return false;

        EntityBehaviorVillager behavior = entity.GetBehavior<EntityBehaviorVillager>();
        Village village = behavior?.Village;
        workstationPos = behavior?.Workstation;
        if (village == null || workstationPos == null)
        {
            LogFailure("missing village or workstation");
            return false;
        }

        FishingSpot spot = GetCachedFishingSpot(village);
        if (spot == null)
        {
            LogFailure("no fishingspot block or fishable shoreline found for workstation " + workstationPos);
            return false;
        }

        fishingSpotBlock = spot.StandPos;
        fishingYaw = spot.FacingYaw;
        returnSpotPos = FindWorkstationStandingPos(workstationPos);
        if (returnSpotPos == null)
        {
            LogFailure("could not resolve a return spot for workstation " + workstationPos);
            return false;
        }

        if (!TryReserveSpot(fishingSpotBlock))
        {
            cachedSpot = null;
            cacheExpiresMs = -1L;
            LogFailure("fishing spot already reserved by another angler");
            return false;
        }

        targetPos = spot.SitPos.Clone();
        lastFailureReason = null;
        return true;
    }

    public override void StartExecute()
    {
        base.StartExecute();
        phase = Phase.GoToFishingSpot;
        StopFishingAnimation();
        BuildPathToTarget(targetPos);

        // Never keep a bad cached target. If the chosen fishing spot can't be pathed to,
        // invalidate cache now so the next ShouldExecute performs a fresh search.
        if (stuck || currentPath == null || currentPath.Count == 0)
        {
            cachedSpot = null;
            cacheExpiresMs = -1L;
            return;
        }

        if (!stuck && targetPos != null && entity.Pos.SquareDistanceTo(targetPos) <= ArrivalDistanceSq)
        {
            BeginFishing();
        }
    }

    public override bool ContinueExecute(float dt)
    {
        if (phase == Phase.GoToFishingSpot)
        {
            if (!ContinuePathing()) return false;
            if (HasReachedTarget())
            {
                BeginFishing();
            }
            return true;
        }

        if (phase == Phase.Fishing)
        {
            if (!IntervalUtil.matchesCurrentTime(duringDayTimeFrames, entity.World))
            {
                BeginGoHome();
                return true;
            }

            entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
            entity.Controls.StopAllMovement();
            entity.Pos.Motion.Set(0.0, 0.0, 0.0);
            entity.Pos.Yaw = fishingYaw;
            EnsureFishingAnimation();
            return true;
        }

        if (phase == Phase.GoHome)
        {
            if (!ContinuePathing()) return false;
            if (HasReachedTarget())
            {
                lastCompletedDay = (long)Math.Floor(entity.World.Calendar.TotalDays);
                return false;
            }
            return true;
        }

        return false;
    }

    public override void FinishExecute(bool cancelled)
    {
        UnreserveSpot(fishingSpotBlock);
        base.FinishExecute(cancelled);
        entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
        entity.Controls.StopAllMovement();
        entity.Pos.Motion.Set(0.0, 0.0, 0.0);
        if (animMeta != null) entity.AnimManager.StopAnimation(animMeta.Code);
        StopFishingAnimation();
        DoorPathHelper.CloseOpenDoorsAlongPath(entity, currentPath);
        currentPath = null;
        currentPathIndex = 0;
        targetPos = null;
        lastPosition = null;
        timesStuck = 0;
        stuck = false;
    }

    private FishingSpot GetCachedFishingSpot(Village village)
    {
        long now = entity.World.ElapsedMilliseconds;
        if (now < cacheExpiresMs)
        {
            // Cache MISSES too. The scan is village.Radius squared times 7 block reads and runs on
            // every ShouldExecute tick, so an angler with no reachable spot was pinning the server.
            if (cachedSpot == null) return null;
            if (cachedSpot.IsStillValid(village, villageBoundaryPadding)
                && !IsSpotOccupied(cachedSpot.StandPos, isFallbackSpot) && !ReservedSpots.Contains(cachedSpot.StandPos))
                return cachedSpot;
        }

        cachedSpot = FindNearestFishingSpot(village);
        if (cachedSpot == null)
        {
            // Shoreline fallback only when there is a real body of water. A placed fishingspot
            // block bypasses the volume check so small docked ponds still work.
            if (VillagerHireRequirementChecker.HasValidFishableWaterNearby(workstationPos, entity.Api))
            {
                cachedSpot = FindWaterEdgeFishingSpot(village);
            }
            isFallbackSpot = cachedSpot != null;
        }
        else
        {
            isFallbackSpot = false;
        }
        cacheExpiresMs = now + spotCacheMs;
        return cachedSpot;
    }

    private FishingSpot FindNearestFishingSpot(Village village)
    {
        IBlockAccessor ba = entity.World.BlockAccessor;
        int searchRadius = Math.Max(village.Radius, 4);
        int centerY = village.EffectiveCenterY();
        BlockPos best = null;
        double bestSq = double.MaxValue;
        BlockPos tmp = new BlockPos(0);

        for (int dx = -searchRadius; dx <= searchRadius; dx++)
        {
            for (int dz = -searchRadius; dz <= searchRadius; dz++)
            {
                int sq = dx * dx + dz * dz;
                if (sq > searchRadius * searchRadius) continue;
                for (int dy = -3; dy <= 3; dy++)
                {
                    tmp.Set(village.Pos.X + dx, centerY + dy, village.Pos.Z + dz);
                    Block b = ba.GetBlock(tmp);
                    if (b?.Code?.Path == null) continue;
                    if (!b.Code.Path.Contains("fishingspot")) continue;
                    if (IsSpotOccupied(tmp, fallback: false)) continue;
                    if (ReservedSpots.Contains(tmp)) continue;
                    double dsq = sq + dy * dy;
                    if (dsq < bestSq)
                    {
                        bestSq = dsq;
                        best = tmp.Copy();
                    }
                }
            }
        }

        if (best == null) return null;

        Vec3d sitPos = best.ToVec3d().Add(0.5, 0.0, 0.5);
        float yaw = GetYawFromFishingSpotBlock(best, ba);
        return new FishingSpot(best, sitPos, yaw);
    }

    // Fallback: no fishingspot block placed. Find solid ground adjacent to water inside the
    // village boundary; the angler stands at the water's edge and fishes there.
    private FishingSpot FindWaterEdgeFishingSpot(Village village)
    {
        IBlockAccessor ba = entity.World.BlockAccessor;
        int searchRadius = Math.Max(village.Radius, 4);
        int centerY = village.EffectiveCenterY();
        BlockPos bestStandPos = null;
        double bestSq = double.MaxValue;
        BlockPos tmp = new BlockPos(0);
        BlockPos neighborTmp = new BlockPos(0);

        for (int dx = -searchRadius; dx <= searchRadius; dx++)
        {
            for (int dz = -searchRadius; dz <= searchRadius; dz++)
            {
                int sq = dx * dx + dz * dz;
                if (sq > searchRadius * searchRadius) continue;
                for (int dy = -3; dy <= 3; dy++)
                {
                    tmp.Set(village.Pos.X + dx, centerY + dy, village.Pos.Z + dz);
                    if (!IsWaterAtAnyLayer(ba, tmp)) continue;

                    foreach (BlockFacing facing in BlockFacing.HORIZONTALS)
                    {
                        neighborTmp.Set(tmp.X + facing.Normali.X, tmp.Y, tmp.Z + facing.Normali.Z);
                        if (!IsWithinVillageBoundary(neighborTmp, village)) continue;
                        if (!IsStandableSpot(neighborTmp, ba)) continue;
                        if (IsSpotOccupied(neighborTmp, fallback: true)) continue;
                        if (ReservedSpots.Contains(neighborTmp)) continue;

                        double dsq = sq + dy * dy;
                        if (dsq < bestSq)
                        {
                            bestSq = dsq;
                            bestStandPos = neighborTmp.Copy();
                        }
                    }
                }
            }
        }

        if (bestStandPos == null) return null;

        Vec3d sitPos = bestStandPos.ToVec3d().Add(0.5, 0.0, 0.5);
        float yaw = GetYawFromFishingSpotBlock(bestStandPos, ba);
        return new FishingSpot(bestStandPos, sitPos, yaw);
    }

    private bool IsWithinVillageBoundary(BlockPos pos, Village village)
    {
        int allowedRadius = Math.Max(4, village.Radius - villageBoundaryPadding);
        int dx = pos.X - village.Pos.X;
        int dz = pos.Z - village.Pos.Z;
        return dx * dx + dz * dz <= allowedRadius * allowedRadius;
    }

    // Face the angler toward adjacent water. Same-Y water wins over Y-1 water so corner spots
    // orient toward open lake instead of a stepped-down edge. Both block layers probed: lake
    // water lives in the fluid layer and can be hidden by a non-empty solid layer.
    private static float GetYawFromFishingSpotBlock(BlockPos pos, IBlockAccessor ba)
    {
        BlockPos probe = new BlockPos(0);

        foreach (BlockFacing facing in BlockFacing.HORIZONTALS)
        {
            probe.Set(pos.X + facing.Normali.X, pos.Y, pos.Z + facing.Normali.Z);
            if (IsWaterAtAnyLayer(ba, probe)) return YawForFacing(facing);
        }

        foreach (BlockFacing facing in BlockFacing.HORIZONTALS)
        {
            probe.Set(pos.X + facing.Normali.X, pos.Y - 1, pos.Z + facing.Normali.Z);
            if (IsWaterAtAnyLayer(ba, probe)) return YawForFacing(facing);
        }

        return 0f;
    }

    private static bool IsWaterAtAnyLayer(IBlockAccessor ba, BlockPos pos)
    {
        if (IsAnyWaterFamily(ba.GetBlock(pos, BlockLayersAccess.Solid))) return true;
        if (IsAnyWaterFamily(ba.GetBlock(pos, BlockLayersAccess.Fluid))) return true;
        return false;
    }

    // Yaw is offset +PI/2+PI from bare Atan2(dx,dz) because the sitedgefish pose splays sideways;
    // values were corrected against in-game testing (Sakura 2026-05-09). If a test shows the
    // angler facing 180 degrees from the water, swap NORTH/SOUTH and EAST/WEST signs.
    private static float YawForFacing(BlockFacing facing)
    {
        if (facing == BlockFacing.NORTH) return 0f;
        if (facing == BlockFacing.SOUTH) return (float)Math.PI;
        if (facing == BlockFacing.EAST)  return (float)(Math.PI / 2);
        if (facing == BlockFacing.WEST)  return (float)(-Math.PI / 2);
        return 0f;
    }

    private static bool IsAnyWaterFamily(Block block)
    {
        string path = block?.Code?.Path;
        if (path == null) return false;
        return path.StartsWith("water-", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("saltwater-", StringComparison.OrdinalIgnoreCase);
    }

    private Vec3d FindWorkstationStandingPos(BlockPos wsPos)
    {
        Vec3d workstationCenter = wsPos.ToVec3d().Add(0.25, 0.0, 0.25);
        IBlockAccessor blockAccessor = entity.World.BlockAccessor;
        Block block = blockAccessor.GetBlock(wsPos);
        BlockFacing blockFacing = BlockFacing.NORTH;

        if (block?.Variant != null)
        {
            if (block.Variant.TryGetValue("side", out string side))
            {
                blockFacing = BlockFacing.FromCode(side) ?? BlockFacing.NORTH;
            }
            else if (block.Variant.TryGetValue("facing", out string facing))
            {
                blockFacing = BlockFacing.FromCode(facing) ?? BlockFacing.NORTH;
            }
            else if (block.Variant.TryGetValue("orientation", out string orientation))
            {
                blockFacing = BlockFacing.FromCode(orientation) ?? BlockFacing.NORTH;
            }
        }

        List<BlockFacing> candidateFacings = new List<BlockFacing> { blockFacing.Opposite };
        foreach (BlockFacing horizontal in BlockFacing.HORIZONTALS)
        {
            if (horizontal != blockFacing.Opposite) candidateFacings.Add(horizontal);
        }

        foreach (BlockFacing facing in candidateFacings)
        {
            Vec3d candidate = workstationCenter.AddCopy(facing.Normalf.X * 0.75, 0.0, facing.Normalf.Z * 0.75);
            if (IsStandableSpot(candidate.AsBlockPos, blockAccessor))
                return candidate;
        }

        return workstationCenter;
    }

    private static bool IsStandableSpot(BlockPos pos, IBlockAccessor ba)
    {
        Block foot  = ba.GetBlock(pos);
        Block head  = ba.GetBlock(pos.UpCopy());
        Block below = ba.GetBlock(pos.DownCopy());
        bool footClear = foot.CollisionBoxes == null || foot.CollisionBoxes.Length == 0;
        bool headClear = head.CollisionBoxes == null || head.CollisionBoxes.Length == 0;
        bool grounded  = below.Id != 0;
        return footClear && headClear && grounded;
    }

    private static bool TryReserveSpot(BlockPos pos)
    {
        if (pos == null) return false;
        lock (ReservedSpots)
        {
            if (ReservedSpots.Contains(pos)) return false;
            ReservedSpots.Add(pos);
            return true;
        }
    }

    private static void UnreserveSpot(BlockPos pos)
    {
        if (pos == null) return;
        lock (ReservedSpots)
        {
            ReservedSpots.Remove(pos);
        }
    }

    private bool IsSpotOccupied(BlockPos spotPos, bool fallback)
    {
        double sitY = spotPos.Y + (fallback ? 1.0 : 0.7);
        Vec3d center = new Vec3d(spotPos.X + 0.5, sitY, spotPos.Z + 0.5);
        Entity[] nearby = entity.World.GetEntitiesAround(center, 0.8f, 0.8f, e =>
            e is EntityVillager && e.EntityId != entity.EntityId);
        return nearby.Length > 0;
    }

    private void BeginFishing()
    {
        phase = Phase.Fishing;
        entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
        entity.Controls.StopAllMovement();
        entity.Pos.Motion.Set(0.0, 0.0, 0.0);
        if (animMeta != null) entity.AnimManager.StopAnimation(animMeta.Code);

        // Snap onto the spot: fishingspot platform sits at +0.7, fallback ground at +1.0.
        if (fishingSpotBlock != null)
        {
            entity.Pos.X = fishingSpotBlock.X + 0.5;
            entity.Pos.Z = fishingSpotBlock.Z + 0.5;
            entity.Pos.Y = fishingSpotBlock.Y + (isFallbackSpot ? 1.0 : 0.7);
        }

        EnsureFishingAnimation();
        entity.Pos.Yaw = fishingYaw;
    }

    private void BeginGoHome()
    {
        UnreserveSpot(fishingSpotBlock);
        StopFishingAnimation();
        targetPos = returnSpotPos?.Clone();
        if (targetPos == null)
        {
            phase = Phase.GoHome;
            stuck = true;
            return;
        }

        phase = Phase.GoHome;
        BuildPathToTarget(targetPos);
    }

    private bool ContinuePathing()
    {
        if (targetPos == null || stuck || currentPath == null) return false;

        CheckIfStuck();
        if (stuck || currentPath == null) return false;

        if (HasReachedTarget())
        {
            entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
            entity.Controls.StopAllMovement();
            return true;
        }

        HandlePathTraversal();
        return true;
    }

    private bool HasReachedTarget()
    {
        return targetPos != null && entity.Pos.SquareDistanceTo(targetPos) <= ArrivalDistanceSq;
    }

    private void BuildPathToTarget(Vec3d desiredTarget)
    {
        currentPath = null;
        currentPathIndex = 0;
        targetPos = desiredTarget?.Clone();
        stuck = targetPos == null;
        lastPosition = null;
        timesStuck = 0;
        if (stuck) return;

        pathfinder.blockAccessor.Begin();
        pathfinder.SetEntityCollisionBox(entity);
        BlockPos startPos = pathfinder.GetStartPos(entity.Pos.XYZ);
        currentPath = pathfinder.FindPath(startPos, targetPos.AsBlockPos, pathfindNodes);
        pathfinder.blockAccessor.Commit();

        if (currentPath == null || currentPath.Count == 0)
        {
            stuck = true;
            // Invalidate cached spot on path failure so we don't retry the same unreachable
            // shoreline for the whole cache window.
            if (phase == Phase.GoToFishingSpot)
            {
                cachedSpot = null;
                cacheExpiresMs = -1L;
            }
            return;
        }

        currentPathIndex = 0;
        lastPosition = entity.Pos.XYZ.Clone();
        stuckCheckTime = entity.World.ElapsedMilliseconds;
        timesStuck = 0;
        stuck = false;
    }

    private void HandlePathTraversal()
    {
        if (currentPath == null || currentPathIndex >= currentPath.Count)
        {
            stuck = true;
            return;
        }

        VillagerPathNode node = currentPath[currentPathIndex];
        Vec3d nodePos = node.BlockPos.ToVec3d().Add(0.5, 0.0, 0.5);
        Vec3d myPos = entity.Pos.XYZ;
        double dx = myPos.X - nodePos.X;
        double dz = myPos.Z - nodePos.Z;

        if (Math.Sqrt(dx * dx + dz * dz) < 0.5)
        {
            currentPathIndex++;
            if (currentPathIndex < currentPath.Count)
            {
                VillagerPathNode next = currentPath[currentPathIndex];
                if (next.IsDoor) DoorPathHelper.ToggleDoor(entity, next.BlockPos, opened: true);
            }

            if (node.IsDoor)
            {
                DoorPathHelper.ScheduleDoorClose(entity, node.BlockPos.Copy(), 5000);
            }
        }

        if (currentPathIndex < currentPath.Count)
        {
            Vec3d nextPos = currentPath[currentPathIndex].BlockPos.ToVec3d().Add(0.5, 0.0, 0.5);
            Vec3d dir = nextPos.Clone().Sub(myPos);
            dir.Y = 0.0;
            if (dir.LengthSq() < 0.001) return;

            dir = dir.Normalize();
            entity.Pos.Yaw = (float)Math.Atan2(dir.X, dir.Z);
            entity.Controls.WalkVector.Set(dir.X * moveSpeed, 0.0, dir.Z * moveSpeed);
            if (animMeta != null && !entity.AnimManager.IsAnimationActive(animMeta.Code))
            {
                entity.AnimManager.StartAnimation(animMeta);
            }
        }
    }

    private void CheckIfStuck()
    {
        long now = entity.World.ElapsedMilliseconds;
        if (now - stuckCheckTime < 3000) return;

        Vec3d currentPos = entity.Pos.XYZ;
        if (lastPosition != null)
        {
            double moved = currentPos.DistanceTo(lastPosition);
            double threshold = Math.Max(0.25, moveSpeed * 60 * 0.4);
            if (moved < threshold)
            {
                timesStuck++;
                if (timesStuck <= 5)
                {
                    if (now - lastRepathTime > 3000)
                    {
                        AttemptRepath();
                        lastRepathTime = now;
                    }
                }
                else
                {
                    stuck = true;
                }
            }
            else
            {
                timesStuck = 0;
            }
        }

        lastPosition = currentPos.Clone();
        stuckCheckTime = now;
    }

    private void AttemptRepath()
    {
        if (targetPos == null) return;

        pathfinder.blockAccessor.Begin();
        pathfinder.SetEntityCollisionBox(entity);
        BlockPos startPos = pathfinder.GetStartPos(entity.Pos.XYZ);
        List<VillagerPathNode> newPath = pathfinder.FindPath(startPos, targetPos.AsBlockPos, pathfindNodes);
        pathfinder.blockAccessor.Commit();

        if (newPath != null && newPath.Count > 0)
        {
            currentPath = newPath;
            currentPathIndex = 0;
            stuck = false;
        }
    }

    private void EnsureFishingAnimation()
    {
        if (!entity.AnimManager.IsAnimationActive(SitAnimationCode))
        {
            entity.AnimManager.StartAnimation(new AnimationMetaData
            {
                Animation = SitAnimationCode,
                Code = SitAnimationCode,
                AnimationSpeed = 1.0f,
                BlendMode = EnumAnimationBlendMode.Average,
                EaseInSpeed = 2f,
                EaseOutSpeed = 2f
            }.Init());
        }
    }

    private void StopFishingAnimation()
    {
        entity.AnimManager.StopAnimation(SitAnimationCode);
    }

    // ReservedSpots is static and only cleared in FinishExecute, so an angler that despawned or
    // unloaded mid-task held its spot for the life of the process, blocking every other angler
    // and surviving a world reload within the same session.
    public override void OnEntityDespawn(EntityDespawnData reason)
    {
        UnreserveSpot(fishingSpotBlock);
        fishingSpotBlock = null;
        base.OnEntityDespawn(reason);
    }

    private bool IsAngler()
    {
        return entity?.Code?.Path?.EndsWith("-angler") == true;
    }

    private void LogFailure(string reason)
    {
        long now = entity.World.ElapsedMilliseconds;
        if (reason == lastFailureReason && lastFailureLogMs >= 0 && now - lastFailureLogMs < 30000)
        {
            return;
        }

        lastFailureReason = reason;
        lastFailureLogMs = now;
        entity.World.Logger.Warning("[VsVillage] Angler " + entity.EntityId + " will not start fishing: " + reason);
    }

    private sealed class FishingSpot
    {
        public BlockPos StandPos { get; }
        public Vec3d SitPos { get; }
        public float FacingYaw { get; }

        public FishingSpot(BlockPos standPos, Vec3d sitPos, float facingYaw)
        {
            StandPos = standPos;
            SitPos = sitPos;
            FacingYaw = facingYaw;
        }

        public bool IsStillValid(Village village, int boundaryPadding)
        {
            int allowedRadius = Math.Max(4, village.Radius - boundaryPadding);
            int dx = StandPos.X - village.Pos.X;
            int dz = StandPos.Z - village.Pos.Z;
            return dx * dx + dz * dz <= allowedRadius * allowedRadius;
        }
    }
}
