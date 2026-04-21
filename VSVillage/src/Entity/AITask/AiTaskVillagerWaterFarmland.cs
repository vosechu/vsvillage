using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// Farmer task: walks to farmland with unripe crops and waters it,
/// also watering adjacent blocks. Fires more frequently than cultivate
/// to keep farmers visibly busy.
/// </summary>
public class AiTaskVillagerWaterFarmland : AiTaskGotoAndInteract
{
	private BlockEntityFarmland nearestFarmland;

	private Dictionary<BlockPos, long> recentlyWateredFarmland;

	private long waterCooldownMs;

	public AiTaskVillagerWaterFarmland(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		recentlyWateredFarmland = new Dictionary<BlockPos, long>();
		waterCooldownMs = (taskConfig["waterCooldownSeconds"] != null)
			? taskConfig["waterCooldownSeconds"].AsInt(30) * 1000
			: 30000L;
	}

	protected override Vec3d GetTargetPos()
	{
		if (!IsFarmer())
		{
			return null;
		}

		// Scan all farmland in range and pick the driest (lowest moisture) that needs water
		POIRegistry poiReg = entity.Api.ModLoader.GetModSystem<POIRegistry>();
		Vec3d myPos = ((Entity)entity).ServerPos.XYZ;
		BlockEntityFarmland driestFarmland = null;
		float lowestMoisture = float.MaxValue;

		poiReg.GetNearestPoi(myPos, maxDistance, poi =>
		{
			if (!(poi is BlockEntityFarmland bef)) return false;
			if (!bef.HasUnripeCrop()) return false;
			if (IsFarmlandOnCooldown(bef.Pos)) return false;
			if (bef.MoistureLevel >= 0.75f) return false;
			if (bef.MoistureLevel < lowestMoisture)
			{
				lowestMoisture = bef.MoistureLevel;
				driestFarmland = bef;
			}
			return false; // keep scanning all
		});

		nearestFarmland = driestFarmland;
		if (nearestFarmland == null)
		{
			return null;
		}
		return nearestFarmland.Pos.ToVec3d().Add(0.5, 1.0, 0.5);
	}

	protected override void ApplyInteractionEffect()
	{
		if (!IsFarmer() || nearestFarmland == null)
		{
			return;
		}

		entity.AnimManager.StartAnimation(new AnimationMetaData
		{
			Animation = "hoe-till",
			Code = "hoe-till",
			AnimationSpeed = 1f,
			BlendMode = EnumAnimationBlendMode.Average
		}.Init());

		entity.World.RegisterCallback(delegate
		{
			PerformWatering();
		}, 1500);
	}

	public override void FinishExecute(bool cancelled)
	{
		entity.AnimManager.StopAnimation("hoe-till");
		base.FinishExecute(cancelled);
	}

	private void PerformWatering()
	{
		if (nearestFarmland == null)
		{
			return;
		}

		// WaterFarmland(dt, waterNeighbours=true) handles target block + adjacent blocks
		// dt=0.5 adds 0.25 moisture to target, reduced to adjacent blocks
		nearestFarmland.WaterFarmland(0.5f, true);

		MarkFarmlandWatered(nearestFarmland.Pos);
		entity.AnimManager.StopAnimation("hoe-till");

		SimpleParticleProperties particles = new SimpleParticleProperties(
			8f, 12f, ColorUtil.ToRgba(200, 100, 160, 220),
			nearestFarmland.Position.AddCopy(-0.3, 0.9, -0.3),
			nearestFarmland.Position.AddCopy(0.3, 1.2, 0.3),
			new Vec3f(-0.05f, 0.15f, -0.05f),
			new Vec3f(0.05f, 0.25f, 0.05f),
			1.2f, 0.4f, 0.08f
		);
		particles.MinPos = nearestFarmland.Position.AddCopy(0.5, 1.0, 0.5);
		entity.World.SpawnParticles(particles);

		entity.World.Logger.Debug("Water: Watered farmland at " + nearestFarmland.Pos.ToString());
	}

	private bool isValidFarmland(IPointOfInterest poi)
	{
		if (!(poi is BlockEntityFarmland bef))
		{
			return false;
		}
		return bef.HasUnripeCrop()
			&& bef.MoistureLevel < 0.75f
			&& !IsFarmlandOnCooldown(bef.Pos);
	}

	private bool IsFarmlandOnCooldown(BlockPos pos)
	{
		long now = entity.World.ElapsedMilliseconds;
		List<BlockPos> expired = new List<BlockPos>();
		foreach (KeyValuePair<BlockPos, long> kv in recentlyWateredFarmland)
		{
			if (now - kv.Value > waterCooldownMs)
			{
				expired.Add(kv.Key);
			}
		}
		foreach (BlockPos k in expired)
		{
			recentlyWateredFarmland.Remove(k);
		}
		return recentlyWateredFarmland.TryGetValue(pos, out long t) && now - t < waterCooldownMs;
	}

	private void MarkFarmlandWatered(BlockPos pos)
	{
		recentlyWateredFarmland[pos.Copy()] = entity.World.ElapsedMilliseconds;
	}

	private bool IsFarmer()
	{
		return entity?.Code?.Path?.EndsWith("-farmer") == true;
	}
}
