using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

public class AiTaskVillagerProfessionIdle : AiTaskGotoAndInteract
{
	private string professionSuffix;

	private string interactionAnimation;

	private float interactionAnimationSpeed;

	private int animationDuration;

	private bool plantFlowerpot;

	public AiTaskVillagerProfessionIdle(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		professionSuffix = taskConfig["professionSuffix"].AsString("");
		interactionAnimation = taskConfig["interactionAnimation"].AsString("hoe-till");
		interactionAnimationSpeed = taskConfig["interactionAnimationSpeed"].AsFloat(1f);
		animationDuration = taskConfig["animationDuration"].AsInt(2500);
		plantFlowerpot = taskConfig["plantFlowerpot"].AsBool();
	}

	protected override Vec3d GetTargetPos()
	{
		if (!IsProfession())
		{
			return null;
		}
		BlockPos ws = entity.GetBehavior<EntityBehaviorVillager>()?.Workstation;
		if (ws == null)
		{
			return null;
		}
		return ws.ToVec3d().Add(0.5, 1.0, 0.5);
	}

	protected override void ApplyInteractionEffect()
	{
		if (IsProfession())
		{
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
			if (plantFlowerpot)
			{
				TryPlantFlowerpot();
			}
		}
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
		EntityAgent entityAgent = entity;
		return entityAgent != null && entityAgent.Code?.Path?.EndsWith(professionSuffix) == true;
	}

	private void TryPlantFlowerpot()
	{
		EntityBehaviorVillager villager = entity.GetBehavior<EntityBehaviorVillager>();
		if (villager?.Workstation == null)
		{
			return;
		}
		Block horsetailPot = FindBlock("game:flowerpot-horsetail-free") ?? FindBlock("game:flowerpot-horsetail") ?? FindBlock("game:flowerpot-wildhorsetail-free");
		if (horsetailPot == null)
		{
			return;
		}
		IBlockAccessor ba = entity.World.BlockAccessor;
		BlockPos ws = villager.Workstation;
		BlockPos tmp = ws.Copy();
		for (int dx = -4; dx <= 4; dx++)
		{
			for (int dy = -1; dy <= 2; dy++)
			{
				for (int dz = -4; dz <= 4; dz++)
				{
					tmp.Set(ws.X + dx, ws.Y + dy, ws.Z + dz);
					Block b = ba.GetBlock(tmp);
					if (b != null && b.Code?.Path?.Contains("flowerpot") == true && !b.Code.Path.Contains("horsetail"))
					{
						ba.SetBlock(horsetailPot.Id, tmp);
						ba.TriggerNeighbourBlockUpdate(tmp);
						return;
					}
				}
			}
		}
	}

	private Block FindBlock(string code)
	{
		Block b = entity.World.GetBlock(new AssetLocation(code));
		return (b != null && b.Id != 0) ? b : null;
	}
}
