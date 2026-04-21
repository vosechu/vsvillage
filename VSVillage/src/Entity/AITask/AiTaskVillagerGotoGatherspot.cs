using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

public class AiTaskVillagerGotoGatherspot : AiTaskGotoAndInteract
{
	private float offset;

	private BlockEntityVillagerBrazier brazier;

	private long lastExecutionTime;

	public AiTaskVillagerGotoGatherspot(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		offset = (float)entity.World.Rand.Next(taskConfig["minoffset"].AsInt(-50), taskConfig["maxoffset"].AsInt(50)) / 100f;
	}

	protected override void ApplyInteractionEffect()
	{
		if (brazier != null)
		{
			brazier.Ignite();
			entity.World.Logger.Notification("Villager ignited brazier");
		}
		brazier = null;
	}

	protected override Vec3d GetTargetPos()
	{
		ICoreAPI api = entity.Api;
		BlockPos blockPos = (entity.GetBehavior<EntityBehaviorVillager>()?.Village)?.FindRandomGatherplace();
		brazier = ((blockPos != null) ? api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerBrazier>(blockPos) : null);
		Vec3d vec3d = ((brazier != null) ? brazier.Position : null);
		if (vec3d == null)
		{
			entity.World.Logger.Debug("No gatherplace found for villager");
			return null;
		}
		entity.World.Logger.Notification("Villager going to gatherplace at " + vec3d.ToString());
		return getRandomPosNearby(vec3d);
	}

	public override bool ShouldExecute()
	{
		if (!IntervalUtil.matchesCurrentTime(duringDayTimeFrames, entity.World, offset))
		{
			return false;
		}
		long elapsedMilliseconds = entity.World.ElapsedMilliseconds;
		if (elapsedMilliseconds - lastExecutionTime < 10000)
		{
			return false;
		}
		return base.ShouldExecute();
	}

	private Vec3d getRandomPosNearby(Vec3d middle)
	{
		if (middle == null)
		{
			return null;
		}
		IBlockAccessor blockAccessor = entity.World.BlockAccessor;
		for (int i = 0; i < 5; i++)
		{
			int num = entity.World.Rand.Next(-3, 4);
			int num2 = entity.World.Rand.Next(-3, 4);
			Vec3d vec3d = middle.AddCopy(num2, 0f, num);
			if (blockAccessor.GetBlock(vec3d.AsBlockPos.Up()).Id == 0)
			{
				entity.World.Logger.Debug("Found random position near gatherplace: " + vec3d.ToString());
				return vec3d;
			}
		}
		entity.World.Logger.Debug("Could not find open position, using center");
		return middle;
	}

	public override bool ContinueExecute(float dt)
	{
		return base.ContinueExecute(dt);
	}

	public override void FinishExecute(bool cancelled)
	{
		entity.World.Logger.Notification("GotoGatherspot: Finishing execution");
		entity.Controls.StopAllMovement();
		base.FinishExecute(cancelled);
	}

	public override void StartExecute()
	{
		lastExecutionTime = entity.World.ElapsedMilliseconds;
		entity.World.Logger.Notification("GotoGatherspot: Starting execution");
		base.StartExecute();
	}
}
