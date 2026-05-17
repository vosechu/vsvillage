using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

public class AiTaskVillagerCultivateCrops : AiTaskGotoAndInteract
{
    private BlockEntityFarmland nearestFarmland;

    private Dictionary<BlockPos, long> recentlyCultivatedFarmland;
    // Reused per-call so the expired-key sweep doesn't allocate every tick.
    private readonly List<BlockPos> _expiredFarmlandKeys = new List<BlockPos>();

    private long farmlandCooldownMs;

    public AiTaskVillagerCultivateCrops(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        recentlyCultivatedFarmland = new Dictionary<BlockPos, long>();
        farmlandCooldownMs = (taskConfig?["farmlandCooldownSeconds"]?.AsInt(60) ?? 60) * 1000L;

        // Replace the default nod with hoe-till so the base class plays the
        // correct animation on arrival instead of nodding.
        interactAnim = new AnimationMetaData
        {
            Code = "hoe-till",
            Animation = "hoe-till",
            AnimationSpeed = 1f,
            BlendMode = EnumAnimationBlendMode.Average
        }.Init();
    }

    protected override Vec3d GetTargetPos()
    {
        if (!IsFarmer())
        {
            return null;
        }
        // Search around the farmer's current position so she can wander between
        // her workstation and fields without the task constantly anchoring her
        // back home.
        nearestFarmland = entity.Api.ModLoader.GetModSystem<POIRegistry>().GetNearestPoi(entity.Pos.XYZ, base.maxDistance, isValidFarmland) as BlockEntityFarmland;
        if (nearestFarmland == null)
        {
            return null;
        }
        BlockPos pos = nearestFarmland.Pos;
        return pos.ToVec3d().Add(0.5, 1.0, 0.5);
    }

    protected override void ApplyInteractionEffect()
    {
        if (!IsFarmer() || nearestFarmland == null || nearestFarmland.HasRipeCrop()) return;

        // Animation is already running via interactAnim - just schedule the crop advance.
        entity.World.RegisterCallback(delegate
        {
            PerformCultivation();
        }, 1500);
    }

    private bool isValidFarmland(IPointOfInterest poi)
    {
        return poi is BlockEntityFarmland blockEntityFarmland && blockEntityFarmland.GetCrop() != null && !blockEntityFarmland.HasRipeCrop() && !IsFarmlandOnCooldown(blockEntityFarmland.Pos) && entity.World.Rand.NextDouble() < 0.2;
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
        _expiredFarmlandKeys.Clear();
        foreach (KeyValuePair<BlockPos, long> item in recentlyCultivatedFarmland)
        {
            if (elapsedMilliseconds - item.Value > farmlandCooldownMs)
            {
                _expiredFarmlandKeys.Add(item.Key);
            }
        }
        for (int i = 0; i < _expiredFarmlandKeys.Count; i++)
        {
            recentlyCultivatedFarmland.Remove(_expiredFarmlandKeys[i]);
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
        if (nearestFarmland != null && !nearestFarmland.HasRipeCrop())
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
        }
    }
}