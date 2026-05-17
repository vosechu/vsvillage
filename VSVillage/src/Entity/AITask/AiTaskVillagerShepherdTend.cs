using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

// Shepherd cosmetic kneel-grooming. Anchored on workstation so she only fusses over animals inside the pen, no wild-animal fallback.
public class AiTaskVillagerShepherdTend : AiTaskGotoAndInteract
{
    private float minRange;

    // Livestock code prefixes; matched via FirstCodePart so variants (-baby, -male, etc.) all qualify.
    private static readonly string[] LivestockPrefixes =
    {
        "sheep", "ram", "ewe", "lamb",
        "cow",   "bull",  "calf",
        "chicken", "hen",  "rooster", "chick",
        "pig",     "sow",  "piglet",
        "goat",
        "alpaca", "llama"
    };

    public AiTaskVillagerShepherdTend(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        minRange = taskConfig["minRange"].AsFloat(1f);

        // Slow kneeling anim so it reads as deliberate grooming. Easing matches base interactAnim.
        interactAnim = new AnimationMetaData
        {
            Code           = "kneellaundry",
            Animation      = "kneellaundry",
            AnimationSpeed = 0.6f,
            BlendMode      = EnumAnimationBlendMode.Average,
            EaseInSpeed    = 3f,
            EaseOutSpeed   = 3f
        }.Init();
    }

    protected override Vec3d GetTargetPos()
    {
        if (!IsShepherd()) return null;

        // Anchor on the pen via workstation. No workstation, no tending. Never fall back to entity.Pos (would tend wild animals).
        BlockPos ws = entity.GetBehavior<EntityBehaviorVillager>()?.Workstation;
        if (ws == null) return null;
        Vec3d penCentre = ws.ToVec3d().Add(0.5, 0.0, 0.5);

        float searchRadius = maxDistance;
        Entity best = null;
        double bestDistSq = double.MaxValue;

        // Scan around the pen centre, not the shepherd. Animals inside the
        // pen will be within maxDistance of the workstation; wild animals in
        // the surrounding forest fall outside this search and never qualify.
        Entity[] candidates = entity.World.GetEntitiesAround(penCentre, searchRadius, searchRadius,
            e => e != null && e.Alive && IsLivestock(e));

        Vec3d myPos = entity.Pos.XYZ;
        foreach (Entity c in candidates)
        {
            double dsq = myPos.SquareDistanceTo(c.Pos.XYZ);
            if (dsq < minRange * minRange) continue;
            if (dsq < bestDistSq)
            {
                bestDistSq = dsq;
                best = c;
            }
        }

        if (best == null) return null;

        // Target a tile next to the animal (one block offset toward us) so
        // we kneel at its side instead of walking on top of it.
        Vec3d animalPos = best.Pos.XYZ;
        double dx = myPos.X - animalPos.X;
        double dz = myPos.Z - animalPos.Z;
        double dist = Math.Sqrt(dx * dx + dz * dz);
        if (dist < 0.5) return animalPos.Clone();
        double scale = 1.0 / dist;
        return new Vec3d(animalPos.X + dx * scale, animalPos.Y, animalPos.Z + dz * scale);
    }

    protected override void ApplyInteractionEffect()
    {
        // Pure cosmetic - the kneellaundry anim plays via interactAnim on
        // arrival. No state change, no mutation on the animal.
    }

    public override void FinishExecute(bool cancelled)
    {
        entity.AnimManager.StopAnimation("kneellaundry");
        base.FinishExecute(cancelled);
    }

    private bool IsShepherd()
        => entity?.Code?.Path?.EndsWith("-shepherd") == true;

    private static bool IsLivestock(Entity e)
    {
        string firstPart = e.Code?.FirstCodePart();
        if (string.IsNullOrEmpty(firstPart)) return false;
        foreach (string prefix in LivestockPrefixes)
        {
            if (firstPart == prefix) return true;
        }
        return false;
    }
}
