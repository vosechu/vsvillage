using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace VsVillage;

[JsonObject(MemberSerialization.OptIn)]
public class SleepAction : EntityActionBase
{
	public const string ActionType = "Sleep";

	[JsonProperty]
	public float AnimSpeed = 1f;

	[JsonProperty]
	public string AnimCode = "Lie";

	private TimeOfDayCondition timeOfDayCondition;

	public override string Type => "Sleep";

	public override void Start(EntityActivity entityActivity)
	{
		EntityBehaviorVillager behavior = vas.Entity.GetBehavior<EntityBehaviorVillager>();
		if (behavior?.Bed != null)
		{
			BlockEntityVillagerBed blockEntity = vas.Entity.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerBed>(behavior.Bed);
			if (blockEntity != null)
			{
				((Entity)vas.Entity).ServerPos.SetPos(getPos(blockEntity));
				((Entity)vas.Entity).ServerPos.Yaw = blockEntity.Yaw;
				vas.Entity.AnimManager.StartAnimation(new AnimationMetaData
				{
					Code = AnimCode,
					Animation = AnimCode,
					AnimationSpeed = AnimSpeed
				});
			}
		}
		entityActivity?.Conditions.Foreach(delegate(IActionCondition candidate)
		{
			if (candidate is TimeOfDayCondition timeOfDayCondition)
			{
				this.timeOfDayCondition = timeOfDayCondition;
			}
		});
	}

	private Vec3d getPos(BlockEntityVillagerBed bed)
	{
		Cardinal cardinal = bed.Block.Variant["side"] switch
		{
			"north" => Cardinal.North, 
			"east" => Cardinal.East, 
			"south" => Cardinal.South, 
			_ => Cardinal.West, 
		};
		return bed.Pos.ToVec3d().Add(0.5, 0.0, 0.5).Add(cardinal.Normalf.Clone().Mul(0.7f));
	}

	public override bool IsFinished()
	{
		TimeOfDayCondition obj = timeOfDayCondition;
		if (obj == null)
		{
			return true;
		}
		return !obj.ConditionSatisfied(vas.Entity);
	}

	public override void Finish()
	{
		vas.Entity.AnimManager.StopAnimation(AnimCode);
	}

	public override void Pause(EnumInteruptionType interuptionType)
	{
		Finish();
	}

	public override void Resume()
	{
		Start(null);
	}

	public override IEntityAction Clone()
	{
		return new SleepAction
		{
			vas = vas,
			AnimCode = AnimCode,
			AnimSpeed = AnimSpeed,
			timeOfDayCondition = timeOfDayCondition
		};
	}

	public override void AddGuiEditFields(ICoreClientAPI capi, GuiComposer singleComposer)
	{
		ElementBounds elementBounds = ElementBounds.Fixed(0.0, 0.0, 200.0, 25.0);
		ElementBounds elementBounds2 = elementBounds.BelowCopy();
		new List<VillagePointOfInterest>(Enum.GetValues<VillagePointOfInterest>()).ConvertAll((VillagePointOfInterest poi) => poi.ToString()).ToArray();
		singleComposer.AddStaticText("Animation", CairoFont.WhiteDetailText(), elementBounds).AddTextInput(elementBounds.RightCopy(), delegate
		{
		}, null, "Animation").AddStaticText("Animationspeed", CairoFont.WhiteDetailText(), elementBounds2)
			.AddNumberInput(elementBounds2.RightCopy(), delegate
			{
			}, null, "Animationspeed");
		singleComposer.GetTextInput("Animation").SetValue(AnimCode);
		singleComposer.GetNumberInput("Animationspeed").SetValue(AnimSpeed);
	}

	public override bool StoreGuiEditFields(ICoreClientAPI capi, GuiComposer singleComposer)
	{
		AnimCode = singleComposer.GetTextInput("Animation").GetText();
		AnimSpeed = singleComposer.GetNumberInput("Animationspeed").GetValue();
		return true;
	}

	public override string ToString()
	{
		return $"Sleep, Animation {AnimCode}, AnimSpeed {AnimSpeed}";
	}
}
