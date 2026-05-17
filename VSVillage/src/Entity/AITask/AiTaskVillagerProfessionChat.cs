using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

// Daytime social filler: villager walks over to ANOTHER villager of the
// same profession for a brief greet animation. Different from
// villagersocialize (which picks any villager) - this one favours
// "shepherds chat with shepherds, farmers chat with farmers" so the village
// reads like people swap notes with their peers.
// Soldiers/archers are excluded by entity-suffix check; they have their
// own combat priorities and shouldn't be drifting off to chat.
// JSON config:
//   maxdistance  - search radius (default 18).
//   interact     - animation code on arrival (default "welcome").
public class AiTaskVillagerProfessionChat : AiTaskGotoAndInteract
{
    public AiTaskVillagerProfessionChat(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        string interactCode = taskConfig["interact"].AsString("welcome");
        interactAnim = new AnimationMetaData
        {
            Code           = interactCode,
            Animation      = interactCode,
            AnimationSpeed = 1f,
            BlendMode      = EnumAnimationBlendMode.Average,
            EaseInSpeed    = 3f,
            EaseOutSpeed   = 3f
        }.Init();
    }

    protected override Vec3d GetTargetPos()
    {
        // Soldier/archer exclusion happens via excludeEntitySuffixes in JSON; the base SuffixGatePasses() in ShouldExecute filters them before we get here.
        EntityBehaviorVillager myBeh = entity.GetBehavior<EntityBehaviorVillager>();
        if (myBeh == null) return null;
        EnumVillagerProfession myProfession = myBeh.Profession;

        Vec3d myPos = entity.Pos.XYZ;
        Entity[] nearby = entity.World.GetEntitiesAround(myPos, maxDistance, 4f, e =>
        {
            if (e == entity || !e.Alive) return false;
            EntityBehaviorVillager beh = e.GetBehavior<EntityBehaviorVillager>();
            if (beh == null) return false;
            return beh.Profession == myProfession;
        });

        if (nearby == null || nearby.Length == 0) return null;

        // Nearest match.
        Entity best = null;
        double bestDistSq = double.MaxValue;
        foreach (Entity c in nearby)
        {
            double dsq = myPos.SquareDistanceTo(c.Pos.XYZ);
            // Avoid pathing onto the same tile as the peer.
            if (dsq < 1.0) continue;
            if (dsq < bestDistSq)
            {
                bestDistSq = dsq;
                best = c;
            }
        }

        return best?.Pos.XYZ;
    }

    protected override void ApplyInteractionEffect()
    {
        // Welcome anim plays via interactAnim on arrival. Nothing else to do.
    }

    public override void FinishExecute(bool cancelled)
    {
        if (interactAnim != null) entity.AnimManager.StopAnimation(interactAnim.Code);
        base.FinishExecute(cancelled);
    }
}
