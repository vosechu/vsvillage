using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsVillage;

/// <summary>
/// Morning muster task: villagers walk to the mayor station (town-centre workstation)
/// before heading off to their jobs. If no mayor station is registered with the village
/// the task falls back to any available gatherplace (brazier).
///
/// Replaces the old morning villagergotogather slot. The evening villagergotogather
/// (17:00-20:30) is kept unchanged.
/// </summary>
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
		// No special interaction — villagers are just gathering / milling around.
	}

	protected override Vec3d GetTargetPos()
	{
		Village village = entity.GetBehavior<EntityBehaviorVillager>()?.Village;
		if (village == null)
		{
			entity.World.Logger.Debug("GotoMayor: No village found for entity " + entity.EntityId);
			return null;
		}

		// Prefer the mayor workstation (the town-centre station the player places).
		BlockPos mayorPos = null;
		foreach (VillagerWorkstation ws in village.Workstations.Values)
		{
			if (ws.Profession == EnumVillagerProfession.mayor)
			{
				mayorPos = ws.Pos;
				break;
			}
		}

		// Fall back to any gatherplace brazier if no mayor station is registered.
		if (mayorPos == null)
		{
			mayorPos = village.FindRandomGatherplace();
		}

		if (mayorPos == null)
		{
			entity.World.Logger.Debug("GotoMayor: No mayor station or gatherplace found");
			return null;
		}

		entity.World.Logger.Notification("GotoMayor: Entity " + entity.EntityId + " heading to town centre at " + mayorPos);
		// Pick a random spot in a small radius so villagers don't all stack at one point.
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
		entity.World.Logger.Notification("GotoMayor: StartExecute for entity " + entity.EntityId);
		base.StartExecute();
	}

	public override bool ContinueExecute(float dt)
	{
		return base.ContinueExecute(dt);
	}

	public override void FinishExecute(bool cancelled)
	{
		entity.World.Logger.Notification("GotoMayor: FinishExecute for entity " + entity.EntityId);
		entity.Controls.StopAllMovement();
		base.FinishExecute(cancelled);
	}

	private Vec3d GetRandomPosNear(Vec3d centre)
	{
		if (centre == null) return null;
		IBlockAccessor ba = entity.World.BlockAccessor;
		for (int i = 0; i < 8; i++)
		{
			int dx = entity.World.Rand.Next(-4, 5);
			int dz = entity.World.Rand.Next(-4, 5);
			Vec3d candidate = centre.AddCopy(dx, 0, dz);
			// Require clear headroom — same check as getRandomPosNearby in GotoGatherspot.
			if (ba.GetBlock(candidate.AsBlockPos.Up()).Id == 0)
			{
				return candidate;
			}
		}
		return centre;
	}
}
