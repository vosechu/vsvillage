using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

// Builder wanders town doing cosmetic maintenance (hammering fences, walls, doors, workstations)
// when no active construction exists. Pure immersion - no world effect.
public class AiTaskVillagerBuilderMaintenance : AiTaskGotoAndInteract
{
    private const string HammerAnimCode = "hammer-forge";
    private long hammerStartedAtMs;
    private readonly long hammerDurationMs;

    public AiTaskVillagerBuilderMaintenance(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        hammerDurationMs = (long)(taskConfig["hammerDurationSeconds"].AsFloat(6f) * 1000f);
    }

    protected override Vec3d GetTargetPos()
    {
        if (entity?.Code?.Path?.EndsWith("-builder") != true) return null;

        Village village = entity.GetBehavior<EntityBehaviorVillager>()?.Village;
        if (village == null) return null;

        // Yield to actual construction work.
        if (village.ConstructionQueue.Count > 0) return null;

        IBlockAccessor ba = entity.World.BlockAccessor;
        BlockPos centerBp = entity.Pos.XYZ.AsBlockPos;
        BlockPos tmp = new BlockPos(0);
        List<BlockPos> candidates = new List<BlockPos>();

        int r = (int)maxDistance;
        for (int dx = -r; dx <= r; dx += 2)
        for (int dy = -3; dy <= 3; dy++)
        for (int dz = -r; dz <= r; dz += 2)
        {
            tmp.Set(centerBp.X + dx, centerBp.Y + dy, centerBp.Z + dz);
            Block b = ba.GetBlock(tmp);
            if (IsMaintainableBlock(b)) candidates.Add(tmp.Copy());
        }

        if (candidates.Count == 0) return null;
        BlockPos chosen = candidates[entity.World.Rand.Next(candidates.Count)];
        return chosen.ToVec3d().Add(0.5, 0.0, 0.5);
    }

    protected override bool InteractionPossible()
    {
        if (targetPos == null) return false;
        return entity.Pos.XYZ.SquareDistanceTo(targetPos) < 4.0;
    }

    public override void StartExecute()
    {
        base.StartExecute();
        hammerStartedAtMs = 0L;
    }

    public override bool ContinueExecute(float dt)
    {
        if (!targetReached) return base.ContinueExecute(dt);

        if (hammerStartedAtMs == 0L) hammerStartedAtMs = entity.World.ElapsedMilliseconds;

        entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);

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

        return entity.World.ElapsedMilliseconds - hammerStartedAtMs < hammerDurationMs;
    }

    public override void FinishExecute(bool cancelled)
    {
        entity.AnimManager.StopAnimation(HammerAnimCode);
        base.FinishExecute(cancelled);
    }

    protected override void ApplyInteractionEffect() { }

    private static bool IsMaintainableBlock(Block b)
    {
        string path = b?.Code?.Path;
        if (path == null) return false;
        return path.Contains("fence") || path.Contains("gate") || path.Contains("door")
            || path.Contains("planks") || path.StartsWith("workstation-");
    }
}
