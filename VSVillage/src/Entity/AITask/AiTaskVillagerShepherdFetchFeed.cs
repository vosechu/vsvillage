using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// Shepherd fetch leg: withdraw feed a pen trough will accept, bounded to the troughs' free
/// capacity so we don't over-hoard. Only fires when a pen trough actually needs feed.
/// </summary>
public class AiTaskVillagerShepherdFetchFeed : AiTaskGotoAndTransact
{
    private BlockEntityTrough refTrough;   // representative pen trough for acceptance + need

    public AiTaskVillagerShepherdFetchFeed(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig) { }

    // Override targeting (not ShouldExecute) so the POI query runs on the base's throttled,
    // cooldown-gated, suffix-gated cadence — not every tick. Shepherd-gating comes from the
    // JSON onlyForEntitySuffix "-shepherd" (SuffixGatePasses runs before GetTargetPos). The base
    // AiTaskGotoAndTransact.ShouldExecute already refuses unless the carry slot is empty.
    protected override Vec3d GetTargetPos()
    {
        refTrough = FindNeedyTrough();
        if (refTrough == null) return null;   // no pen trough needs feed -> no fetch (no hoarding)
        return base.GetTargetPos();           // ranks containers via WantsItem, claims, returns approach
    }

    private BlockEntityTrough FindNeedyTrough()
    {
        POIRegistry poiReg = entity.Api.ModLoader.GetModSystem<POIRegistry>();
        if (poiReg == null) return null;
        return poiReg.GetNearestPoi(entity.Pos.XYZ, maxDistance,
            poi => ShepherdTroughs.IsTroughPoi(poi)
                && poi is BlockEntityTrough t && ShepherdTroughs.NeedsFeed(t)) as BlockEntityTrough;
    }

    // Fetch only items the representative trough accepts.
    protected override bool WantsItem(BlockEntityContainer be)
    {
        if (be.Inventory == null || refTrough == null) return false;
        foreach (ItemSlot slot in be.Inventory)
        {
            if (slot.Empty) continue;
            if (ShepherdTroughs.AcceptsItem(entity.World, refTrough, slot.Itemstack)) return true;
        }
        return false;
    }

    // Cap the withdrawal at the representative trough's free capacity (thrash mitigation).
    protected override int WithdrawNeed(ItemSlot src)
    {
        if (refTrough == null) return src.StackSize;
        ItemSlot troughSlot = refTrough.Inventory?[0];
        ContentConfig cfg = ItemSlotTrough.getContentConfig(entity.World, refTrough.contentConfigs, src);
        if (cfg == null) return 0;   // this source isn't feed for the trough
        int current = (troughSlot == null || troughSlot.Empty) ? 0 : troughSlot.StackSize;
        return VillagerInventoryMath.TroughFreeCapacity(current, cfg.MaxFillLevels, cfg.QuantityPerFillLevel);
    }
}
