using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace VsVillage;

/// <summary>
/// GUI opened when a player right-clicks a workstation or bed that belongs to a
/// village.  Lists compatible villagers and lets the player manually assign one,
/// or clear the current assignment.
/// </summary>
public class AssignVillagerGui : GuiDialog
{
	private readonly VillageAssignmentContext ctx;
	private long selectedVillagerId;

	public override string ToggleKeyCombinationCode => null;

	public AssignVillagerGui(ICoreClientAPI capi, VillageAssignmentContext ctx) : base(capi)
	{
		this.ctx = ctx;
		ctx.Village.Init(capi);
		ComposeGui(capi);
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
			EnumVillagerProfession requiredProfession = ctx.Village.Workstations.TryGetValue(ctx.StructurePos, out VillagerWorkstation ws2)
				? ws2.Profession
				: EnumVillagerProfession.farmer;
			compatible = ctx.Village.VillagerSaveData.Values
				.Where(v => v.Profession == requiredProfession)
				.ToList();
		}
		else
		{
			compatible = ctx.Village.VillagerSaveData.Values.ToList();
		}

		// "None" is always the first entry (clear assignment).
		var dropdownIds   = new List<string> { "-1" };
		var dropdownNames = new List<string> { Lang.Get("vsvillage:assign-none") };
		foreach (VillagerData vd in compatible)
		{
			dropdownIds.Add(vd.Id.ToString());
			dropdownNames.Add(vd.Name);
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
				infoText += $"\n\nFarmland managed: {ctx.WorkstationResourceCount} blocks";
				if (ctx.ResourceBreakdownKeys?.Count > 0)
				{
					var parts = new System.Collections.Generic.List<string>();
					for (int i = 0; i < ctx.ResourceBreakdownKeys.Count; i++)
						parts.Add($"{ctx.ResourceBreakdownKeys[i]}: {ctx.ResourceBreakdownValues[i]}");
					infoText += $"\nCrops: {string.Join(", ", parts)}";
				}
				else
				{
					infoText += "\nNo crops planted yet.";
				}
			}
			else if (wsStats.Profession == EnumVillagerProfession.shepherd)
			{
				infoText += $"\n\nAnimals in village pens/barns: {ctx.WorkstationResourceCount}";
				if (ctx.ResourceBreakdownKeys?.Count > 0)
				{
					var parts = new System.Collections.Generic.List<string>();
					for (int i = 0; i < ctx.ResourceBreakdownKeys.Count; i++)
						parts.Add($"{ctx.ResourceBreakdownKeys[i]}: {ctx.ResourceBreakdownValues[i]}");
					infoText += $"\nLivestock: {string.Join(", ", parts)}";
				}
				else
				{
					infoText += "\nNo enclosed animals detected.";
				}
			}
		}

		// --- Layout — shift select/buttons down when stats add extra lines ---
		double infoH       = hasStats ? 130.0 : 80.0;
		double selectY     = 20.0 + infoH + 10.0;
		double buttonY     = selectY + 40.0;

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
}
