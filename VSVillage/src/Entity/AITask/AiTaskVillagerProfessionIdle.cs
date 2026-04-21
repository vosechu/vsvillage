using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// Generic station-idle task: walks to the villager's assigned workstation and
/// plays a configurable animation. Used for herbalists (grinding) and traders (sweep).
/// Configure via JSON: "professionSuffix" filters which entity type runs this task,
/// "interactionAnimation" names the animation to play, "animationDuration" sets how long.
/// </summary>
public class AiTaskVillagerProfessionIdle : AiTaskGotoAndInteract
{
	private string professionSuffix;
	private string interactionAnimation;
	private float interactionAnimationSpeed;
	private int animationDuration;

	public AiTaskVillagerProfessionIdle(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		professionSuffix = taskConfig["professionSuffix"].AsString("");
		interactionAnimation = taskConfig["interactionAnimation"].AsString("hoe-till");
		interactionAnimationSpeed = taskConfig["interactionAnimationSpeed"].AsFloat(1f);
		animationDuration = taskConfig["animationDuration"].AsInt(2500);
	}

	protected override Vec3d GetTargetPos()
	{
		if (!IsProfession())
		{
			return null;
		}
		EntityBehaviorVillager villager = entity.GetBehavior<EntityBehaviorVillager>();
		BlockPos ws = villager?.Workstation;
		if (ws == null)
		{
			return null;
		}
		return ws.ToVec3d().Add(0.5, 1.0, 0.5);
	}

	protected override void ApplyInteractionEffect()
	{
		if (!IsProfession())
		{
			return;
		}

		entity.AnimManager.StartAnimation(new AnimationMetaData
		{
			Animation = interactionAnimation,
			Code = interactionAnimation,
			AnimationSpeed = interactionAnimationSpeed,
			BlendMode = EnumAnimationBlendMode.Average
		}.Init());

		entity.World.RegisterCallback(delegate
		{
			entity.AnimManager.StopAnimation(interactionAnimation);
		}, animationDuration);
	}

	public override void FinishExecute(bool cancelled)
	{
		entity.AnimManager.StopAnimation(interactionAnimation);
		base.FinishExecute(cancelled);
	}

	private bool IsProfession()
	{
		if (string.IsNullOrEmpty(professionSuffix))
		{
			return true;
		}
		return entity?.Code?.Path?.EndsWith(professionSuffix) == true;
	}
}
