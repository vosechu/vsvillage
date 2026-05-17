using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

// Farmer ambient hoe-till. Cosmetic clone of cultivatecrops minus the crop advance, lower priority so real farming wins.
// Uses POIRegistry.GetNearestPoi (indexed, cheap) instead of the older r*r*5 manual grid scan. JSON: maxdistance.
public class AiTaskVillagerHoeTilling : AiTaskGotoAndInteract
{
    private BlockEntityFarmland nearestFarmland;

    public AiTaskVillagerHoeTilling(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        // Replace base "nod" with hoe-till so arrival plays the farming anim.
        interactAnim = new AnimationMetaData
        {
            Code           = "hoe-till",
            Animation      = "hoe-till",
            AnimationSpeed = 1f,
            BlendMode      = EnumAnimationBlendMode.Average
        }.Init();
    }

    protected override Vec3d GetTargetPos()
    {
        if (!IsFarmer()) return null;

        // Use the same POI lookup that cultivate uses. The 0.2 random chance
        // in the predicate spreads the farmer's picks across multiple tiles
        // each cycle so she doesn't stand at the same square forever.
        nearestFarmland = entity.Api.ModLoader.GetModSystem<POIRegistry>()
            .GetNearestPoi(entity.Pos.XYZ, maxDistance, isValidFarmland) as BlockEntityFarmland;
        if (nearestFarmland == null) return null;

        return nearestFarmland.Pos.ToVec3d().Add(0.5, 1.0, 0.5);
    }

    protected override void ApplyInteractionEffect()
    {
        // Pure cosmetic - the hoe-till anim plays via interactAnim on arrival.
        // No crop advance, no farmland mutation. Real cultivation lives in
        // villagercultivatecrops.
    }

    private bool isValidFarmland(IPointOfInterest poi)
    {
        // Any farmland POI qualifies for ambient hoeing. No crop required,
        // no ripe-crop exclusion - we're not changing state. The random gate
        // (~20%) spreads selection so the farmer rotates between tiles.
        return poi is BlockEntityFarmland && entity.World.Rand.NextDouble() < 0.2;
    }

    public override void FinishExecute(bool cancelled)
    {
        entity.AnimManager.StopAnimation("hoe-till");
        base.FinishExecute(cancelled);
    }

    private bool IsFarmer()
        => entity?.Code?.Path?.EndsWith("-farmer") == true;
}
