using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace VsVillage;

// GUI opened when a player right-clicks a workstation or bed that belongs to a
// village.  Lists compatible villagers and lets the player manually assign one,
// or clear the current assignment.
public class AssignVillagerGui : GuiDialog
{
	private readonly VillageAssignmentContext ctx;
	private long selectedVillagerId;
	private long selectedDayGuardId;
	private long selectedNightGuardId;

	public override string ToggleKeyCombinationCode => null;

	public AssignVillagerGui(ICoreClientAPI capi, VillageAssignmentContext ctx) : base(capi)
	{
		this.ctx = ctx;
		ctx.Village.Init(capi);
		if (ctx.IsGuardPost) ComposeGuardPostGui(capi);
		else ComposeGui(capi);
	}

	// Guard posts carry two independent assignments, so they get their own compose rather than
	// bending the single-dropdown workstation/bed layout.
	private void ComposeGuardPostGui(ICoreClientAPI capi)
	{
		ctx.Village.GuardPosts.TryGetValue(ctx.StructurePos, out VillagerGuardPost post);
		if (post == null)
		{
			capi.Logger.Warning("[VsVillage] Assign GUI: guard post at {0} no longer tracked.", ctx.StructurePos);
		}
		long dayOwnerId = post?.DayOwnerId ?? -1L;
		long nightOwnerId = post?.NightOwnerId ?? -1L;
		selectedDayGuardId = dayOwnerId;
		selectedNightGuardId = nightOwnerId;

		List<VillagerData> compatible = ctx.Village.VillagerSaveData.Values
			.Where(v => GuardDuty.CanStandWatch(v.Profession))
			.OrderBy(v => v.Name)
			.ThenBy(v => v.Id)
			.ToList();

		string infoText = Lang.Get("vsvillage:assign-structure-info-guardpost",
			ManagementGui.BlockPosToString(ctx.StructurePos, capi),
			NameOf(dayOwnerId),
			NameOf(nightOwnerId));

		double dayY = 20.0 + 80.0 + 10.0;
		double nightY = dayY + 40.0;
		double buttonY = nightY + 45.0;

		ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
		ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
		bgBounds.BothSizing = ElementSizing.FitToChildren;

		var composer = capi.Gui
			.CreateCompo("VillageGuardPostDialog", dialogBounds)
			.AddShadedDialogBG(bgBounds)
			.AddDialogTitleBar(Lang.Get("vsvillage:assign-guardpost-title"), () => TryClose())
			.BeginChildElements(bgBounds)
			.AddRichtext(infoText, CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0.0, 20.0, 500.0, 80.0))
			.AddStaticText(Lang.Get("vsvillage:guardpost-shift-day"), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0.0, dayY, 200.0, 30.0))
			.AddStaticText(Lang.Get("vsvillage:guardpost-shift-night"), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0.0, nightY, 200.0, 30.0));

		AddGuardDropDown(composer, compatible, dayOwnerId, nightOwnerId, dayY, "guardpost-day-select",
			value => selectedDayGuardId = long.Parse(value));
		AddGuardDropDown(composer, compatible, nightOwnerId, dayOwnerId, nightY, "guardpost-night-select",
			value => selectedNightGuardId = long.Parse(value));

		composer
			.AddButton(Lang.Get("vsvillage:guardpost-assign-day"), () => SendAssignGuard(capi, EnumGuardShift.day, selectedDayGuardId), ElementBounds.Fixed(0.0, buttonY, 240.0, 30.0))
			.AddButton(Lang.Get("vsvillage:guardpost-assign-night"), () => SendAssignGuard(capi, EnumGuardShift.night, selectedNightGuardId), ElementBounds.Fixed(250.0, buttonY, 240.0, 30.0))
			.EndChildElements();

		base.SingleComposer = composer.Compose();
	}

	// excludeId is the other watch's owner: a guard cannot hold both. The server enforces this too.
	private void AddGuardDropDown(GuiComposer composer, List<VillagerData> compatible, long currentId, long excludeId, double y, string key, System.Action<string> onSelect)
	{
		var ids = new List<string> { "-1" };
		var names = new List<string> { Lang.Get("vsvillage:assign-none") };
		foreach (VillagerData vd in compatible)
		{
			if (vd.Id == excludeId && excludeId != -1L) continue;
			ids.Add(vd.Id.ToString());
			names.Add(VillagerOption(vd));
		}

		int initialSel = 0;
		if (currentId != -1L)
		{
			int idx = ids.IndexOf(currentId.ToString());
			if (idx >= 0) initialSel = idx;
		}

		if (ids.Count > 1)
		{
			composer.AddDropDown(ids.ToArray(), names.ToArray(), initialSel,
				(value, _) => onSelect(value), ElementBounds.Fixed(210.0, y, 280.0, 30.0), key);
		}
		else
		{
			composer.AddStaticText(Lang.Get("vsvillage:assign-no-compatible"), CairoFont.WhiteSmallishText(),
				ElementBounds.Fixed(210.0, y, 280.0, 30.0));
		}
	}

	private string NameOf(long villagerId)
	{
		if (villagerId != -1L && ctx.Village.VillagerSaveData.TryGetValue(villagerId, out VillagerData data))
			return data.Name;
		return Lang.Get("vsvillage:nobody");
	}

	private static string VillagerOption(VillagerData villager)
	{
		string profession = Lang.Get("vsvillage:management-profession-" + villager.Profession);
		return Lang.Get("vsvillage:assign-villager-option", villager.Name, profession);
	}

	private bool SendAssignGuard(ICoreClientAPI capi, EnumGuardShift shift, long entityId)
	{
		var msg = new VillageManagementMessage
		{
			Operation         = EnumVillageManagementOperation.assignGuardPost,
			Id                = ctx.Village.Id,
			StructureToAssign = ctx.StructurePos,
			AssigneeEntityId  = entityId,
			GuardShift        = shift
		};
		capi.Network.GetChannel("villagemanagementnetwork").SendPacket(msg);
		TryClose();
		return true;
	}

	private void ComposeGui(ICoreClientAPI capi)
	{
		// --- Determine current owner ---
		long currentOwnerId = -1L;
		string currentOwnerName = Lang.Get("vsvillage:nobody");

		if (!ctx.IsBed)
		{
			if (ctx.Village.Workstations.TryGetValue(ctx.StructurePos, out VillagerWorkstation ws) && ws.OwnerId != -1)
			{
				currentOwnerId   = ws.OwnerId;
				currentOwnerName = ctx.Village.VillagerSaveData.TryGetValue(currentOwnerId, out VillagerData ownerData)
					? ownerData.Name
					: Lang.Get("vsvillage:nobody");
			}
		}
		else
		{
			if (ctx.Village.Beds.TryGetValue(ctx.StructurePos, out VillagerBed bed) && bed.OwnerId != -1)
			{
				currentOwnerId   = bed.OwnerId;
				currentOwnerName = ctx.Village.VillagerSaveData.TryGetValue(currentOwnerId, out VillagerData ownerData)
					? ownerData.Name
					: Lang.Get("vsvillage:nobody");
			}
		}

		// --- Build compatible villager list ---
		List<VillagerData> compatible;
		if (!ctx.IsBed)
		{
			if (ctx.Village.Workstations.TryGetValue(ctx.StructurePos, out VillagerWorkstation ws2))
			{
				compatible = ctx.Village.VillagerSaveData.Values
					.Where(v => v.Profession == ws2.Profession)
					.OrderBy(v => v.Name)
					.ThenBy(v => v.Id)
					.ToList();
			}
			else
			{
				// Workstation entry vanished between server-send and client-compose. Empty list instead of misleading farmer-default.
				capi.Logger.Warning("[VsVillage] Assign GUI: workstation at {0} no longer tracked.", ctx.StructurePos);
				compatible = new List<VillagerData>();
			}
		}
		else
		{
			compatible = ctx.Village.VillagerSaveData.Values
				.OrderBy(v => v.Name)
				.ThenBy(v => v.Id)
				.ToList();
		}

		// "None" is always the first entry (clear assignment).
		var dropdownIds   = new List<string> { "-1" };
		var dropdownNames = new List<string> { Lang.Get("vsvillage:assign-none") };
		foreach (VillagerData vd in compatible)
		{
			dropdownIds.Add(vd.Id.ToString());
			dropdownNames.Add(VillagerOption(vd));
		}

		// Pre-select the current owner in the dropdown (if present).
		int initialSel = 0;
		if (currentOwnerId != -1)
		{
			int idx = dropdownIds.IndexOf(currentOwnerId.ToString());
			if (idx >= 0) initialSel = idx;
		}
		selectedVillagerId = currentOwnerId; // default send value

		// --- Structure info text ---
		string posStr = ManagementGui.BlockPosToString(ctx.StructurePos, capi);
		string infoText;
		if (ctx.IsBed)
		{
			infoText = Lang.Get("vsvillage:assign-structure-info-bed", posStr, currentOwnerName);
		}
		else
		{
			string profName = ctx.Village.Workstations.TryGetValue(ctx.StructurePos, out VillagerWorkstation ws3)
				? Lang.Get("vsvillage:management-profession-" + ws3.Profession)
				: "?";
			infoText = Lang.Get("vsvillage:assign-structure-info-workstation", profName, posStr, currentOwnerName);
		}

		// --- Profession resource stats (farmer: farmland+crops / shepherd: animals) ---
		bool hasStats = !ctx.IsBed && ctx.WorkstationResourceCount >= 0;
		if (hasStats && ctx.Village.Workstations.TryGetValue(ctx.StructurePos, out VillagerWorkstation wsStats))
		{
			if (wsStats.Profession == EnumVillagerProfession.farmer)
			{
				infoText += "\n\n" + Lang.Get("vsvillage:assign-farmland-count", ctx.WorkstationResourceCount);
				if (ctx.ResourceBreakdownKeys?.Count > 0)
				{
					var parts = new System.Collections.Generic.List<string>();
					for (int i = 0; i < ctx.ResourceBreakdownKeys.Count; i++)
						parts.Add($"{ctx.ResourceBreakdownKeys[i]}: {ctx.ResourceBreakdownValues[i]}");
					infoText += "\n" + Lang.Get("vsvillage:assign-crops-list", string.Join(", ", parts));
				}
				else
				{
					infoText += "\n" + Lang.Get("vsvillage:assign-no-crops");
				}
			}
			else if (wsStats.Profession == EnumVillagerProfession.shepherd)
			{
				infoText += "\n\n" + Lang.Get("vsvillage:assign-animal-count", ctx.WorkstationResourceCount);
				if (ctx.ResourceBreakdownKeys?.Count > 0)
				{
					var parts = new System.Collections.Generic.List<string>();
					for (int i = 0; i < ctx.ResourceBreakdownKeys.Count; i++)
						parts.Add($"{ctx.ResourceBreakdownKeys[i]}: {ctx.ResourceBreakdownValues[i]}");
					infoText += "\n" + Lang.Get("vsvillage:assign-livestock-list", string.Join(", ", parts));
				}
				else
				{
					infoText += "\n" + Lang.Get("vsvillage:assign-no-animals");
				}
			}
		}

		// --- Shop specialty (trader workstations only) ---
		VillagerWorkstation shopWs = null;
		bool isTraderStation = !ctx.IsBed
			&& ctx.Village.Workstations.TryGetValue(ctx.StructurePos, out shopWs)
			&& shopWs.Profession == EnumVillagerProfession.trader;
		string currentShopType = isTraderStation ? TraderShopType.Sanitize(shopWs.ShopType) : TraderShopType.Default;
		int shopCost = isTraderStation ? TraderShopType.CostFor(shopWs.ShopTypeChanges) : 0;
		if (isTraderStation)
		{
			infoText += "\n\n" + Lang.Get("vsvillage:shoptype-current", Lang.Get("vsvillage:shoptype-" + currentShopType));
		}

		// --- Layout - shift select/buttons down when stats add extra lines ---
		// Trader stations add a blank line plus the shop-type line, so two rows at ~27px.
		double infoH       = (hasStats ? 130.0 : 80.0) + (isTraderStation ? 55.0 : 0.0);
		double selectY     = 20.0 + infoH + 10.0;
		double shopButtonY = selectY + 40.0;
		double buttonY     = (isTraderStation ? shopButtonY : selectY) + 40.0;

		ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
		ElementBounds bgBounds     = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
		bgBounds.BothSizing = ElementSizing.FitToChildren;

		var composer = capi.Gui
			.CreateCompo("VillageAssignmentDialog", dialogBounds)
			.AddShadedDialogBG(bgBounds)
			.AddDialogTitleBar(Lang.Get("vsvillage:assign-villager-title"), () => TryClose())
			.BeginChildElements(bgBounds)
			.AddRichtext(infoText, CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0.0, 20.0, 500.0, infoH))
			.AddStaticText(Lang.Get("vsvillage:assign-select-villager"), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0.0, selectY, 200.0, 30.0));

		if (dropdownIds.Count > 1)
		{
			composer.AddDropDown(
				dropdownIds.ToArray(),
				dropdownNames.ToArray(),
				initialSel,
				(value, _) => selectedVillagerId = long.Parse(value),
				ElementBounds.Fixed(210.0, selectY, 280.0, 30.0),
				"assign-villager-select");
		}
		else
		{
			composer.AddStaticText(
				Lang.Get("vsvillage:assign-no-compatible"),
				CairoFont.WhiteSmallishText(),
				ElementBounds.Fixed(210.0, selectY, 280.0, 30.0));
		}

		if (isTraderStation)
		{
			composer.AddButton(Lang.Get("vsvillage:shoptype-change-button"), () => OpenShopTypePicker(capi, currentShopType, shopCost),
				ElementBounds.Fixed(0.0, shopButtonY, 340.0, 30.0));
		}

		composer
			.AddButton(Lang.Get("vsvillage:assign-button"),       () => SendAssign(capi, selectedVillagerId), ElementBounds.Fixed(0.0,   buttonY, 150.0, 30.0))
			.AddButton(Lang.Get("vsvillage:assign-clear-button"),  () => SendAssign(capi, -1L),               ElementBounds.Fixed(160.0, buttonY, 180.0, 30.0))
			.EndChildElements();

		base.SingleComposer = composer.Compose();
	}

	private bool SendAssign(ICoreClientAPI capi, long entityId)
	{
		var msg = new VillageManagementMessage
		{
			Operation        = ctx.IsBed ? EnumVillageManagementOperation.assignBed : EnumVillageManagementOperation.assignWorkstation,
			Id               = ctx.Village.Id,
			StructureToAssign = ctx.StructurePos,
			AssigneeEntityId  = entityId
		};
		capi.Network.GetChannel("villagemanagementnetwork").SendPacket(msg);
		TryClose();
		return true;
	}

	// This dialog stays open behind the picker so Cancel puts the player back here.
	private bool OpenShopTypePicker(ICoreClientAPI capi, string currentShopType, int cost)
	{
		new TraderShopTypeGui(capi, ctx, this, currentShopType, cost).TryOpen();
		return true;
	}
}
