using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

// Baker task: walk to the oven and manage the bread cycle.
// - Pulls any finished bread out.
// - Loads fresh dough into empty slots, but only if the oven is hot enough.
// Vanilla requires the oven to be at minBakeTemp before dough is loaded (cold
// pre-loading just doesn't bake), so the temperature gate stays. Pulling and
// loading happen in the same visit since the baker is already at the oven -
// avoids double round-trips and keeps the bread cycle tight.
// Fueling + ignition is a separate task (AiTaskVillagerBakerTendOven) so each
// phase has its own cooldown.
public class AiTaskVillagerBakerCollectBread : AiTaskVillagerBakerBase
{
    private BlockPos ovenPos;

    public AiTaskVillagerBakerCollectBread(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
    }

    protected override Vec3d GetTargetPos()
    {
        if (!IsBaker()) return null;

        BlockPos ws = entity.GetBehavior<EntityBehaviorVillager>()?.Workstation;
        if (ws == null) return null;

        BlockEntityOven oven = FindOven(ws);
        if (oven == null) return null;

        // Fire if there's finished bread to collect OR the oven is hot enough to
        // accept fresh dough into empty slots.
        bool hasFinished = HasFinishedBread(oven);
        bool canLoadDough = oven.ovenTemperature >= minBakeTemp && HasEmptyBakeableSlot(oven);
        if (!hasFinished && !canLoadDough) return null;

        ovenPos = oven.Pos.Copy();
        return oven.Pos.ToVec3d().Add(0.5, 1.0, 0.5);
    }

    protected override bool InteractionPossible()
    {
        if (targetPos == null) return false;
        double dx = entity.Pos.X - targetPos.X;
        double dz = entity.Pos.Z - targetPos.Z;
        return dx * dx + dz * dz < 49.0;
    }

    protected override void ApplyInteractionEffect()
    {
        if (!IsBaker() || ovenPos == null) return;

        BlockEntityOven oven = entity.World.BlockAccessor.GetBlockEntity<BlockEntityOven>(ovenPos);
        if (oven == null) return;

        entity.AnimManager.StartAnimation(new AnimationMetaData
        {
            Animation = "hoe-till",
            Code = "hoe-till",
            AnimationSpeed = 1.2f,
            BlendMode = EnumAnimationBlendMode.Average
        }.Init());

        // Collect any finished bread first (frees up slots for the dough load below).
        CollectFinishedBread(oven);

        // Load fresh dough into empty slots if the oven is hot enough.
        // Re-check oven state after collecting - the oven is now in its post-collect
        // state, slots may have just opened up.
        if (oven.ovenTemperature >= minBakeTemp && HasEmptyBakeableSlot(oven))
        {
            TryLoadDough(oven);
        }

        entity.World.RegisterCallback(_ => entity.AnimManager.StopAnimation("hoe-till"), 1200);
    }

    public override void FinishExecute(bool cancelled)
    {
        entity.AnimManager.StopAnimation("hoe-till");
        base.FinishExecute(cancelled);
        ovenPos = null;
    }
}
