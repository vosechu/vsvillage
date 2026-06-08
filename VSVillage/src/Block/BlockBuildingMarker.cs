using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VsVillage;

// Sign-phase marker that the player places. Holds a size bucket (small/medium/large) via its
// "size" variant. Right-click opens the building catalog GUI. Placement validity: every cell
// of the marker's bucket footprint must be IsClearOrDiggable.
public class BlockBuildingMarker : Block
{
    public EnumBuildingSize SizeBucket
    {
        get
        {
            string s = Variant["size"] ?? "small";
            return s switch
            {
                "medium" => EnumBuildingSize.Medium,
                "large" => EnumBuildingSize.Large,
                _ => EnumBuildingSize.Small,
            };
        }
    }

    // Footprint validity is checked against the bucket's maximum, not a specific schematic.
    // Player picks the schematic in the GUI AFTER placement; any bucket-valid schematic will fit.
    public (int xz, int y) BucketBounds => SizeBucket switch
    {
        EnumBuildingSize.Medium => (BuildingCatalog.MediumMaxXZ, BuildingCatalog.MediumMaxY),
        EnumBuildingSize.Large => (BuildingCatalog.LargeMaxXZ, BuildingCatalog.LargeMaxY),
        _ => (BuildingCatalog.SmallMaxXZ, BuildingCatalog.SmallMaxY),
    };

    // Offsets (dx, dy, dz) relative to the marker's own cell. Marker sits at the south-edge
    // centre so the build volume extends north of it and the player keeps a clear south-side
    // exit. X is centred (slight east bias on even widths). Y starts at marker level.
    public (int minDx, int maxDx, int minDy, int maxDy, int minDz, int maxDz) FootprintBounds
    {
        get
        {
            (int xzCap, int yCap) = BucketBounds;
            int halfL = xzCap / 2;
            int halfR = (xzCap - 1) - halfL;
            return (-halfL, +halfR, 0, yCap - 1, -(xzCap - 1), 0);
        }
    }

    // Auto-open the catalog GUI for the placing player right after placement so they don't have
    // to right-click separately. Defer 100ms so the BE has time to initialise.
    public override bool DoPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ItemStack byItemStack)
    {
        bool ok = base.DoPlaceBlock(world, byPlayer, blockSel, byItemStack);
        if (!ok) return false;
        if (world.Side == EnumAppSide.Client && world.Api is ICoreClientAPI capi
            && byPlayer != null && byPlayer.PlayerUID == capi.World?.Player?.PlayerUID)
        {
            BlockPos pos = blockSel.Position.Copy();
            world.RegisterCallback((dt) =>
            {
                BlockEntityBuildingMarker be = world.BlockAccessor.GetBlockEntity<BlockEntityBuildingMarker>(pos);
                if (be == null) return;
                BuildingCatalog catalog = capi.ModLoader.GetModSystem<BuildingCatalog>();
                if (catalog == null) return;
                new BuildingMarkerGui(capi, pos, this, be, catalog).TryOpen();
            }, 100);
        }
        return true;
    }

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (world.Api is ICoreClientAPI capi)
        {
            BlockPos pos = blockSel.Position.Copy();
            world.RegisterCallback((dt) =>
            {
                BlockEntityBuildingMarker be = world.BlockAccessor.GetBlockEntity<BlockEntityBuildingMarker>(pos);
                if (be == null) return;
                BuildingCatalog catalog = capi.ModLoader.GetModSystem<BuildingCatalog>();
                if (catalog == null) return;
                new BuildingMarkerGui(capi, pos, this, be, catalog).TryOpen();
            }, 30);
            return false;
        }
        return true;
    }

    // Always indestructible in survival. Right-click to remove or cancel via the GUI.
    public override bool OnBlockBrokenWith(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, float dropQuantityMultiplier = 1f)
    {
        if (byEntity is EntityPlayer ep && ep.Player?.WorldData?.CurrentGameMode == EnumGameMode.Creative)
        {
            return base.OnBlockBrokenWith(world, byEntity, itemslot, blockSel, dropQuantityMultiplier);
        }
        if (world.Side == EnumAppSide.Server && byEntity is EntityPlayer ep2)
        {
            BlockEntityBuildingMarker be = world.BlockAccessor.GetBlockEntity<BlockEntityBuildingMarker>(blockSel.Position);
            string msgKey = (be?.ConstructionStarted == true) ? "vsvillage:marker-protected" : "vsvillage:marker-remove-via-menu";
            (ep2.Player as IServerPlayer)?.SendIngameError(msgKey, Lang.Get(msgKey));
        }
        return false;
    }

    private static int CountGears(IPlayer player)
    {
        int total = 0;
        IPlayerInventoryManager mgr = player.InventoryManager;
        foreach (string invName in new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName })
        {
            IInventory inv = mgr.GetOwnInventory(invName);
            if (inv == null) continue;
            foreach (ItemSlot slot in inv)
            {
                if (slot.Empty) continue;
                if (slot.Itemstack?.Collectible?.Code?.Path == "gear-rusty") total += slot.StackSize;
            }
        }
        return total;
    }

    public override bool CanPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref string failureCode)
    {
        if (!base.CanPlaceBlock(world, byPlayer, blockSel, ref failureCode)) return false;

        BlockPos markerPos = blockSel.Position;
        var b = FootprintBounds;

        for (int dy = b.minDy; dy <= b.maxDy; dy++)
            for (int dx = b.minDx; dx <= b.maxDx; dx++)
                for (int dz = b.minDz; dz <= b.maxDz; dz++)
                {
                    BlockPos check = new BlockPos(
                        markerPos.X + dx,
                        markerPos.Y + dy,
                        markerPos.Z + dz,
                        markerPos.dimension);
                    Block existing = world.BlockAccessor.GetBlock(check);
                    if (!IsClearOrDiggable(existing))
                    {
                        failureCode = "vsvillage:need-clear-building-footprint";
                        return false;
                    }
                }
        return true;
    }

    // Cells the builder can safely demolish: air, soil, plants, sand, gravel, stone, snow.
    public static bool IsClearOrDiggable(Block b)
    {
        if (b == null || b.Id == 0) return true;
        EnumBlockMaterial m = b.BlockMaterial;
        return m == EnumBlockMaterial.Soil
            || m == EnumBlockMaterial.Stone
            || m == EnumBlockMaterial.Sand
            || m == EnumBlockMaterial.Gravel
            || m == EnumBlockMaterial.Plant
            || m == EnumBlockMaterial.Snow;
    }

    // Per-cell ARGB. Alpha 80 gives a "translucent block face overlay" that mirrors WorldEdit.
    private static readonly int ColorValid = ColorUtil.ToRgba(80, 80, 240, 80);
    private static readonly int ColorBlocked = ColorUtil.ToRgba(80, 240, 80, 80);
    private const int HighlightSlotId = 332;
    private const long PreviewIntervalMs = 200L;
    private long lastPreviewMs;

    public override void OnHeldIdle(ItemSlot slot, EntityAgent byEntity)
    {
        base.OnHeldIdle(slot, byEntity);
        if (api?.Side != EnumAppSide.Client) return;

        IPlayer byPlayer = (byEntity as EntityPlayer)?.Player;
        if (byPlayer == null) return;
        BlockSelection bs = byPlayer.CurrentBlockSelection;
        if (bs == null) return;

        long now = api.World.ElapsedMilliseconds;
        if (now - lastPreviewMs < PreviewIntervalMs) return;
        lastPreviewMs = now;

        BlockPos markerPos = bs.Position.AddCopy(bs.Face);
        var b = FootprintBounds;
        IBlockAccessor ba = api.World.BlockAccessor;

        int cellCount = (b.maxDx - b.minDx + 1) * (b.maxDy - b.minDy + 1) * (b.maxDz - b.minDz + 1);
        List<BlockPos> cells = new List<BlockPos>(cellCount);
        List<int> colors = new List<int>(cellCount);

        for (int dy = b.minDy; dy <= b.maxDy; dy++)
            for (int dx = b.minDx; dx <= b.maxDx; dx++)
                for (int dz = b.minDz; dz <= b.maxDz; dz++)
                {
                    BlockPos cell = new BlockPos(
                        markerPos.X + dx,
                        markerPos.Y + dy,
                        markerPos.Z + dz,
                        markerPos.dimension);
                    cells.Add(cell);
                    colors.Add(IsClearOrDiggable(ba.GetBlock(cell)) ? ColorValid : ColorBlocked);
                }

        // Arbitrary mode treats each BlockPos as its own cell. Cubes mode would pair them
        // into corner pairs and reject zero-extent pairs, which is what produced the banding.
        api.World.HighlightBlocks(byPlayer, HighlightSlotId, cells, colors,
            EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
    }
}