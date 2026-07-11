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
    public static bool IsTroughPoi(IPointOfInterest poi)
    {
        if (poi is BlockEntityTrough) return true;
        if (poi is BlockEntity be) return be.Block?.Code?.Path?.Contains("trough") == true;
        return false;
    }

    public static BlockPos GetTroughPos(IPointOfInterest poi) => (poi as BlockEntity)?.Pos;

    // Empty, or the feed slot is below the stack cap (room for more). Mirrors the old isValidTrough.
    public static bool NeedsFeed(BlockEntityTrough trough)
    {
        ItemSlot slot = trough?.Inventory?[0];
        if (slot == null || slot.Empty) return true;
        return slot.StackSize < slot.Itemstack.Collectible.MaxStackSize;
    }

    // Does this trough's content config accept this item type? (Fetch-time "is this feed" filter.)
    public static bool AcceptsItem(IWorldAccessor world, BlockEntityTrough trough, ItemStack stack)
    {
        if (trough == null || stack == null) return false;
        return ItemSlotTrough.getContentConfig(world, trough.contentConfigs, new DummySlot(stack)) != null;
    }
}
