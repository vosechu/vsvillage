using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

public class AiTaskVillagerCultivateCrops : AiTaskGotoAndInteract
{
	private BlockEntityFarmland nearestFarmland;

	private Dictionary<BlockPos, long> recentlyCultivatedFarmland;

	private long farmlandCooldownMs;

	public AiTaskVillagerCultivateCrops(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		recentlyCultivatedFarmland = new Dictionary<BlockPos, long>();
		if (taskConfig["farmlandCooldownSeconds"] != null)
		{
			farmlandCooldownMs = taskConfig["farmlandCooldownSeconds"].AsInt(60) * 1000;
		}
		else
		{
			farmlandCooldownMs = 60000L;
		}
	}

	protected override Vec3d GetTargetPos()
	{
		if (!IsFarmer())
		{
			return null;
		}
		nearestFarmland = entity.Api.ModLoader.GetModSystem<POIRegistry>().GetNearestPoi(((Entity)entity).ServerPos.XYZ, base.maxDistance, isValidFarmland) as BlockEntityFarmland;
		if (nearestFarmland == null)
		{
			return null;
		}
		BlockPos pos = nearestFarmland.Pos;
		entity.World.Logger.Debug("Cultivate: Found farmland at " + pos.ToString() + ", targeting directly");
		return pos.ToVec3d().Add(0.5, 1.0, 0.5);
	}

	protected override void ApplyInteractionEffect()
	{
		if (IsFarmer() && nearestFarmland != null && nearestFarmland.HasUnripeCrop())
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
				PerformCultivation();
			}, 1500);
		}
	}

	private bool isValidFarmland(IPointOfInterest poi)
	{
		return poi is BlockEntityFarmland blockEntityFarmland && blockEntityFarmland.HasUnripeCrop() && !IsFarmlandOnCooldown(blockEntityFarmland.Pos) && entity.World.Rand.NextDouble() < 0.2;
	}

	public override void FinishExecute(bool cancelled)
	{
		entity.AnimManager.StopAnimation("hoe-till");
		base.FinishExecute(cancelled);
	}

	private bool IsFarmer()
	{
		return entity != null && entity.Code != null && entity.Code.Path != null && entity.Code.Path.EndsWith("-farmer");
	}

	private bool IsFarmlandOnCooldown(BlockPos pos)
	{
		long elapsedMilliseconds = entity.World.ElapsedMilliseconds;
		List<BlockPos> list = new List<BlockPos>();
		foreach (KeyValuePair<BlockPos, long> item in recentlyCultivatedFarmland)
		{
			if (elapsedMilliseconds - item.Value > farmlandCooldownMs)
			{
				list.Add(item.Key);
			}
		}
		for (int i = 0; i < list.Count; i++)
		{
			recentlyCultivatedFarmland.Remove(list[i]);
		}
		long value;
		return recentlyCultivatedFarmland.TryGetValue(pos, out value) && elapsedMilliseconds - value < farmlandCooldownMs;
	}

	private void MarkFarmlandCultivated(BlockPos pos)
	{
		recentlyCultivatedFarmland[pos.Copy()] = entity.World.ElapsedMilliseconds;
	}

	private void PerformCultivation()
	{
		if (nearestFarmland != null && nearestFarmland.HasUnripeCrop())
		{
			double totalHours = entity.World.Calendar.TotalHours;
			double hoursForNextStage = nearestFarmland.GetHoursForNextStage();
			double currentTotalHours = totalHours + hoursForNextStage + 1.0;
			nearestFarmland.TryGrowCrop(currentTotalHours);
			MarkFarmlandCultivated(nearestFarmland.Pos);
			SimpleParticleProperties simpleParticleProperties = new SimpleParticleProperties(10f, 15f, ColorUtil.ToRgba(255, 255, 233, 83), nearestFarmland.Position.AddCopy(-0.4, 0.8, -0.4), nearestFarmland.Position.AddCopy(-0.6, 0.8, -0.6), new Vec3f(-0.25f, 0f, -0.25f), new Vec3f(0.25f, 0f, 0.25f), 2f, 1f, 0.2f);
			simpleParticleProperties.MinPos = nearestFarmland.Position.AddCopy(0.5, 1.0, 0.5);
			entity.World.SpawnParticles(simpleParticleProperties);
			entity.AnimManager.StopAnimation("hoe-till");
			entity.World.Logger.Debug("Cultivate: Performed cultivation on farmland at " + nearestFarmland.Pos.ToString());
		}
	}
}
