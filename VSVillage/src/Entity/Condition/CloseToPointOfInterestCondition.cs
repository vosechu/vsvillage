using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

[JsonObject(MemberSerialization.OptIn)]
public class CloseToPointOfInterestCondition : IActionCondition, IStorableTypedComponent
{
	public const string ConditionType = "CloseToPointOfInterest";

	public string Type => "CloseToPointOfInterest";

	[JsonProperty]
	public bool Invert { get; set; }

	[JsonProperty]
	public VillagePointOfInterest Target { get; set; }

	[JsonProperty]
	public float MaxDistance { get; set; } = 2f;

	public IActionCondition Clone()
	{
		return new CloseToPointOfInterestCondition
		{
			Invert = Invert,
			Target = Target,
			MaxDistance = MaxDistance
		};
	}

	public bool ConditionSatisfied(Entity entity)
	{
		BlockPos blockPos = findPointOfInterest(entity.GetBehavior<EntityBehaviorVillager>());
		if (blockPos != null)
		{
			return blockPos.DistanceSqTo(entity.ServerPos.X, entity.ServerPos.Y, entity.ServerPos.Z) < MaxDistance * MaxDistance;
		}
		return false;
	}

	private BlockPos findPointOfInterest(EntityBehaviorVillager villager)
	{
		if (villager == null)
		{
			return null;
		}
		switch (Target)
		{
		case VillagePointOfInterest.bed:
			if (villager.Bed == null || (villager.Village != null && !villager.Village.Beds.ContainsKey(villager.Bed)))
			{
				villager.Bed = villager.Village?.FindFreeBed(villager.entity.EntityId);
			}
			return villager.Bed;
		case VillagePointOfInterest.workstation:
			if (villager.Workstation == null || (villager.Village != null && !villager.Village.Workstations.ContainsKey(villager.Workstation)))
			{
				villager.Workstation = villager.Village?.FindFreeWorkstation(villager.entity.EntityId, villager.Profession);
			}
			return villager.Workstation;
		case VillagePointOfInterest.gatherplace:
		{
			EntityPos pos = villager.entity.ServerPos;
			return villager.Village?.Gatherplaces?.MinBy((BlockPos gatherplace) => gatherplace.DistanceSqTo(pos.X, pos.Y, pos.Z));
		}
		default:
			return null;
		}
	}

	public void AddGuiEditFields(ICoreClientAPI capi, GuiComposer singleComposer)
	{
		ElementBounds elementBounds = ElementBounds.Fixed(0.0, 0.0, 200.0, 25.0);
		ElementBounds elementBounds2 = elementBounds.BelowCopy();
		elementBounds2.BelowCopy().BelowCopy();
		string[] array = new List<VillagePointOfInterest>(Enum.GetValues<VillagePointOfInterest>()).ConvertAll((VillagePointOfInterest poi) => poi.ToString()).ToArray();
		singleComposer.AddStaticText("Point of Interest", CairoFont.WhiteDetailText(), elementBounds).AddDropDown(array, array, 0, delegate
		{
		}, elementBounds.RightCopy(), "Target").AddStaticText("Max Distance", CairoFont.WhiteDetailText(), elementBounds2)
			.AddNumberInput(elementBounds2.RightCopy(), delegate
			{
			}, null, "MaxDistance");
		singleComposer.GetDropDown("Target").SetSelectedIndex((int)Target);
		singleComposer.GetNumberInput("MaxDistance").SetValue(MaxDistance);
	}

	public void StoreGuiEditFields(ICoreClientAPI capi, GuiComposer singleComposer)
	{
		Target = Enum.Parse<VillagePointOfInterest>(singleComposer.GetDropDown("Target").SelectedValue);
		MaxDistance = singleComposer.GetNumberInput("MaxDistance").GetValue();
	}

	public void OnLoaded(EntityActivitySystem vas)
	{
	}

	public void LoadState(ITreeAttribute tree)
	{
	}

	public void StoreState(ITreeAttribute tree)
	{
	}

	public override string ToString()
	{
		return string.Format("Whenever villager is {0}in a {1} block range of its {2}.", Invert ? "not " : "", MaxDistance, Target);
	}
}
