using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace VsVillage;

// Confirmation popup for re-specialising a trader workstation. Opens over
// AssignVillagerGui, which stays behind it so Cancel returns the player there.
public class TraderShopTypeGui : GuiDialog
{
	private readonly VillageAssignmentContext ctx;
	private readonly GuiDialog parent;
	private readonly string currentType;
	private readonly int cost;
	private readonly int gearsHeld;
	private string selectedType;

	public override string ToggleKeyCombinationCode => null;

	public TraderShopTypeGui(ICoreClientAPI capi, VillageAssignmentContext ctx, GuiDialog parent, string currentType, int cost)
		: base(capi)
	{
		this.ctx = ctx;
		this.parent = parent;
		this.currentType = currentType;
		this.cost = cost;
		this.selectedType = currentType;
		this.gearsHeld = CountRustyGears(capi);
		ComposeGui(capi);
	}

	// Advisory only. The server recounts and is the one that can refuse.
	private static int CountRustyGears(ICoreClientAPI capi)
	{
		int total = 0;
		IPlayerInventoryManager mgr = capi.World?.Player?.InventoryManager;
		if (mgr == null) return 0;
		foreach (IInventory inv in mgr.Inventories.Values)
		{
			if (inv.ClassName == "creative") continue;
			foreach (ItemSlot slot in inv)
			{
				if (slot?.Itemstack?.Collectible?.Code?.Path == "gear-rusty") total += slot.Itemstack.StackSize;
			}
		}
		return total;
	}

	private void ComposeGui(ICoreClientAPI capi)
	{
		var names = new string[TraderShopType.Types.Length];
		for (int i = 0; i < TraderShopType.Types.Length; i++)
			names[i] = Lang.Get("vsvillage:shoptype-" + TraderShopType.Types[i]);

		int initialSel = System.Math.Max(0, System.Array.IndexOf(TraderShopType.Types, currentType));

		string infoText = Lang.Get("vsvillage:shoptype-current", Lang.Get("vsvillage:shoptype-" + currentType));
		string costText = gearsHeld >= cost
			? Lang.Get("vsvillage:shoptype-cost", cost, gearsHeld)
			: Lang.Get("vsvillage:shoptype-cost-short", cost, gearsHeld);

		double selectY = 20.0 + 40.0 + 10.0;
		double costY   = selectY + 45.0;
		double buttonY = costY + 40.0;

		ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
		ElementBounds bgBounds     = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
		bgBounds.BothSizing = ElementSizing.FitToChildren;

		var composer = capi.Gui
			.CreateCompo("VillageShopTypeDialog", dialogBounds)
			.AddShadedDialogBG(bgBounds)
			.AddDialogTitleBar(Lang.Get("vsvillage:shoptype-dialog-title"), () => TryClose())
			.BeginChildElements(bgBounds)
			.AddRichtext(infoText, CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0.0, 20.0, 460.0, 40.0))
			.AddStaticText(Lang.Get("vsvillage:shoptype-select"), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0.0, selectY, 180.0, 30.0))
			.AddDropDown(
				TraderShopType.Types,
				names,
				initialSel,
				(value, _) => OnTypePicked(value),
				ElementBounds.Fixed(190.0, selectY, 270.0, 30.0),
				"shoptype-picker")
			.AddRichtext(costText, CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0.0, costY, 460.0, 30.0))
			.AddButton(Lang.Get("vsvillage:shoptype-confirm"), () => SendChange(capi), ElementBounds.Fixed(0.0, buttonY, 220.0, 30.0), EnumButtonStyle.Normal, "shoptype-confirm")
			.AddButton(Lang.Get("vsvillage:shoptype-cancel"), () => TryClose(), ElementBounds.Fixed(240.0, buttonY, 220.0, 30.0))
			.EndChildElements();

		base.SingleComposer = composer.Compose();
		RefreshConfirmState();
	}

	// The parent is still open behind us but lost focus when we opened. Hand it back
	// so Cancel leaves the player on a working assignment dialog.
	public override void OnGuiClosed()
	{
		base.OnGuiClosed();
		if (parent != null && parent.IsOpened()) capi.Gui.RequestFocus(parent);
	}

	private void OnTypePicked(string value)
	{
		selectedType = value;
		RefreshConfirmState();
	}

	private void RefreshConfirmState()
	{
		GuiElementTextButton confirm = base.SingleComposer?.GetButton("shoptype-confirm");
		if (confirm == null) return;
		confirm.Enabled = selectedType != currentType && gearsHeld >= cost;
	}

	private bool SendChange(ICoreClientAPI capi)
	{
		var msg = new VillageManagementMessage
		{
			Operation         = EnumVillageManagementOperation.changeShopType,
			Id                = ctx.Village.Id,
			StructureToAssign = ctx.StructurePos,
			ShopType          = selectedType
		};
		capi.Network.GetChannel("villagemanagementnetwork").SendPacket(msg);
		TryClose();
		// The parent still shows the old type and the old price, so close it too.
		parent?.TryClose();
		return true;
	}
}
