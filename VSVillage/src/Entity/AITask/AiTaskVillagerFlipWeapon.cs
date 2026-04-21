using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace VsVillage;

public class AiTaskVillagerFlipWeapon : AiTaskIdle
{
	private string weapon;

	public AiTaskVillagerFlipWeapon(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		weapon = taskConfig["weapon"].AsString("sword");
	}

	public override bool ShouldExecute()
	{
		base.ShouldExecute();
		return false;
	}

	public override void StartExecute()
	{
		base.StartExecute();
	}
}
