using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace VsVillage;

public class CooldownCondition : IActionCondition, IStorableTypedComponent
{
	public const string ConditionType = "CooldownCondition";

	[JsonProperty]
	public long CooldownInSeconds = 30L;

	public long LastSuccessfulCheck = long.MinValue;

	public string Type => "CooldownCondition";

	[JsonProperty]
	public bool Invert { get; set; }

	public void AddGuiEditFields(ICoreClientAPI capi, GuiComposer singleComposer)
	{
		ElementBounds elementBounds = ElementBounds.Fixed(0.0, 0.0, 200.0, 25.0);
		singleComposer.AddStaticText("Cooldown in seconds", CairoFont.WhiteDetailText(), elementBounds).AddNumberInput(elementBounds.RightCopy(), delegate
		{
		}, null, "CooldownInSeconds").GetNumberInput("CooldownInSeconds")
			.SetValue(30f);
	}

	public void StoreGuiEditFields(ICoreClientAPI capi, GuiComposer singleComposer)
	{
		CooldownInSeconds = (long)singleComposer.GetNumberInput("CooldownInSeconds").GetValue();
	}

	public bool ConditionSatisfied(Entity entity)
	{
		long elapsedSeconds = entity.World.Calendar.ElapsedSeconds;
		if (LastSuccessfulCheck + CooldownInSeconds < elapsedSeconds)
		{
			LastSuccessfulCheck = elapsedSeconds;
			return true;
		}
		return false;
	}

	public void LoadState(ITreeAttribute tree)
	{
		LastSuccessfulCheck = tree.GetLong("LastSuccessfulCheck", long.MinValue);
	}

	public void OnLoaded(EntityActivitySystem vas)
	{
	}

	public void StoreState(ITreeAttribute tree)
	{
		tree.SetFloat("LastSuccessfulCheck", LastSuccessfulCheck);
	}

	public IActionCondition Clone()
	{
		return new CooldownCondition
		{
			Invert = Invert,
			CooldownInSeconds = CooldownInSeconds
		};
	}

	public override string ToString()
	{
		return string.Format("Whenever villager hasn't tried this at {0} {1} seconds.", Invert ? "most " : "least", CooldownInSeconds);
	}
}
