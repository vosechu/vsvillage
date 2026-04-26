using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// Placeholder task that triggers the villager weapon-flip behaviour via the
/// priority system.  ShouldExecute always returns false so it never actually
/// runs its own tick — the weapon flip is driven externally by
/// AiTaskVillagerMeleeAttack hooking into this task's priority slot.
/// </summary>
public class AiTaskVillagerFlipWeapon : AiTaskIdle
{
	public AiTaskVillagerFlipWeapon(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
	}

	public override bool ShouldExecute() => false;
}
