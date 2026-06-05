using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

public class AiTaskVillagerGotoGatherspot : AiTaskGotoAndInteract
{
	private float offset;

	private BlockEntityVillagerBrazier brazier;

	private long lastExecutionTime;

	// N civilians converge on the brazier 18:30-20:54; crowd-avoidance would deadlock them.
	protected override bool RespectCrowdAvoidance => false;

	public AiTaskVillagerGotoGatherspot(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		offset = (float)entity.World.Rand.Next(taskConfig["minoffset"].AsInt(-50), taskConfig["maxoffset"].AsInt(50)) / 100f;
	}

    protected override void ApplyInteractionEffect()
    {
        if (brazier != null)
        {
            Block block = entity.World.BlockAccessor.GetBlock(brazier.Pos);
            if (block?.Code?.Path?.Contains("brazier") == true)
            {
                brazier.Ignite();
            }
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
			return null;
		}
		return getRandomPosNearby(vec3d);
	}

	public override bool ShouldExecute()
	{
		string entityPath = entity?.Code?.Path;
		if (entityPath != null && (entityPath.EndsWith("-soldier") || entityPath.EndsWith("-archer")))
		{
			return false;
		}
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
				return vec3d;
			}
		}
		return middle;
	}

	public override void FinishExecute(bool cancelled)
	{
		entity.Controls.StopAllMovement();
		base.FinishExecute(cancelled);
	}

	public override void StartExecute()
	{
		lastExecutionTime = entity.World.ElapsedMilliseconds;
		base.StartExecute();
	}
}
