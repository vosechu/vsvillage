using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

public class AiTaskVillagerRangedAttack : AiTaskBaseTargetable
{
	private int durationMs;

	private int releaseAtMs;

	private long lastSearchTotalMs;

	private float minVertDist = 2f;

	private float minDist = 3f;

	private float maxDist = 15f;

	protected int searchWaitMs = 7000;

	private float startTimeStamp;

	private bool didThrow;

	private bool didRenderswitch;

	private float minTurnAnglePerSec;

	private float maxTurnAnglePerSec;

	private float curTurnRadPerSec;

	protected EntityProperties projectileType;

	protected AssetLocation shootingSound;

	protected AssetLocation drawingsound;

	private AnimationMetaData animationRelease;

	private bool animStarted;

	private float damage;

	public AiTaskVillagerRangedAttack(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		durationMs = taskConfig["durationMs"].AsInt(1500);
		releaseAtMs = taskConfig["releaseAtMs"].AsInt(1000);
		minDist = taskConfig["minDist"].AsFloat(3f);
		minVertDist = taskConfig["minVertDist"].AsFloat(2f);
		maxDist = taskConfig["maxDist"].AsFloat(15f);
		projectileType = entity.World.GetEntityType(new AssetLocation(taskConfig["projectile"].AsString()));
		if (taskConfig["drawingsound"].Exists)
		{
			drawingsound = new AssetLocation(taskConfig["drawingsound"].AsString());
		}
		if (taskConfig["shootingsound"].Exists)
		{
			shootingSound = new AssetLocation(taskConfig["shootingsound"].AsString());
		}
		if (taskConfig["animationRelase"].Exists)
		{
			animationRelease = new AnimationMetaData
			{
				Animation = taskConfig["animationRelase"].AsString(),
				Code = taskConfig["animationRelase"].AsString()
			}.Init();
		}
		damage = taskConfig["damage"].AsFloat(3f);
	}

	public override bool ShouldExecute()
	{
		if (cooldownUntilMs > entity.World.ElapsedMilliseconds)
		{
			return false;
		}
		bool needsSearch = false;
		if (lastSearchTotalMs + searchWaitMs < entity.World.ElapsedMilliseconds)
		{
			Entity obj = targetEntity;
			if (obj == null || !obj.Alive)
			{
				needsSearch = true;
			}
		}
		if (lastSearchTotalMs + searchWaitMs * 5 < entity.World.ElapsedMilliseconds)
		{
			needsSearch = true;
		}
		if (needsSearch)
		{
			float range = maxDist;
			lastSearchTotalMs = entity.World.ElapsedMilliseconds;
			targetEntity = partitionUtil.GetNearestInteractableEntity(((Entity)entity).ServerPos.XYZ, range, (Entity e) => base.IsTargetableEntity(e, range * 4f, false) && hasDirectContact(e, range * 4f, range / 2f));
		}
		return targetEntity?.Alive ?? false;
	}

	public override void StartExecute()
	{
		if (entity is EntityVillager entityVillager)
		{
			entityVillager.RightHandItemSlot?.Itemstack?.Attributes?.SetInt("renderVariant", 1);
			entityVillager.RightHandItemSlot.MarkDirty();
		}
		startTimeStamp = 0f;
		didThrow = false;
		didRenderswitch = false;
		animStarted = false;
		if (entity?.Properties.Server?.Attributes != null)
		{
			ITreeAttribute treeAttribute = entity.Properties.Server.Attributes.GetTreeAttribute("pathfinder");
			if (treeAttribute != null)
			{
				minTurnAnglePerSec = treeAttribute.GetFloat("minTurnAnglePerSec", 250f);
				maxTurnAnglePerSec = treeAttribute.GetFloat("maxTurnAnglePerSec", 450f);
			}
		}
		else
		{
			minTurnAnglePerSec = 250f;
			maxTurnAnglePerSec = 450f;
		}
		curTurnRadPerSec = minTurnAnglePerSec + (float)entity.World.Rand.NextDouble() * (maxTurnAnglePerSec - minTurnAnglePerSec);
		curTurnRadPerSec *= (float)Math.PI / 180f;
	}

	public override bool ContinueExecute(float dt)
	{
Vec3f vec3f = targetEntity.ServerPos.XYZFloat.Sub(((Entity)entity).ServerPos.XYZFloat);
		vec3f.Set((float)(targetEntity.ServerPos.X - ((Entity)entity).ServerPos.X), (float)(targetEntity.ServerPos.Y - ((Entity)entity).ServerPos.Y), (float)(targetEntity.ServerPos.Z - ((Entity)entity).ServerPos.Z));
		float end = (float)Math.Atan2(vec3f.X, vec3f.Z);
		float num = GameMath.AngleRadDistance(((Entity)entity).ServerPos.Yaw, end);
		((Entity)entity).ServerPos.Yaw += GameMath.Clamp(num, (0f - curTurnRadPerSec) * dt, curTurnRadPerSec * dt);
		((Entity)entity).ServerPos.Yaw = ((Entity)entity).ServerPos.Yaw % ((float)Math.PI * 2f);
		if ((double)Math.Abs(num) > 0.02)
		{
			return true;
		}
		if (animMeta != null && !animStarted)
		{
			animStarted = true;
			animMeta.EaseInSpeed = 1f;
			animMeta.EaseOutSpeed = 1f;
			entity.AnimManager.StartAnimation(animMeta);
			if (drawingsound != null)
			{
				entity.World.PlaySoundAt(drawingsound, entity, null, randomizePitch: false);
			}
		}
		startTimeStamp += dt;
		if (entity is EntityVillager && !didRenderswitch && startTimeStamp > (float)releaseAtMs / 2000f)
		{
			entity.RightHandItemSlot?.Itemstack?.Attributes?.SetInt("renderVariant", 3);
			entity.RightHandItemSlot.MarkDirty();
			didRenderswitch = true;
		}
		if (startTimeStamp > (float)releaseAtMs / 1000f && !didThrow && !entityInTheWay())
		{
			didThrow = true;
			EntityProjectile val = (EntityProjectile)entity.World.ClassRegistry.CreateEntity(projectileType);
			val.FiredBy = entity;
			val.Damage = damage;
			val.ProjectileStack = new ItemStack();
			val.DropOnImpactChance = 0f;
			val.World = entity.World;
			Vec3d vec3d = ((Entity)entity).ServerPos.AheadCopy(0.5).XYZ.AddCopy(0.0, entity.LocalEyePos.Y, 0.0);
			Vec3d vec3d2 = targetEntity.ServerPos.XYZ.AddCopy(0.0, targetEntity.LocalEyePos.Y, 0.0);
			double num2 = Math.Pow(vec3d.SquareDistanceTo(vec3d2), 0.1);
			Vec3d pos = (vec3d2 - vec3d + new Vec3d(0.0, vec3d.DistanceTo(vec3d2) / 16f, 0.0)).Normalize() * GameMath.Clamp(num2 - 1.0, 0.10000000149011612, 1.0);
			val.ServerPos.SetPos(((Entity)entity).ServerPos.AheadCopy(0.5).XYZ.Add(0.0, entity.LocalEyePos.Y, 0.0));
			val.ServerPos.Motion.Set(pos);
			val.Pos.SetFrom(val.ServerPos);
			val.SetRotation();
			entity.World.SpawnEntity(val);
			if (shootingSound != null)
			{
				entity.World.PlaySoundAt(shootingSound, entity, null, randomizePitch: false);
			}
			if (animationRelease != null)
			{
				animationRelease.EaseInSpeed = 1f;
				animationRelease.EaseOutSpeed = 1f;
				entity.AnimManager.StartAnimation(animationRelease);
			}
		}
		return startTimeStamp < (float)durationMs / 1000f;
	}

	private bool entityInTheWay()
	{
		EntitySelection entitySelection = new EntitySelection();
		BlockSelection blockSelection = new BlockSelection();
		entity.World.RayTraceForSelection(((Entity)entity).ServerPos.XYZ.AddCopy(entity.LocalEyePos), targetEntity.ServerPos.XYZ.AddCopy(targetEntity.LocalEyePos), ref blockSelection, ref entitySelection);
		return entitySelection?.Entity != targetEntity;
	}

	public override void FinishExecute(bool cancelled)
	{
		base.FinishExecute(cancelled);
		if (entity is EntityVillager)
		{
			entity.RightHandItemSlot?.Itemstack?.Attributes?.SetInt("renderVariant", 0);
			entity.RightHandItemSlot.MarkDirty();
		}
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

	public override bool IsTargetableEntity(Entity e, float range, bool ignoreEntityCode = false)
	{
		if (e == attackedByEntity && e != null && e.Alive)
		{
			return true;
		}
		return base.IsTargetableEntity(e, range, ignoreEntityCode);
	}

	public void OnAllyAttacked(Entity byEntity)
	{
		if (targetEntity == null || !targetEntity.Alive)
		{
			targetEntity = byEntity;
		}
	}
}
