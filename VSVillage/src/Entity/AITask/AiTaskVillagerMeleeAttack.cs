using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace VsVillage;

public class AiTaskVillagerMeleeAttack : AiTaskMeleeAttack
{
	public AnimationMetaData baseAnimMeta { get; set; }

	public AnimationMetaData stabAnimMeta { get; set; }

	public AnimationMetaData slashAnimMeta { get; set; }

	public float unarmedDamage { get; set; }

	public float armedDamageMultiplier { get; set; }

	public AiTaskVillagerMeleeAttack(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		baseAnimMeta = animMeta;
		unarmedDamage = damage;
		armedDamageMultiplier = taskConfig["armedDamageMultiplier"].AsFloat(4f);
		if (taskConfig["stabanimation"].Exists)
		{
			stabAnimMeta = new AnimationMetaData
			{
				Code = taskConfig["stabanimation"].AsString()?.ToLowerInvariant(),
				Animation = taskConfig["stabanimation"].AsString()?.ToLowerInvariant(),
				AnimationSpeed = taskConfig["stabanimationSpeed"].AsFloat(1f)
			}.Init();
		}
		if (taskConfig["slashanimation"].Exists)
		{
			slashAnimMeta = new AnimationMetaData
			{
				Code = taskConfig["slashanimation"].AsString()?.ToLowerInvariant(),
				Animation = taskConfig["slashanimation"].AsString()?.ToLowerInvariant(),
				AnimationSpeed = taskConfig["slashanimationSpeed"].AsFloat(1f)
			}.Init();
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

	public override void StartExecute()
	{
		string path = entity.Code?.Path ?? "";
		bool isCombatant = path.EndsWith("-soldier") || path.EndsWith("-archer");

		if (entity.RightHandItemSlot != null && !entity.RightHandItemSlot.Empty)
		{
			damage = Math.Max(entity.RightHandItemSlot.Itemstack.Item.AttackPower * armedDamageMultiplier, unarmedDamage);
			animMeta = entity.RightHandItemSlot.Itemstack.Item.Code.Path.Contains("spear")
				? stabAnimMeta
				: slashAnimMeta;
		}
		else if (isCombatant)
		{
			// Soldier/archer: use armed damage and animations even without a held item,
			// since their weapon is in the gear WEAPON slot rather than the held slot.
			damage = unarmedDamage * armedDamageMultiplier;
			animMeta = (stabAnimMeta != null && path.EndsWith("-archer")) ? stabAnimMeta : (slashAnimMeta ?? baseAnimMeta);
		}
		else
		{
			damage = unarmedDamage;
			animMeta = baseAnimMeta;
		}
		base.StartExecute();
	}

	public override void OnEntityHurt(DamageSource source, float damage)
	{
		Entity causeEntity = source.CauseEntity;
		if (causeEntity == null || !causeEntity.HasBehavior<EntityBehaviorVillager>())
		{
			Entity sourceEntity = source.SourceEntity;
			if (sourceEntity == null || !sourceEntity.HasBehavior<EntityBehaviorVillager>())
			{
				base.OnEntityHurt(source, damage);
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
}
