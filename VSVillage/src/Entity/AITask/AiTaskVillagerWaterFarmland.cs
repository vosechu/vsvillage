using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

public class AiTaskVillagerWaterFarmland : AiTaskGotoAndInteract
{
	private BlockEntityFarmland nearestFarmland;

	private Dictionary<BlockPos, long> recentlyWateredFarmland;

	private long waterCooldownMs;

	public AiTaskVillagerWaterFarmland(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		recentlyWateredFarmland = new Dictionary<BlockPos, long>();
		waterCooldownMs = ((taskConfig["waterCooldownSeconds"] != null) ? (taskConfig["waterCooldownSeconds"].AsInt(30) * 1000) : 30000);
	}

	protected override Vec3d GetTargetPos()
	{
		if (!IsFarmer())
		{
			return null;
		}
		// Search around the farmer's current position so she can wander between
		// her workstation and fields without the task constantly anchoring her
		// back home. The villagergotowork task brings her back periodically.
		POIRegistry poiReg = entity.Api.ModLoader.GetModSystem<POIRegistry>();
		BlockEntityFarmland driestFarmland = null;
		float lowestMoisture = float.MaxValue;
		poiReg.GetNearestPoi(entity.Pos.XYZ, base.maxDistance, delegate(IPointOfInterest poi)
		{
			if (!(poi is BlockEntityFarmland blockEntityFarmland))
			{
				return false;
			}
			if (blockEntityFarmland.GetCrop() == null)
			{
				return false;
			}
			if (blockEntityFarmland.HasRipeCrop())
			{
				return false;
			}
			if (IsFarmlandOnCooldown(blockEntityFarmland.Pos))
			{
				return false;
			}
			if (blockEntityFarmland.MoistureLevel >= 0.9f)
			{
				return false;
			}
			if (blockEntityFarmland.MoistureLevel < lowestMoisture)
			{
				lowestMoisture = blockEntityFarmland.MoistureLevel;
				driestFarmland = blockEntityFarmland;
			}
			return false;
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
		if (IsFarmer() && nearestFarmland != null)
		{
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
	}

	public override void FinishExecute(bool cancelled)
	{
		entity.AnimManager.StopAnimation("hoe-till");
		base.FinishExecute(cancelled);
	}

	private void PerformWatering()
	{
		if (nearestFarmland == null) return;

		IBlockAccessor ba = entity.World.BlockAccessor;
		BlockPos center = nearestFarmland.Pos;

		// Water the 3×3 area (9 blocks) centred on the target farmland block.
		for (int dx = -1; dx <= 1; dx++)
		{
			for (int dz = -1; dz <= 1; dz++)
			{
				BlockPos checkPos = new BlockPos(center.X + dx, center.Y, center.Z + dz);
				BlockEntityFarmland nearby = ba.GetBlockEntity<BlockEntityFarmland>(checkPos);
				if (nearby != null && nearby.MoistureLevel < 0.9f && !IsFarmlandOnCooldown(checkPos))
				{
					nearby.WaterFarmland(0.9f);
					MarkFarmlandWatered(checkPos);
				}
			}
		}

		entity.AnimManager.StopAnimation("hoe-till");
		SimpleParticleProperties particles = new SimpleParticleProperties(
			8f, 12f,
			ColorUtil.ToRgba(200, 100, 160, 220),
			nearestFarmland.Position.AddCopy(-0.3, 0.9, -0.3),
			nearestFarmland.Position.AddCopy(0.3, 1.2, 0.3),
			new Vec3f(-0.05f, 0.15f, -0.05f),
			new Vec3f(0.05f, 0.25f, 0.05f),
			1.2f, 0.4f, 0.08f);
		particles.MinPos = nearestFarmland.Position.AddCopy(0.5, 1.0, 0.5);
		entity.World.SpawnParticles(particles);
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
		long t;
		return recentlyWateredFarmland.TryGetValue(pos, out t) && now - t < waterCooldownMs;
	}

	private void MarkFarmlandWatered(BlockPos pos)
	{
		recentlyWateredFarmland[pos.Copy()] = entity.World.ElapsedMilliseconds;
	}

	private bool IsFarmer()
	{
		EntityAgent entityAgent = entity;
		return entityAgent != null && entityAgent.Code?.Path?.EndsWith("-farmer") == true;
	}
}
