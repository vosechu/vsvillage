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
    private BlockPos claimedTroughPos;                   // held through the fill leg so 2 shepherds split pens

    // Trough↔animal proximity: an animal within this range of the trough is "in the pen" it serves.
    private const float PenRadius = 8f;

    public AiTaskVillagerShepherdFetchFeed(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig) { }

    // Override targeting (not ShouldExecute) so the POI query runs on the base's throttled,
    // cooldown-gated, suffix-gated cadence — not every tick. Shepherd-gating comes from the JSON
    // onlyForEntitySuffix "-shepherd". The base refuses unless the carry slot is empty.
    protected override Vec3d GetTargetPos()
    {
        // Free any claim from a prior attempt before re-choosing — symmetric to the base container claim
        // (AiTaskGotoAndTransact.GetTargetPos) and the fill leg. Without this, a query that re-targets or
        // fails below would orphan the pen's claim under our id until the 120s expiry, blocking the other
        // shepherd. Safe for the hold-through-fill path: a successful fetch already nulled this field.
        ReleaseTroughClaim();

        refTrough = FindNeedyServableTrough();
        if (refTrough == null) return null;                        // no needy trough with a consumer → no fetch
        POIRegistry poiReg = entity.Api.ModLoader.GetModSystem<POIRegistry>();
        served = ShepherdFeeding.FindServed(entity.World, poiReg, refTrough, PenRadius);
        if (served == null) return null;                           // consumer vanished between filter and here

        // Claim the trough we're provisioning and HOLD it through the fill leg, so a second shepherd
        // fetches for a different pen instead of piling onto this one (and mis-routing its feed).
        claimedTroughPos = refTrough.Pos.Copy();
        VsVillage.TroughClaims.TryClaim(claimedTroughPos, entity.EntityId, entity.World.ElapsedMilliseconds);

        Vec3d approach = base.GetTargetPos();                      // ranks containers via WantsItem, claims, approach
        if (approach == null) ReleaseTroughClaim();                // no reachable feed container → don't hold the pen
        return approach;
    }

    // Nearest trough that needs feed, has a suitable served animal, AND isn't already held by another
    // shepherd. Excluding consumerless troughs prevents the "fill a pen nobody lives in → brick" path;
    // excluding other-held troughs is the 2-shepherd conflict resolution (each provisions its own pen).
    private BlockEntityTrough FindNeedyServableTrough()
    {
        POIRegistry poiReg = entity.Api.ModLoader.GetModSystem<POIRegistry>();
        if (poiReg == null) return null;
        long now = entity.World.ElapsedMilliseconds;
        return poiReg.GetNearestPoi(entity.Pos.XYZ, maxDistance,
            poi => ShepherdTroughs.IsTroughPoi(poi)
                && poi is BlockEntityTrough t && ShepherdTroughs.NeedsFeed(t)
                && !VsVillage.TroughClaims.IsClaimedByOther(t.Pos, entity.EntityId, now)
                && ShepherdFeeding.FindServed(entity.World, poiReg, t, PenRadius) != null) as BlockEntityTrough;
    }

    public override void FinishExecute(bool cancelled)
    {
        base.FinishExecute(cancelled);
        // Keep the registry claim if we actually picked up feed — the fill leg needs it and will release it
        // (we just drop our local handle). Otherwise (cancelled, or arrived to an empty chest) free the pen.
        if (claimedTroughPos != null)
        {
            EntityBehaviorVillager bh = entity.GetBehavior<EntityBehaviorVillager>();
            if (cancelled || bh == null || bh.IsCarryEmpty) ReleaseTroughClaim();
            else claimedTroughPos = null;
        }
    }

    private void ReleaseTroughClaim()
    {
        if (claimedTroughPos != null)
        {
            VsVillage.TroughClaims.Release(claimedTroughPos, entity.EntityId);
            claimedTroughPos = null;
        }
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
