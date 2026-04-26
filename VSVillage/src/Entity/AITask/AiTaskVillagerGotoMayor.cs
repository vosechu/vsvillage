using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

public class AiTaskVillagerGotoMayor : AiTaskGotoAndInteract
{
	private float offset;

	private long lastExecutionTime;

	public AiTaskVillagerGotoMayor(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		offset = (float)entity.World.Rand.Next(taskConfig["minoffset"].AsInt(-30), taskConfig["maxoffset"].AsInt(30)) / 100f;
	}

	protected override void ApplyInteractionEffect()
	{
	}

	protected override Vec3d GetTargetPos()
	{
		Village village = entity.GetBehavior<EntityBehaviorVillager>()?.Village;
		if (village == null)
		{
			return null;
		}
		BlockPos mayorPos = null;
		foreach (VillagerWorkstation ws in village.Workstations.Values)
		{
			if (ws.Profession == EnumVillagerProfession.mayor)
			{
				mayorPos = ws.Pos;
				break;
			}
		}
		if (mayorPos == null)
		{
			mayorPos = village.FindRandomGatherplace();
		}
		if (mayorPos == null)
		{
			return null;
		}
		return GetRandomPosNear(mayorPos.ToVec3d().Add(0.5, 0.5, 0.5));
	}

	public override bool ShouldExecute()
	{
		if (!IntervalUtil.matchesCurrentTime(duringDayTimeFrames, entity.World, offset))
		{
			return false;
		}
		long elapsedMilliseconds = entity.World.ElapsedMilliseconds;
		if (elapsedMilliseconds - lastExecutionTime < 20000)
		{
			return false;
		}
		return base.ShouldExecute();
	}

	public override void StartExecute()
	{
		lastExecutionTime = entity.World.ElapsedMilliseconds;
		base.StartExecute();
	}

	public override void FinishExecute(bool cancelled)
	{
		entity.Controls.StopAllMovement();
		base.FinishExecute(cancelled);
	}

	private Vec3d GetRandomPosNear(Vec3d centre)
	{
		if (centre == null)
		{
			return null;
		}
		IBlockAccessor ba = entity.World.BlockAccessor;
		for (int i = 0; i < 8; i++)
		{
			int dx = entity.World.Rand.Next(-4, 5);
			int dz = entity.World.Rand.Next(-4, 5);
			Vec3d candidate = centre.AddCopy(dx, 0f, dz);
			if (ba.GetBlock(candidate.AsBlockPos.Up()).Id == 0)
			{
				return candidate;
			}
		}
		return centre;
	}
}
