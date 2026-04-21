using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace VsVillage;

public class AiTaskVillagerSeekEntity : AiTaskSeekEntity
{
	protected float minRange;

	protected long lastCheckTotalMs { get; set; }

	protected long lastCheckCooldown { get; set; } = 500L;

	protected long lastCallForHelp { get; set; }

	public AiTaskVillagerSeekEntity(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		minRange = taskConfig["minRange"].AsFloat();
	}

	public override bool ShouldExecute()
	{
		if (lastCheckTotalMs + lastCheckCooldown > entity.World.ElapsedMilliseconds)
		{
			return false;
		}
		lastCheckTotalMs = entity.World.ElapsedMilliseconds;
		if (targetEntity != null && targetEntity.Alive && entityInReach(targetEntity))
		{
			targetPos = targetEntity.ServerPos.XYZ;
			return true;
		}
		targetEntity = null;
		if (attackedByEntity != null && attackedByEntity.Alive && entityInReach(attackedByEntity))
		{
			targetEntity = attackedByEntity;
			targetPos = targetEntity.ServerPos.XYZ;
			return true;
		}
		attackedByEntity = null;
		if (lastSearchTotalMs + searchWaitMs < entity.World.ElapsedMilliseconds)
		{
			lastSearchTotalMs = entity.World.ElapsedMilliseconds;
			targetEntity = partitionUtil.GetNearestInteractableEntity(((Entity)entity).ServerPos.XYZ, seekingRange, (Entity potentialTarget) => ((AiTaskBaseTargetable)this).IsTargetableEntity(potentialTarget, seekingRange, false));
			if (targetEntity != null && targetEntity.Alive && entityInReach(targetEntity))
			{
				targetPos = targetEntity.ServerPos.XYZ;
				return true;
			}
			targetEntity = null;
		}
		return false;
	}

	private bool entityInReach(Entity candidate)
	{
		double num = candidate.ServerPos.SquareDistanceTo(((Entity)entity).ServerPos.XYZ);
		if (num < (double)(seekingRange * seekingRange * 2f))
		{
			return num > (double)(minRange * minRange);
		}
		return false;
	}

	public override void StartExecute()
	{
		base.StartExecute();
	}

	public override bool ContinueExecute(float dt)
	{
		if (targetEntity != null && entityInReach(targetEntity))
		{
			return base.ContinueExecute(dt);
		}
		return false;
	}

	public override void OnEntityHurt(DamageSource source, float damage)
	{
		Entity causeEntity = source.CauseEntity;
		if (causeEntity != null && causeEntity.HasBehavior<EntityBehaviorVillager>())
		{
			return;
		}
		Entity sourceEntity = source.SourceEntity;
		if (sourceEntity != null && sourceEntity.HasBehavior<EntityBehaviorVillager>())
		{
			return;
		}
		base.OnEntityHurt(source, damage);
		if (source.Type != EnumDamageType.Heal && (source.CauseEntity != null || source.SourceEntity != null) && lastCallForHelp + 5000 < entity.World.ElapsedMilliseconds)
		{
			lastCallForHelp = entity.World.ElapsedMilliseconds;
			Entity[] entitiesAround = entity.World.GetEntitiesAround(((Entity)entity).ServerPos.XYZ, 15f, 4f, delegate(Entity entity)
			{
				EntityBehaviorVillager behavior = entity.GetBehavior<EntityBehaviorVillager>();
				return behavior != null && behavior.Profession == EnumVillagerProfession.soldier;
			});
			for (int num = 0; num < entitiesAround.Length; num++)
			{
				AiTaskManager taskManager = entitiesAround[num].GetBehavior<EntityBehaviorTaskAI>().TaskManager;
				taskManager.GetTask<AiTaskVillagerSeekEntity>()?.OnAllyAttacked(source.SourceEntity);
				taskManager.GetTask<AiTaskVillagerMeleeAttack>()?.OnAllyAttacked(source.SourceEntity);
				taskManager.GetTask<AiTaskVillagerRangedAttack>()?.OnAllyAttacked(source.SourceEntity);
			}
		}
	}

	public void OnAllyAttacked(Entity byEntity)
	{
		if (targetEntity == null || !targetEntity.Alive)
		{
			targetEntity = byEntity;
		}
	}

	public override bool IsTargetableEntity(Entity e, float range, bool ignoreEntityCode = false)
	{
		if (e == attackedByEntity && e != null && e.Alive)
		{
			return true;
		}
		return base.IsTargetableEntity(e, range, ignoreEntityCode);
	}
}
