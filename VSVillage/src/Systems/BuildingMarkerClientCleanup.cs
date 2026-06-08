using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VsVillage;

// Client-only. Clears the BuildingMarker highlight overlay once the player switches off the
// marker item. Without this, HighlightBlocks lingers in space forever.
public class BuildingMarkerClientCleanup : ModSystem
{
    private ICoreClientAPI _capi;
    private bool _highlightActive;
    private const int HighlightSlotId = 332;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        base.StartClientSide(api);
        _capi = api;
        api.Event.RegisterGameTickListener(OnTick, 250);
    }

    private void OnTick(float dt)
    {
        IPlayer player = _capi.World.Player;
        if (player?.InventoryManager?.ActiveHotbarSlot == null) return;
        ItemStack stack = player.InventoryManager.ActiveHotbarSlot.Itemstack;
        bool holdingMarker = stack?.Collectible is BlockBuildingMarker;
        if (holdingMarker)
        {
            _highlightActive = true;
            return;
        }
        if (_highlightActive)
        {
            _capi.World.HighlightBlocks(player, HighlightSlotId, new List<BlockPos>(), new List<int>());
            _highlightActive = false;
        }
    }
}
