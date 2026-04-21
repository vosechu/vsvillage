using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

public class ManagementGui : GuiDialog
{
	private VillageManagementMessage managementMessage = new VillageManagementMessage();

	public override string ToggleKeyCombinationCode => null;

	public ManagementGui(ICoreClientAPI capi, BlockPos pos, Village village = null)
		: base(capi)
	{
		ManagementGui managementGui = this;
		ElementBounds elementBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
		ElementBounds elementBounds2 = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
		elementBounds2.BothSizing = ElementSizing.FitToChildren;
		if (village == null || village.Pos == null)
		{
			managementMessage.Pos = pos ?? capi.World.BlockAccessor.GetChunkAtBlockPos(((Entity)capi.World.Player.Entity).Pos.AsBlockPos).BlockEntities.Where((KeyValuePair<BlockPos, BlockEntity> entry) => entry.Value.Block is BlockMayorWorkstation).First().Value.Pos;
			base.SingleComposer = capi.Gui.CreateCompo("VillageManagementDialog-", elementBounds).AddShadedDialogBG(elementBounds2).AddDialogTitleBar(Lang.Get("vsvillage:management-title"), delegate
			{
				managementGui.TryClose();
			})
				.BeginChildElements(elementBounds2)
				.AddStaticText(Lang.Get("vsvillage:management-no-village-found"), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0.0, 20.0, 500.0, 30.0))
				.AddStaticText(Lang.Get("vsvillage:management-village-name"), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0.0, 80.0, 200.0, 30.0))
				.AddTextInput(ElementBounds.Fixed(100.0, 80.0, 200.0, 30.0), delegate(string name)
				{
					managementGui.managementMessage.Name = name;
				}, CairoFont.WhiteSmallishText())
				.AddStaticText(Lang.Get("vsvillage:management-village-radius"), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0.0, 110.0, 200.0, 30.0))
				.AddNumberInput(ElementBounds.Fixed(100.0, 110.0, 200.0, 30.0), delegate(string radius)
				{
					int.TryParse(radius, out managementGui.managementMessage.Radius);
				})
				.AddButton(Lang.Get("vsvillage:management-found-new-village"), () => managementGui.createVillage(capi), ElementBounds.Fixed(0.0, 140.0, 200.0, 30.0))
				.EndChildElements()
				.Compose();
		}
		else
		{
			managementMessage.Id = village.Id;
			managementMessage.Radius = village.Radius;
			managementMessage.Name = village.Name;
			recompose(capi, village, elementBounds, elementBounds2);
		}
	}

	private void recompose(ICoreClientAPI capi, Village village, ElementBounds dialogBounds, ElementBounds bgBounds, int curTab = 0)
	{
		GuiTab[] tabs = new GuiTab[5]
		{
			new GuiTab
			{
				Name = Lang.Get("vsvillage:tab-management-stats"),
				DataInt = 0,
				Active = (curTab == 0)
			},
			new GuiTab
			{
				Name = Lang.Get("vsvillage:tab-management-hire"),
				DataInt = 1,
				Active = (curTab == 1)
			},
			new GuiTab
			{
				Name = Lang.Get("vsvillage:tab-management-residents"),
				DataInt = 2,
				Active = (curTab == 2)
			},
			new GuiTab
			{
				Name = Lang.Get("vsvillage:tab-management-structures"),
				DataInt = 3,
				Active = (curTab == 3)
			},
			new GuiTab
			{
				Name = Lang.Get("vsvillage:tab-management-destroy"),
				DataInt = 4,
				Active = (curTab == 4)
			}
		};
		base.SingleComposer = capi.Gui.CreateCompo("VillageManagementDialog-", dialogBounds).AddShadedDialogBG(bgBounds).AddDialogTitleBar(Lang.Get("vsvillage:management-title"), delegate
		{
			TryClose();
		})
			.AddVerticalTabs(tabs, ElementBounds.Fixed(-200.0, 35.0, 200.0, 200.0), delegate(int id, GuiTab tab)
			{
				recompose(capi, village, dialogBounds, bgBounds, id);
			}, "tabs")
			.BeginChildElements(bgBounds);
		switch (curTab)
		{
		case 0:
		{
			int count = village.VillagerSaveData.Count;
			int count2 = village.Beds.Count;
			int count3 = village.Workstations.Count;
			int count4 = village.Gatherplaces.Count;
			Dictionary<EnumVillagerProfession, int> villagersByProfession = (from villager in village.VillagerSaveData.Values
				group villager by villager.Profession).ToDictionary((IGrouping<EnumVillagerProfession, VillagerData> group) => group.Key, (IGrouping<EnumVillagerProfession, VillagerData> group) => group.Count());
			Dictionary<EnumVillagerProfession, int> workstationsByProfession = (from workstation in village.Workstations.Values
				group workstation by workstation.Profession).ToDictionary((IGrouping<EnumVillagerProfession, VillagerWorkstation> group) => group.Key, (IGrouping<EnumVillagerProfession, VillagerWorkstation> group) => group.Count());
			List<EnumVillagerProfession> source = new List<EnumVillagerProfession>(Enum.GetValues<EnumVillagerProfession>());
			string vtmlCode = Lang.Get("vsvillage:management-poi-stats", count2, count3, count4, count) + source.Select((EnumVillagerProfession profession) => Lang.Get("vsvillage:management-profession-stats", Lang.Get("vsvillage:management-profession-" + profession), villagersByProfession.GetValueOrDefault(profession, 0), workstationsByProfession.GetValueOrDefault(profession, 0))).Aggregate(string.Concat);
			base.SingleComposer.AddRichtext(vtmlCode, CairoFont.WhiteSmallText(), ElementBounds.Fixed(0.0, 20.0, 450.0, 200.0)).AddStaticText(Lang.Get("vsvillage:management-village-name"), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(470.0, 20.0, 200.0, 30.0)).AddTextInput(ElementBounds.Fixed(570.0, 20.0, 200.0, 30.0), delegate(string name)
			{
				managementMessage.Name = name;
			}, CairoFont.WhiteSmallishText(), "villagename")
				.AddStaticText(Lang.Get("vsvillage:management-village-radius"), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(470.0, 60.0, 200.0, 30.0))
				.AddNumberInput(ElementBounds.Fixed(570.0, 60.0, 200.0, 30.0), delegate(string radius)
				{
					int.TryParse(radius, out managementMessage.Radius);
				}, null, "villageradius")
				.AddButton(Lang.Get("vsvillage:management-update-village-button"), () => changeStatsVillage(capi), ElementBounds.Fixed(470.0, 100.0, 200.0, 30.0));
			break;
		}
		case 1:
			// Note: mayor is intentionally excluded from the hire list — the player IS the mayor.
			// Mayor villagers exist only for world-generated villages and are not player-hirable.
			base.SingleComposer.AddButton(Lang.Get("vsvillage:management-hire-farmer"), () => hireVillager(capi, "farmer"), ElementBounds.Fixed(0.0, 20.0, 200.0, 30.0)).AddButton(Lang.Get("vsvillage:management-hire-shepherd"), () => hireVillager(capi, "shepherd"), ElementBounds.Fixed(220.0, 20.0, 200.0, 30.0)).AddButton(Lang.Get("vsvillage:management-hire-trader"), () => hireVillager(capi, "trader"), ElementBounds.Fixed(0.0, 60.0, 200.0, 30.0))
				.AddButton(Lang.Get("vsvillage:management-hire-smith"), () => hireVillager(capi, "smith"), ElementBounds.Fixed(220.0, 60.0, 200.0, 30.0))
				.AddButton(Lang.Get("vsvillage:management-hire-soldier"), () => hireVillager(capi, "soldier"), ElementBounds.Fixed(0.0, 100.0, 200.0, 30.0))
				.AddButton(Lang.Get("vsvillage:management-hire-herbalist"), () => hireVillager(capi, "herbalist"), ElementBounds.Fixed(220.0, 100.0, 200.0, 30.0))
				.AddButton(Lang.Get("vsvillage:management-hire-archer"), () => hireVillager(capi, "archer", EnumVillagerProfession.soldier), ElementBounds.Fixed(0.0, 140.0, 200.0, 30.0));
			break;
		case 2:
		{
			string[] array = village.VillagerSaveData.Values.ToList().ConvertAll((VillagerData data) => data.Id.ToString()).ToArray();
			string[] names = village.VillagerSaveData.Values.ToList().ConvertAll((VillagerData data) => data.Name).ToArray();
			if (array.Length != 0)
			{
				base.SingleComposer.AddStaticText(Lang.Get("vsvillage:management-select-villager"), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0.0, 20.0, 200.0, 30.0)).AddDropDown(array, names, 0, delegate(string code, bool sel)
				{
					base.SingleComposer.GetDynamicText("villager-note").SetNewText(villagerNote(code, capi));
				}, ElementBounds.Fixed(200.0, 20.0, 300.0, 30.0), "villagers").AddButton(Lang.Get("vsvillage:management-remove-villager"), () => removeVillager(capi), ElementBounds.Fixed(520.0, 20.0, 200.0, 30.0))
					.AddDynamicText(villagerNote(array[0], capi), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0.0, 60.0, 500.0, 150.0), "villager-note");
			}
			else
			{
				base.SingleComposer.AddStaticText(Lang.Get("vsvillage:management-emtpy"), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0.0, 20.0, 500.0, 30.0));
			}
			break;
		}
		case 3:
		{
			List<string> list = village.Workstations.Values.ToList().ConvertAll((VillagerWorkstation workstation) => workstation.Pos.ToString());
			list.AddRange(village.Beds.Values.ToList().ConvertAll((VillagerBed bed) => bed.Pos.ToString()));
			list.AddRange(village.Gatherplaces.ToList().ConvertAll((BlockPos gatherplace) => gatherplace.ToString()));
			List<string> list2 = village.Workstations.Values.ToList().ConvertAll((VillagerWorkstation workstation) => $"{Lang.GetMatching($"vsvillage:block-workstation-{workstation.Profession}-east")}, {BlockPosToString(workstation.Pos, capi)}");
			list2.AddRange(village.Beds.Values.ToList().ConvertAll((VillagerBed bed) => string.Format("{0}, {1}", Lang.GetMatching("vsvillage:block-villagebed-east"), BlockPosToString(bed.Pos, capi))));
			list2.AddRange(village.Gatherplaces.ToList().ConvertAll((BlockPos gatherplace) => string.Format("{0}, {1}", Lang.GetMatching("vsvillage:block-brazier-extinct"), BlockPosToString(gatherplace, capi))));
			if (list.Count > 0)
			{
				base.SingleComposer.AddStaticText(Lang.Get("vsvillage:management-select-structure"), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0.0, 20.0, 200.0, 30.0)).AddDropDown(list.ToArray(), list2.ToArray(), 0, delegate(string code, bool sel)
				{
					base.SingleComposer.GetDynamicText("structure-note").SetNewText(structureNote(village, code, capi));
				}, ElementBounds.Fixed(200.0, 20.0, 300.0, 30.0), "structures").AddButton(Lang.Get("vsvillage:management-remove-structure"), () => removeStructure(capi), ElementBounds.Fixed(520.0, 20.0, 200.0, 30.0))
					.AddDynamicText(structureNote(village, list[0], capi), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0.0, 60.0, 500.0, 150.0), "structure-note");
			}
			else
			{
				base.SingleComposer.AddStaticText(Lang.Get("vsvillage:management-emtpy"), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0.0, 20.0, 500.0, 30.0));
			}
			break;
		}
		case 4:
			base.SingleComposer.AddStaticText(Lang.Get("vsvillage:management-destroy-village-text"), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0.0, 20.0, 500.0, 30.0)).AddButton(Lang.Get("vsvillage:management-destroy-village-button"), () => destroyVillage(capi), ElementBounds.Fixed(0.0, 50.0, 200.0, 30.0));
			break;
		}
		base.SingleComposer.EndChildElements().Compose();
		base.SingleComposer.GetTextInput("villagename")?.SetValue(village.Name);
		base.SingleComposer.GetTextInput("villageradius")?.SetValue(village.Radius);
	}

	private bool hireVillager(ICoreClientAPI capi, string type, EnumVillagerProfession? profession = null)
	{
		managementMessage.Operation = EnumVillageManagementOperation.hireVillager;
		managementMessage.VillagerProfession = profession ?? Enum.Parse<EnumVillagerProfession>(type);
		managementMessage.VillagerType = type;
		capi.Network.GetChannel("villagemanagementnetwork").SendPacket(managementMessage);
		return true;
	}

	private string villagerNote(string code, ICoreClientAPI capi)
	{
		Entity entityById = capi.World.GetEntityById(long.Parse(code));
		EntityBehaviorVillager entityBehaviorVillager = entityById?.GetBehavior<EntityBehaviorVillager>();
		if (entityBehaviorVillager != null)
		{
			return Lang.Get("vsvillage:management-villager-note", entityById.GetBehavior<EntityBehaviorNameTag>().DisplayName, Lang.Get("vsvillage:management-profession-" + entityBehaviorVillager.Profession), BlockPosToString(entityById.Pos.AsBlockPos, capi), BlockPosToString(entityBehaviorVillager.Workstation, capi), BlockPosToString(entityBehaviorVillager.Bed, capi));
		}
		return Lang.Get("vsvillage:missing-in-action");
	}

	private string structureNote(Village village, string code, ICoreClientAPI capi)
	{
		VillagerWorkstation villagerWorkstation = village.Workstations.Values.ToList().Find((VillagerWorkstation candidate) => candidate.Pos.ToString().Equals(code));
		if (villagerWorkstation != null)
		{
			return Lang.Get("vsvillage:management-structure-note", Lang.Get("vsvillage:" + villagerWorkstation.Profession), capi.World.GetEntityById(villagerWorkstation.OwnerId)?.GetBehavior<EntityBehaviorNameTag>().DisplayName ?? Lang.Get("vsvillage:nobody"), BlockPosToString(villagerWorkstation.Pos, capi));
		}
		VillagerBed villagerBed = village.Beds.Values.ToList().Find((VillagerBed candidate) => candidate.Pos.ToString().Equals(code));
		if (villagerBed != null)
		{
			return Lang.Get("vsvillage:management-structure-note", Lang.Get("vsvillage:bed"), capi.World.GetEntityById(villagerBed.OwnerId)?.GetBehavior<EntityBehaviorNameTag>().DisplayName ?? Lang.Get("vsvillage:nobody"), BlockPosToString(villagerBed.Pos, capi));
		}
		BlockPos blockPos = village.Gatherplaces.ToList().Find((BlockPos candidate) => candidate.ToString().Equals(code));
		if (blockPos != null)
		{
			return Lang.Get("vsvillage:management-structure-note", Lang.Get("vsvillage:gatherplace"), Lang.Get("vsvillage:everybody"), BlockPosToString(blockPos, capi));
		}
		return null;
	}

	private bool createVillage(ICoreClientAPI capi)
	{
		managementMessage.Operation = EnumVillageManagementOperation.create;
		capi.Network.GetChannel("villagemanagementnetwork").SendPacket(managementMessage);
		TryClose();
		return true;
	}

	private bool destroyVillage(ICoreClientAPI capi)
	{
		managementMessage.Operation = EnumVillageManagementOperation.destroy;
		capi.Network.GetChannel("villagemanagementnetwork").SendPacket(managementMessage);
		TryClose();
		return true;
	}

	private bool changeStatsVillage(ICoreClientAPI capi)
	{
		managementMessage.Operation = EnumVillageManagementOperation.changeStats;
		capi.Network.GetChannel("villagemanagementnetwork").SendPacket(managementMessage);
		TryClose();
		return true;
	}

	private bool removeStructure(ICoreClientAPI capi)
	{
		managementMessage.Operation = EnumVillageManagementOperation.removeStructure;
		managementMessage.StructureToRemove = BlockPosFromString(base.SingleComposer.GetDropDown("structures").SelectedValue);
		capi.Network.GetChannel("villagemanagementnetwork").SendPacket(managementMessage);
		TryClose();
		return true;
	}

	private bool removeVillager(ICoreClientAPI capi)
	{
		managementMessage.Operation = EnumVillageManagementOperation.removeVillager;
		managementMessage.VillagerToRemove = long.Parse(base.SingleComposer.GetDropDown("villagers").SelectedValue);
		capi.Network.GetChannel("villagemanagementnetwork").SendPacket(managementMessage);
		TryClose();
		return true;
	}

	public static string BlockPosToString(BlockPos pos, ICoreAPI api)
	{
		if (!(pos != null))
		{
			return Lang.Get("vsvillage:nowhere");
		}
		return $"X={pos.X - api.World.BlockAccessor.MapSizeX / 2}, Y={pos.Y}, Z={pos.Z - api.World.BlockAccessor.MapSizeZ / 2}";
	}

	public static BlockPos BlockPosFromString(string pos)
	{
		List<int> list = new List<Match>(Regex.Matches(pos, "\\d+")).ConvertAll((Match match) => int.Parse(match.Value));
		return new BlockPos(list[0], list[1], list[2], (list.Count > 3) ? list[3] : 0);
	}
}
