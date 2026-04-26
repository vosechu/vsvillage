using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

public class AiTaskHealWounded : AiTaskGotoAndInteract
{
	public Entity woundedEntity;

	public AiTaskHealWounded(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
	}

	protected override Vec3d GetTargetPos()
	{
		if (!IsHerbalist()) return null;

		// Phase 1 — scan nearby entities (no allocation beyond the entity array VS returns).
		Entity[] nearby = entity.World.GetEntitiesAround(entity.Pos.XYZ, base.maxDistance, 5f,
			e => e is EntityVillager || e is EntityTrader || e is EntityPlayer);

		Entity best = null;
		float bestDeficit = 0f;
		foreach (Entity candidate in nearby)
			ScoreCandidate(candidate, ref best, ref bestDeficit);

		// Phase 2 — only scan village-wide if nobody nearby needs healing.
		// Iterates VillagerSaveData directly to avoid the Village.Villagers property
		// which allocates a fresh list + GetBehavior on every call.
		if (best == null)
		{
			EntityBehaviorVillager beh = entity.GetBehavior<EntityBehaviorVillager>();
			if (beh?.Village != null)
			{
				foreach (VillagerData data in beh.Village.VillagerSaveData.Values)
				{
					Entity e = entity.World.GetEntityById(data.Id);
					if (e == null || !e.Alive) continue;
					ScoreCandidate(e, ref best, ref bestDeficit);
				}
			}
		}

		woundedEntity = (bestDeficit > 0.5f) ? best : null;
		return woundedEntity?.Pos?.XYZ;
	}

	/// <summary>Updates <paramref name="best"/> if <paramref name="candidate"/> has a
	/// larger health deficit or is dead (needs reviving).</summary>
	private static void ScoreCandidate(Entity candidate, ref Entity best, ref float bestDeficit)
	{
		EntityBehaviorHealth health = candidate.GetBehavior<EntityBehaviorHealth>();
		if (health == null) return;
		if (health.Health <= 0f)
		{
			bestDeficit = float.MaxValue;
			best = candidate;
			return;
		}
		float deficit = health.MaxHealth - health.Health;
		if (deficit > bestDeficit)
		{
			bestDeficit = deficit;
			best = candidate;
		}
	}

	protected override bool InteractionPossible()
	{
		return IsHerbalist() && woundedEntity != null && entity.Pos.SquareDistanceTo(woundedEntity.Pos) < 9f;
	}

	protected override void ApplyInteractionEffect()
	{
		if (IsHerbalist())
		{
			entity.AnimManager.StartAnimation(new AnimationMetaData
			{
				Animation = "holdbothhands",
				Code = "holdbothhands",
				AnimationSpeed = 1f,
				BlendMode = EnumAnimationBlendMode.Average
			}.Init());
			entity.World.RegisterCallback(delegate
			{
				PerformHealing();
			}, 1000);
		}
	}

	private bool IsHerbalist() => entity?.Code?.Path?.EndsWith("-herbalist") == true;

	private void PerformHealing()
	{
		if (woundedEntity != null)
		{
			if (woundedEntity.Alive)
			{
				woundedEntity.ReceiveDamage(new DamageSource
				{
					DamageTier = 0,
					HitPosition = woundedEntity.Pos.XYZ,
					Source = EnumDamageSource.Internal,
					SourceEntity = entity,
					Type = EnumDamageType.Heal
				}, 100f);
			}
			else
			{
				woundedEntity.Revive();
			}
			Vec3d xYZ = woundedEntity.Pos.XYZ;
			SimpleParticleProperties simpleParticleProperties = new SimpleParticleProperties(20f, 30f, ColorUtil.ToRgba(75, 146, 175, 222), xYZ.AddCopy(-0.3, 0.5, -0.3), xYZ.AddCopy(0.3, 2.0, 0.3), new Vec3f(-0.25f, 0f, -0.25f), new Vec3f(0.25f, 0.5f, 0.25f), 0.8f, -0.075f, 0.5f, 3f, EnumParticleModel.Quad);
			simpleParticleProperties.MinPos = xYZ.AddCopy(-0.5, 0.0, -0.5);
			simpleParticleProperties.SelfPropelled = true;
			entity.World.SpawnParticles(simpleParticleProperties);
			SimpleParticleProperties simpleParticleProperties2 = new SimpleParticleProperties(15f, 20f, ColorUtil.ToRgba(255, 255, 255, 200), xYZ.AddCopy(-0.2, 0.5, -0.2), xYZ.AddCopy(0.2, 1.5, 0.2), new Vec3f(-0.1f, 0.1f, -0.1f), new Vec3f(0.1f, 0.3f, 0.1f), 0.5f, 0f, 0.2f, 0.8f, EnumParticleModel.Quad);
			simpleParticleProperties2.MinPos = xYZ.AddCopy(-0.3, 0.5, -0.3);
			entity.World.SpawnParticles(simpleParticleProperties2);
			entity.AnimManager.StopAnimation("holdbothhands");
			woundedEntity = null;
		}
	}
}
