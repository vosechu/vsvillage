using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// Trough classification shared by the shepherd's feed-fetch and trough-fill tasks so they agree
/// on which troughs need feed and what feed a trough accepts. World-touching (not unit-testable);
/// the pure capacity math lives in <see cref="VillagerInventoryMath"/>.
/// </summary>
public static class ShepherdTroughs
{
    // Large BlockEntityTrough, or any BE whose block code path contains "trough" (mini bowl, variants).
    public static bool IsTroughPoi(IPointOfInterest poi) => poi is BlockEntity be && IsTrough(be);

    // A trough of any size (large BlockEntityTrough or a variant whose block code path contains
    // "trough"). A trough is a feed SINK, not storage — the shepherd fills it but must never fetch
    // FROM one, even though a trough is itself a BlockEntityContainer and would otherwise be enrolled
    // as a village source container by Village.ScanContainers.
    public static bool IsTrough(BlockEntity be) =>
        be is BlockEntityTrough || be?.Block?.Code?.Path?.Contains("trough") == true;

    public static BlockPos GetTroughPos(IPointOfInterest poi) => (poi as BlockEntity)?.Pos;

    // Empty, or the current feed is below the trough's real capacity (MaxFillLevels*QuantityPerFillLevel).
    // NOT the collectible's MaxStackSize — a full trough (e.g. grain 16/16) must not read as needy.
    public static bool NeedsFeed(BlockEntityTrough trough)
    {
        ItemSlot slot = trough?.Inventory?[0];
        if (slot == null || slot.Empty) return true;
        ContentConfig cfg = ItemSlotTrough.getContentConfig(trough.Api.World, trough.contentConfigs, slot);
        return cfg != null && slot.StackSize < cfg.MaxFillLevels * cfg.QuantityPerFillLevel;
    }

    // Can this trough take this item RIGHT NOW? Delegates to ItemSlotTrough.CanTakeFrom -> troughable,
    // which enforces: valid feed type, same-food-or-empty (single-food troughs), and capacity remaining.
    public static bool AcceptsItem(BlockEntityTrough trough, ItemStack stack)
    {
        if (trough == null || stack == null) return false;
        ItemSlot slot = trough.Inventory?[0];
        return slot != null && slot.CanTakeFrom(new DummySlot(stack));
    }
}
