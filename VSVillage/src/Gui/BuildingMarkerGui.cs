using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace VsVillage;

// Reads catalog and BE state directly client-side. Only Begin Build and Cancel send packets.
public class BuildingMarkerGui : GuiDialog
{
    public override string ToggleKeyCombinationCode => null;

    private readonly BlockPos _markerPos;
    private readonly BlockBuildingMarker _markerBlock;
    private readonly BlockEntityBuildingMarker _be;
    private readonly BuildingCatalog _catalog;
    private readonly List<BuildingDefinition> _choices;
    private string _selectedId;
    private int _rotationAngle;

    private const int RotationHighlightSlotId = 333;

    public BuildingMarkerGui(ICoreClientAPI capi, BlockPos markerPos, BlockBuildingMarker markerBlock,
        BlockEntityBuildingMarker be, BuildingCatalog catalog) : base(capi)
    {
        _markerPos = markerPos;
        _markerBlock = markerBlock;
        _be = be;
        _catalog = catalog;
        _choices = new List<BuildingDefinition>(catalog.InBucket(markerBlock.SizeBucket));
        _selectedId = _be.SelectedSchematicId ?? (_choices.Count > 0 ? _choices[0].Id : null);
        _rotationAngle = _be.RotationAngle;

        if (_be.DemolitionInProgress) ComposeDemolition();
        else if (_be.ConstructionStarted) ComposeActive();
        else ComposeSign();
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        if (!_be.ConstructionStarted) RefreshRotationOverlay();
    }

    public override void OnGuiClosed()
    {
        ClearRotationOverlay();
        base.OnGuiClosed();
    }

    private int CurrentGearCount()
    {
        int total = 0;
        IPlayerInventoryManager mgr = capi.World.Player?.InventoryManager;
        if (mgr == null) return 0;
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

    private void ComposeSign()
    {
        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        string title = Lang.Get("vsvillage:building-marker-title", _markerBlock.SizeBucket.ToString().ToLowerInvariant());
        SingleComposer = capi.Gui
            .CreateCompo("vsvillage-building-marker", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(title, OnClose)
            .BeginChildElements(bgBounds);

        if (_choices.Count == 0)
        {
            ElementBounds emptyBounds = ElementBounds.Fixed(20, 50, 420, 30);
            SingleComposer
                .AddStaticText(Lang.Get("vsvillage:building-marker-empty-bucket"),
                    CairoFont.WhiteSmallText(), emptyBounds)
                .EndChildElements()
                .Compose();
            return;
        }

        string[] values = new string[_choices.Count];
        string[] names  = new string[_choices.Count];
        for (int i = 0; i < _choices.Count; i++)
        {
            values[i] = _choices[i].Id;
            names[i]  = ResolveDisplayName(_choices[i]);
        }

        ElementBounds dropdownBounds = ElementBounds.Fixed(20, 50, 420, 30);
        ElementBounds detailBounds   = ElementBounds.Fixed(20, 100, 420, 110);
        ElementBounds rotateLabelBounds = ElementBounds.Fixed(20, 220, 200, 30);
        ElementBounds rotateBtnBounds   = ElementBounds.Fixed(240, 220, 200, 30);
        ElementBounds confirmBounds  = ElementBounds.Fixed(20, 260, 200, 30);
        ElementBounds cancelBounds   = ElementBounds.Fixed(240, 260, 200, 30);

        int idx = System.Array.IndexOf(values, _selectedId);
        SingleComposer
            .AddDropDown(values, names, idx < 0 ? 0 : idx, OnSelectionChanged, dropdownBounds, "buildingdropdown")
            .AddDynamicText(BuildDetailText(_selectedId), CairoFont.WhiteSmallText(), detailBounds, "detail")
            .AddDynamicText(RotationLabelText(), CairoFont.WhiteSmallText(), rotateLabelBounds, "rotationlabel")
            .AddButton(Lang.Get("vsvillage:building-marker-rotate"), OnRotate, rotateBtnBounds)
            .AddButton(Lang.Get("vsvillage:building-marker-confirm"), OnConfirm, confirmBounds)
            .AddButton(Lang.Get("vsvillage:building-marker-cancel"), OnRemoveMarker, cancelBounds)
            .EndChildElements()
            .Compose();
    }

    private bool OnRemoveMarker()
    {
        var net = capi.ModLoader.GetModSystem<BuildingMarkerNetwork>();
        net?.SendCancelToServer(new BuildingMarkerCancelMessage { MarkerPos = _markerPos });
        TryClose();
        return true;
    }

    private string RotationLabelText() => Lang.Get("vsvillage:building-marker-rotation-label", _rotationAngle);

    private bool OnRotate()
    {
        _rotationAngle = (_rotationAngle + 90) % 360;
        SingleComposer?.GetDynamicText("rotationlabel")?.SetNewText(RotationLabelText());
        RefreshRotationOverlay();
        return true;
    }

    // Top-down floor-plan view: every non-air cell in the schematic's Y=1 row gets a colored
    // overlay at world Pos.Y. Door cells red, glass cyan, walls by material. Air cells stay
    // empty so room shapes are visible. Rotation reorients the pattern.
    private void RefreshRotationOverlay()
    {
        if (string.IsNullOrEmpty(_selectedId)) { ClearRotationOverlay(); return; }
        BuildingDefinition def = _choices.Find(x => x.Id == _selectedId);
        if (def?.Schematic == null) { ClearRotationOverlay(); return; }

        int unrotSizeX = def.Schematic.SizeX;
        int unrotSizeZ = def.Schematic.SizeZ;
        int rotSizeX = (_rotationAngle == 90 || _rotationAngle == 270) ? unrotSizeZ : unrotSizeX;
        int rotSizeZ = (_rotationAngle == 90 || _rotationAngle == 270) ? unrotSizeX : unrotSizeZ;
        int rotHalfL = rotSizeX / 2;

        List<BlockPos> cells = new List<BlockPos>();
        List<int> colors = new List<int>();
        HashSet<int> seenIndex = new HashSet<int>();

        const int previewSchemY = 1;
        for (int i = 0; i < def.Schematic.Indices.Count; i++)
        {
            uint packed = def.Schematic.Indices[i];
            int sy = (int)((packed >> 20) & 0x3FF);
            if (sy != previewSchemY) continue;
            int sxUn = (int)(packed & 0x3FF);
            int szUn = (int)((packed >> 10) & 0x3FF);

            (int sxRot, int szRot) = RotateSchemCell(sxUn, szUn, _rotationAngle, unrotSizeX, unrotSizeZ, rotSizeX, rotSizeZ);

            int dx = sxRot - rotHalfL;
            int dz = szRot - (rotSizeZ + 1);
            int cellKey = (dx << 16) | (dz & 0xFFFF);
            if (!seenIndex.Add(cellKey)) continue;

            int storedId = def.Schematic.BlockIds[i];
            if (!def.Schematic.BlockCodes.TryGetValue(storedId, out AssetLocation code) || code == null) continue;
            Block block = capi.World.GetBlock(code);
            if (block == null || block.Id == 0) continue;

            cells.Add(new BlockPos(_markerPos.X + dx, _markerPos.Y, _markerPos.Z + dz, _markerPos.dimension));
            colors.Add(ColorForBlock(block));
        }

        capi.World.HighlightBlocks(capi.World.Player, RotationHighlightSlotId, cells, colors,
            EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
    }

    // Mirrors BlockSchematic.GetRotatedPos for EnumOrigin.BottomCenter, returning the rotated
    // (sx, sz) inside the rotated schematic frame. Y is unchanged so omitted.
    private static (int, int) RotateSchemCell(int sx, int sz, int angle, int unrotSizeX, int unrotSizeZ, int rotSizeX, int rotSizeZ)
    {
        int dx = sx - unrotSizeX / 2;
        int dz = sz - unrotSizeZ / 2;
        int dxr, dzr;
        switch (angle)
        {
            case 90:  dxr = -dz; dzr = dx;  break;
            case 180: dxr = -dx; dzr = -dz; break;
            case 270: dxr = dz;  dzr = -dx; break;
            default:  dxr = dx;  dzr = dz;  break;
        }
        return (dxr + rotSizeX / 2, dzr + rotSizeZ / 2);
    }

    private static int ColorForBlock(Block b)
    {
        string path = b.Code?.Path ?? "";
        if (path.Contains("door")) return ColorUtil.ToRgba(180, 70, 70, 230);
        if (b.BlockMaterial == EnumBlockMaterial.Glass) return ColorUtil.ToRgba(140, 220, 230, 110);
        return b.BlockMaterial switch
        {
            EnumBlockMaterial.Wood    => ColorUtil.ToRgba(160, 90, 140, 180),
            EnumBlockMaterial.Leaves  => ColorUtil.ToRgba(150, 70, 160, 70),
            EnumBlockMaterial.Stone   => ColorUtil.ToRgba(160, 150, 150, 150),
            EnumBlockMaterial.Brick   => ColorUtil.ToRgba(160, 170, 100, 70),
            EnumBlockMaterial.Ceramic => ColorUtil.ToRgba(160, 210, 180, 140),
            EnumBlockMaterial.Cloth   => ColorUtil.ToRgba(160, 220, 130, 170),
            EnumBlockMaterial.Metal   => ColorUtil.ToRgba(180, 200, 200, 220),
            _                          => ColorUtil.ToRgba(150, 200, 200, 200),
        };
    }

    private void ClearRotationOverlay()
    {
        capi.World.HighlightBlocks(capi.World.Player, RotationHighlightSlotId,
            new List<BlockPos>(), new List<int>(),
            EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
    }

    private void ComposeActive()
    {
        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        BuildingDefinition def = _choices.Find(x => x.Id == _be.SelectedSchematicId);
        string name = def != null ? ResolveDisplayName(def) : (_be.SelectedSchematicId ?? "?");
        int refund = BlockEntityBuildingMarker.CalculateRefund(_be.CostPaidGears, _be.DaysCompleted, _be.DaysRequired);

        string title = Lang.Get("vsvillage:building-marker-active-title", name);
        SingleComposer = capi.Gui
            .CreateCompo("vsvillage-building-marker-active", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(title, OnClose)
            .BeginChildElements(bgBounds);

        int pct = _be.DaysRequired > 0
            ? (int)System.Math.Round((double)_be.DaysCompleted / _be.DaysRequired * 100.0)
            : 0;
        string progress = Lang.Get("vsvillage:building-marker-progress", pct);
        string warning = Lang.Get("vsvillage:building-marker-cancel-warning", refund);

        ElementBounds progressBounds = ElementBounds.Fixed(20, 50, 420, 30);
        ElementBounds warningBounds  = ElementBounds.Fixed(20, 90, 420, 80);
        ElementBounds cancelBounds   = ElementBounds.Fixed(20, 180, 200, 30);
        ElementBounds backBounds     = ElementBounds.Fixed(240, 180, 200, 30);

        SingleComposer
            .AddStaticText(progress, CairoFont.WhiteSmallText(), progressBounds)
            .AddStaticText(warning, CairoFont.WhiteSmallText(), warningBounds)
            .AddButton(Lang.Get("vsvillage:building-marker-confirm-cancel"), OnCancelBuild, cancelBounds)
            .AddButton(Lang.Get("vsvillage:building-marker-back"), OnCloseBtn, backBounds)
            .EndChildElements()
            .Compose();
    }

    private void ComposeDemolition()
    {
        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        BuildingDefinition def = _choices.Find(x => x.Id == _be.SelectedSchematicId);
        string name = def != null ? ResolveDisplayName(def) : (_be.SelectedSchematicId ?? "?");

        string title = Lang.Get("vsvillage:building-marker-demolition-title", name);
        SingleComposer = capi.Gui
            .CreateCompo("vsvillage-building-marker-demolition", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(title, OnClose)
            .BeginChildElements(bgBounds);

        int demolitionDone = _be.DaysCompleted - _be.DemolitionDaysRemaining;
        int pct = _be.DaysCompleted > 0
            ? (int)System.Math.Round((double)demolitionDone / _be.DaysCompleted * 100.0)
            : 0;

        ElementBounds progressBounds = ElementBounds.Fixed(20, 50, 420, 30);
        ElementBounds backBounds     = ElementBounds.Fixed(20, 90, 200, 30);

        SingleComposer
            .AddStaticText(Lang.Get("vsvillage:building-marker-demolition-progress", pct), CairoFont.WhiteSmallText(), progressBounds)
            .AddButton(Lang.Get("vsvillage:building-marker-back"), OnCloseBtn, backBounds)
            .EndChildElements()
            .Compose();
    }

    private static string ResolveDisplayName(BuildingDefinition c)
    {
        if (!string.IsNullOrEmpty(c.DisplayNameLangKey))
        {
            string resolved = Lang.Get(c.DisplayNameLangKey);
            if (resolved != c.DisplayNameLangKey) return resolved;
        }
        return c.DisplayName ?? c.Id;
    }

    private string BuildDetailText(string id)
    {
        BuildingDefinition c = _choices.Find(x => x.Id == id);
        if (c == null) return "";
        int gears = CurrentGearCount();
        string canAfford = gears >= c.CostGears
            ? Lang.Get("vsvillage:building-marker-canafford")
            : Lang.Get("vsvillage:building-marker-cantafford");
        return Lang.Get("vsvillage:building-marker-detail",
            ResolveDisplayName(c),
            c.SizeX, c.SizeY, c.SizeZ,
            c.CostGears, gears,
            c.DaysRequired,
            canAfford);
    }

    private void OnSelectionChanged(string newId, bool selected)
    {
        _selectedId = newId;
        SingleComposer?.GetDynamicText("detail")?.SetNewText(BuildDetailText(_selectedId));
        RefreshRotationOverlay();
    }

    private bool OnConfirm()
    {
        if (string.IsNullOrEmpty(_selectedId)) return true;
        BuildingDefinition c = _choices.Find(x => x.Id == _selectedId);
        if (c == null) return true;
        if (CurrentGearCount() < c.CostGears)
        {
            capi.TriggerIngameError(this, "vsvillage-building-cantafford",
                Lang.Get("vsvillage:building-marker-cantafford"));
            return true;
        }
        var net = capi.ModLoader.GetModSystem<BuildingMarkerNetwork>();
        net?.SendSelectToServer(new BuildingMarkerSelectMessage
        {
            MarkerPos = _markerPos,
            SelectedId = _selectedId,
            RotationAngle = _rotationAngle,
        });
        TryClose();
        return true;
    }

    private bool OnCancelBuild()
    {
        var net = capi.ModLoader.GetModSystem<BuildingMarkerNetwork>();
        net?.SendCancelToServer(new BuildingMarkerCancelMessage { MarkerPos = _markerPos });
        TryClose();
        return true;
    }

    private void OnClose()
    {
        TryClose();
    }

    private bool OnCloseBtn()
    {
        TryClose();
        return true;
    }
}
