using System.Linq;
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
		if (!IsHerbalist())
		{
			return null;
		}
		Entity[] array = entity.World.GetEntitiesAround(((Entity)entity).ServerPos.XYZ, base.maxDistance, 5f, (Entity entity) => entity is EntityVillager || entity is EntityTrader || entity is EntityPlayer);
		EntityBehaviorVillager behavior = entity.GetBehavior<EntityBehaviorVillager>();
		if (behavior?.Village != null)
		{
			Entity[] second = (from villager in behavior.Village.Villagers
				where villager != null && villager.entity != null && villager.entity.Alive
				select villager.entity).ToArray();
			array = array.Concat(second).ToArray();
		}
		int num = 0;
		float num2 = 0f;
		for (int num3 = 0; num3 < array.Length; num3++)
		{
			EntityBehaviorHealth behavior2 = array[num3].GetBehavior<EntityBehaviorHealth>();
			if (behavior2 != null && num2 < behavior2.MaxHealth - behavior2.Health)
			{
				num2 = behavior2.MaxHealth - behavior2.Health;
				num = num3;
			}
			if (behavior2 != null && behavior2.Health <= 0f)
			{
				num2 = float.MaxValue;
				num = num3;
			}
		}
		if (num2 > 0.5f)
		{
			woundedEntity = array[num];
		}
		return woundedEntity?.ServerPos?.XYZ;
	}

	protected override bool InteractionPossible()
	{
		return IsHerbalist() && woundedEntity != null && ((Entity)entity).ServerPos.SquareDistanceTo(woundedEntity.ServerPos) < 9f;
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

	private bool IsHerbalist()
	{
		EntityAgent entityAgent = entity;
		bool? flag;
		if (entityAgent == null)
		{
			flag = null;
		}
		else
		{
			AssetLocation code = entityAgent.Code;
			flag = ((!(code == null)) ? code.Path?.EndsWith("-herbalist") : ((bool?)null));
		}
		bool? flag2 = flag;
		return flag2 == true;
	}

	private void PerformHealing()
	{
		if (woundedEntity != null)
		{
			if (woundedEntity.Alive)
			{
				woundedEntity.ReceiveDamage(new DamageSource
				{
					DamageTier = 0,
					HitPosition = woundedEntity.ServerPos.XYZ,
					Source = EnumDamageSource.Internal,
					SourceEntity = entity,
					Type = EnumDamageType.Heal
				}, 100f);
			}
			else
			{
				woundedEntity.Revive();
			}
			Vec3d xYZ = woundedEntity.ServerPos.XYZ;
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
