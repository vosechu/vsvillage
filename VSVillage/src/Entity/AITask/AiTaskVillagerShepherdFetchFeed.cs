using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// Shepherd fetch leg: withdraw the highest-PRIORITY feed a pen's animal will actually EAT, from a
/// village container, bounded to the trough's free capacity. Only fires for a trough that both needs
/// feed AND has a suitable consumer nearby (no animal → no need → no hoarding). The priority + diet
/// selection lives in <see cref="ShepherdFeeding.ChooseFeedSlot"/> (+ pure <see cref="AnimalFeedPriority"/>).
/// </summary>
public class AiTaskVillagerShepherdFetchFeed : AiTaskGotoAndTransact
{
    private BlockEntityTrough refTrough;                 // representative needy pen trough
    private ShepherdFeeding.ServedAnimal served;         // its consumer's static (code, diet)

    // Trough↔animal proximity: an animal within this range of the trough is "in the pen" it serves.
    private const float PenRadius = 8f;

    public AiTaskVillagerShepherdFetchFeed(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig) { }

    // Override targeting (not ShouldExecute) so the POI query runs on the base's throttled,
    // cooldown-gated, suffix-gated cadence — not every tick. Shepherd-gating comes from the JSON
    // onlyForEntitySuffix "-shepherd". The base refuses unless the carry slot is empty.
    protected override Vec3d GetTargetPos()
    {
        refTrough = FindNeedyServableTrough();
        if (refTrough == null) return null;                        // no needy trough with a consumer → no fetch
        served = ShepherdFeeding.FindServed(entity.World, refTrough, PenRadius);
        if (served == null) return null;                           // consumer vanished between filter and here
        return base.GetTargetPos();                                // ranks containers via WantsItem, claims, approach
    }

    // Nearest trough that BOTH needs feed AND has a suitable served animal. Excluding consumerless
    // troughs is the fix for the "fill a pen nobody lives in → permanent brick" path (no need = don't fetch).
    private BlockEntityTrough FindNeedyServableTrough()
    {
        POIRegistry poiReg = entity.Api.ModLoader.GetModSystem<POIRegistry>();
        if (poiReg == null) return null;
        return poiReg.GetNearestPoi(entity.Pos.XYZ, maxDistance,
            poi => ShepherdTroughs.IsTroughPoi(poi)
                && poi is BlockEntityTrough t && ShepherdTroughs.NeedsFeed(t)
                && ShepherdFeeding.FindServed(entity.World, t, PenRadius) != null) as BlockEntityTrough;
    }

    // A container is worth visiting only if it holds a feed that is APPROPRIATE (all four gates) and
    // best-priority for refTrough's animal — i.e. ChooseFeedSlot finds something.
    protected override bool WantsItem(BlockEntityContainer be)
        => served != null && refTrough != null
           && ShepherdFeeding.ChooseFeedSlot(entity.World, be, refTrough, served) != null;

    // Withdraw the highest-priority appropriate feed (hay → veg → grain …), not the first slot.
    protected override ItemSlot ChooseSourceSlot(BlockEntityContainer be)
        => ShepherdFeeding.ChooseFeedSlot(entity.World, be, refTrough, served);

    // Cap the withdrawal at the representative trough's free capacity (thrash mitigation).
    protected override int WithdrawNeed(ItemSlot src)
    {
        if (refTrough == null) return src.StackSize;
        if (!ShepherdTroughs.AcceptsItem(refTrough, src.Itemstack)) return 0;
        ItemSlot troughSlot = refTrough.Inventory?[0];
        ContentConfig cfg = ItemSlotTrough.getContentConfig(entity.World, refTrough.contentConfigs, src);
        if (cfg == null) return 0;   // this source isn't feed for the trough
        int current = (troughSlot == null || troughSlot.Empty) ? 0 : troughSlot.StackSize;
        return VillagerInventoryMath.TroughFreeCapacity(current, cfg.MaxFillLevels, cfg.QuantityPerFillLevel);
    }
}
