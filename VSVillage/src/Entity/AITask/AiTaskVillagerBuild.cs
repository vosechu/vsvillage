using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

public class AiTaskVillagerBuild : AiTaskGotoAndInteract
{
    private const string HammerAnimCode = "hammer-forge";

    private long hammerDurationMs;
    private long hammerStartedAtMs;
    private Vec3d markerStandPos;
    private Vec3d markerLookPos;
    private BlockPos activeMarkerPos;

    private readonly bool applicable;

    public AiTaskVillagerBuild(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        hammerDurationMs = (long)(taskConfig["hammerDurationSeconds"].AsFloat(30f) * 1000f);
        string onlyFor = taskConfig["onlyForEntitySuffix"].AsString("-builder");
        applicable = string.IsNullOrEmpty(onlyFor) || (entity.Code?.Path?.EndsWith(onlyFor) ?? false);
    }

    public override bool ShouldExecute()
    {
        if (!applicable) return false;
        if (duringDayTimeFrames != null && duringDayTimeFrames.Length > 0
            && !IntervalUtil.matchesCurrentTime(duringDayTimeFrames, entity.World))
            return false;
        return base.ShouldExecute();
    }

    // Picks a random ground-clear cell on the ring just outside the rotated scaffold perimeter.
    // Each task invocation rolls a new position so the builder visibly tours the build site.
    // With multiple builders, prefers the site that has the fewest builders registered today.
    protected override Vec3d GetTargetPos()
    {
        Village village = entity.GetBehavior<EntityBehaviorVillager>()?.Village;
        if (village == null) return null;

        // Build a stable list of incomplete sites in queue order (insertion order = age order).
        // Each builder maps to a site via entity ID so builders spread across sites
        // deterministically without oscillating when new sites are added.
        var validSites = new List<(BlockPos pos, BlockEntityBuildingMarker be)>();
        foreach (BlockPos candidate in village.ConstructionQueue)
        {
            BlockEntityBuildingMarker candidateBe = entity.World.BlockAccessor.GetBlockEntity<BlockEntityBuildingMarker>(candidate);
            if (candidateBe == null || candidateBe.IsComplete) continue;
            validSites.Add((candidate, candidateBe));
        }
        if (validSites.Count == 0) return null;

        // Always start at oldest site; only spill to the next when oldest has hit the 3-builder day cap.
        const int builderCap = 3;
        int siteIdx = 0;
        for (int i = 0; i < validSites.Count - 1; i++)
        {
            if (validSites[i].be.BuilderCountToday >= builderCap) siteIdx = i + 1;
            else break;
        }
        BlockPos active = validSites[siteIdx].pos;
        BlockEntityBuildingMarker activeBe = validSites[siteIdx].be;

        activeMarkerPos = active.Copy();
        markerLookPos = active.ToVec3d().Add(0.5, 0.5, 0.5);

        IBlockAccessor ba = entity.World.BlockAccessor;

        BlockEntityBuildingMarker be = activeBe;
        BuildingCatalog catalog = entity.World.Api.ModLoader.GetModSystem<BuildingCatalog>();
        BuildingDefinition def = (be != null && catalog != null) ? catalog.Get(be.SelectedSchematicId) : null;

        int sx = def?.SizeX ?? 3;
        int sz = def?.SizeZ ?? 3;
        int angle = be?.RotationAngle ?? 0;
        if (angle == 90 || angle == 270) { int tmp = sx; sx = sz; sz = tmp; }
        int halfL = sx / 2;
        int innerMinDx = -halfL;
        int innerMaxDx = (sx - 1) - halfL;
        int innerMinDz = -(sz + 1);
        int innerMaxDz = -2;

        // Scaffold cage sits at innerMin-1 / innerMax+1; ring must be one step beyond that.
        int ringMinDx = innerMinDx - 2;
        int ringMaxDx = innerMaxDx + 2;
        int ringMinDz = innerMinDz - 2;
        int ringMaxDz = innerMaxDz + 2;

        List<BlockPos> candidates = new List<BlockPos>();
        for (int dx = ringMinDx; dx <= ringMaxDx; dx++)
        {
            candidates.Add(new BlockPos(active.X + dx, active.Y, active.Z + ringMinDz, active.dimension));
            candidates.Add(new BlockPos(active.X + dx, active.Y, active.Z + ringMaxDz, active.dimension));
        }
        for (int dz = ringMinDz + 1; dz <= ringMaxDz - 1; dz++)
        {
            candidates.Add(new BlockPos(active.X + ringMinDx, active.Y, active.Z + dz, active.dimension));
            candidates.Add(new BlockPos(active.X + ringMaxDx, active.Y, active.Z + dz, active.dimension));
        }

        List<BlockPos> valid = new List<BlockPos>();
        foreach (BlockPos cand in candidates)
        {
            Block below = ba.GetBlock(cand.DownCopy());
            Block atFoot = ba.GetBlock(cand);
            Block atHead = ba.GetBlock(cand.UpCopy());
            bool groundSolid = below.CollisionBoxes?.Length > 0;
            bool footClear = (atFoot.CollisionBoxes?.Length ?? 0) == 0;
            bool headClear = (atHead.CollisionBoxes?.Length ?? 0) == 0;
            if (groundSolid && footClear && headClear) valid.Add(cand);
        }

        if (valid.Count == 0)
        {
            markerStandPos = active.AddCopy(BlockFacing.SOUTH).ToVec3d().Add(0.5, 0.0, 0.5);
            return markerStandPos;
        }

        BlockPos chosen = valid[entity.World.Rand.Next(valid.Count)];
        markerStandPos = chosen.ToVec3d().Add(0.5, 0.0, 0.5);
        return markerStandPos;
    }

    protected override bool InteractionPossible()
    {
        if (targetPos == null) return false;
        double dx = entity.Pos.X - targetPos.X;
        double dz = entity.Pos.Z - targetPos.Z;
        return dx * dx + dz * dz < 1.5;
    }

    public override void StartExecute()
    {
        base.StartExecute();
        hammerStartedAtMs = 0L;
    }

    public override bool ContinueExecute(float dt)
    {
        if (duringDayTimeFrames != null && duringDayTimeFrames.Length > 0
            && !IntervalUtil.matchesCurrentTime(duringDayTimeFrames, entity.World))
            return false;

        if (!targetReached) return base.ContinueExecute(dt);

        if (hammerStartedAtMs == 0L) hammerStartedAtMs = entity.World.ElapsedMilliseconds;

        entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
        entity.Controls.StopAllMovement();

        if (markerLookPos != null)
        {
            Vec3d from = entity.Pos.XYZ.Add(0.0, entity.SelectionBox.Y2 * 0.5, 0.0);
            double dx = markerLookPos.X - from.X;
            double dz = markerLookPos.Z - from.Z;
            entity.Pos.Yaw = (float)Math.Atan2(dx, dz);
        }

        if (!entity.AnimManager.IsAnimationActive(HammerAnimCode))
        {
            entity.AnimManager.StartAnimation(new AnimationMetaData
            {
                Animation = HammerAnimCode,
                Code = HammerAnimCode,
                AnimationSpeed = 1.0f,
                BlendMode = EnumAnimationBlendMode.Add,
                EaseInSpeed = 3f,
                EaseOutSpeed = 3f
            }.Init());
        }

        if (activeMarkerPos != null)
        {
            BlockEntityBuildingMarker be = entity.World.BlockAccessor.GetBlockEntity<BlockEntityBuildingMarker>(activeMarkerPos);
            be?.RegisterBuilderPresence(entity.EntityId);
        }

        return entity.World.ElapsedMilliseconds - hammerStartedAtMs < hammerDurationMs;
    }

    protected override void ApplyInteractionEffect() { }

    public override void FinishExecute(bool cancelled)
    {
        entity.AnimManager.StopAnimation(HammerAnimCode);
        markerStandPos = null;
        markerLookPos = null;
        activeMarkerPos = null;
        base.FinishExecute(cancelled);
        lastExecution = entity.World.ElapsedMilliseconds;
    }
}