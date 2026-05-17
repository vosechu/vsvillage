using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

// Herbalist cosmetic task: walks to her workstation and plays a hoe-till animation
// to simulate her tending her work area.
// Originally this filled a flowerpot in her room, but the flowerpot mechanic was
// scrapped (1.22-era horsetail block resolution proved unreliable). The class name
// and registration code (villagerfillflowerpot) are kept intact to avoid breaking
// task wiring; the implementation is purely workstation-tending now.
public class AiTaskVillagerFillFlowerpot : AiTaskGotoAndInteract
{
    public AiTaskVillagerFillFlowerpot(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        interactAnim = new AnimationMetaData
        {
            Code = "hoe-till",
            Animation = "hoe-till",
            AnimationSpeed = 1f,
            BlendMode = EnumAnimationBlendMode.Average
        }.Init();
    }

    protected override Vec3d GetTargetPos()
    {
        if (!IsHerbalist()) return null;

        BlockPos ws = entity.GetBehavior<EntityBehaviorVillager>()?.Workstation;
        if (ws == null) return null;

        return ws.ToVec3d().Add(0.5, 0.0, 0.5);
    }

    protected override void ApplyInteractionEffect()
    {
        // Pure cosmetic - the hoe-till animation runs via interactAnim on arrival.
        // No mutation, no inventory changes.
    }

    public override void FinishExecute(bool cancelled)
    {
        entity.AnimManager.StopAnimation("hoe-till");
        base.FinishExecute(cancelled);
    }

    private bool IsHerbalist()
        => entity?.Code?.Path?.EndsWith("-herbalist") == true;
}
