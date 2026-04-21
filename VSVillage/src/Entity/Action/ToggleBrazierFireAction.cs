using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace VsVillage;

[JsonObject(MemberSerialization.OptIn)]
public class ToggleBrazierFireAction : EntityActionBase
{
	public string[] states = new string[2] { "extinguish", "ignite" };

	public const string ActionType = "ToggleBrazierFire";

	[JsonProperty]
	public float MaxDistance = 3f;

	[JsonProperty]
	public bool Ignite = true;

	public override string Type => "ToggleBrazierFire";

	public override void Start(EntityActivity entityActivity)
	{
		EntityPos pos = ((Entity)vas.Entity).ServerPos;
		vas.Entity.GetBehavior<EntityBehaviorVillager>()?.Village?.Gatherplaces?.Foreach(delegate(BlockPos gatherplace)
		{
			if (gatherplace.DistanceSqTo(pos.X, pos.Y, pos.Z) < MaxDistance * MaxDistance)
			{
				vas.Entity.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerBrazier>(gatherplace)?.Toggle(Ignite);
			}
		});
	}

	public override IEntityAction Clone()
	{
		return new ToggleBrazierFireAction
		{
			vas = vas,
			Ignite = Ignite,
			MaxDistance = MaxDistance
		};
	}

	public override void AddGuiEditFields(ICoreClientAPI capi, GuiComposer singleComposer)
	{
		ElementBounds elementBounds = ElementBounds.Fixed(0.0, 0.0, 200.0, 25.0);
		ElementBounds elementBounds2 = elementBounds.BelowCopy();
		new List<VillagePointOfInterest>(Enum.GetValues<VillagePointOfInterest>()).ConvertAll((VillagePointOfInterest poi) => poi.ToString()).ToArray();
		singleComposer.AddStaticText("Action", CairoFont.WhiteDetailText(), elementBounds).AddDropDown(states, states, 0, delegate
		{
		}, elementBounds.RightCopy(), "Action").AddStaticText("MaxDistance", CairoFont.WhiteDetailText(), elementBounds2)
			.AddNumberInput(elementBounds2.RightCopy(), delegate
			{
			}, null, "MaxDistance");
		singleComposer.GetDropDown("Action").SetSelectedIndex(Ignite ? 1 : 0);
		singleComposer.GetNumberInput("MaxDistance").SetValue(MaxDistance);
	}

	public override bool StoreGuiEditFields(ICoreClientAPI capi, GuiComposer singleComposer)
	{
		Ignite = singleComposer.GetDropDown("Action").SelectedValue == "ignite";
		MaxDistance = singleComposer.GetNumberInput("MaxDistance").GetValue();
		return true;
	}

	public override string ToString()
	{
		return string.Format("{0} brazier within {1} Blocks", Ignite ? "Ignite" : "Extinguish", MaxDistance);
	}
}
