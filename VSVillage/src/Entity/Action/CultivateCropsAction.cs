using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

public class CultivateCropsAction : EntityActionBase
{
	public const string ActionType = "CultivateCrops";

	public float WalkSpeed = 0.02f;

	public string AnimCode = "walk";

	public VillagePointOfInterest Target;

	private bool isFinished = true;

	private BlockEntityFarmland targetFarmland;

	public override string Type => "CultivateCrops";

	public override void Start(EntityActivity entityActivity)
	{
		EntityBehaviorVillager behavior = vas.Entity.GetBehavior<EntityBehaviorVillager>();
		if (behavior == null)
		{
			isFinished = true;
			return;
		}
		POIRegistry modSystem = vas.Entity.Api.ModLoader.GetModSystem<POIRegistry>();
		targetFarmland = modSystem.GetNearestPoi(((Entity)vas.Entity).ServerPos.XYZ, 40f, IsValidFarmland) as BlockEntityFarmland;
		if (targetFarmland == null)
		{
			isFinished = true;
			return;
		}
		isFinished = false;
		BlockPos asBlockPos = ((Entity)vas.Entity).ServerPos.AsBlockPos;
		BlockPos asBlockPos2 = targetFarmland.Position.AsBlockPos;
		List<VillagerPathNode> list = behavior.Pathfind.FindPath(asBlockPos, asBlockPos2, behavior.Village);
		if (list == null)
		{
			isFinished = true;
			return;
		}
		vas.Entity.AnimManager.StartAnimation(AnimCode);
		vas.wppathTraverser.FollowRoute(behavior.Pathfind.ToWaypoints(list), WalkSpeed, 0.2f, OnArrived, OnStuck);
	}

	private void OnArrived()
	{
		if (targetFarmland == null)
		{
			Finish();
			return;
		}
		vas.Entity.AnimManager.StartAnimation(new AnimationMetaData
		{
			Animation = "hoe-till",
			Code = "hoe-till",
			AnimationSpeed = 1f,
			BlendMode = EnumAnimationBlendMode.Average
		}.Init());
		vas.Entity.World.RegisterCallback(delegate
		{
			ApplyCultivation();
			Finish();
		}, 1500);
	}

	private void OnStuck()
	{
		Finish();
	}

	private void ApplyCultivation()
	{
		if (targetFarmland != null && targetFarmland.HasUnripeCrop())
		{
			double currentTotalHours = vas.Entity.World.Calendar.TotalHours + targetFarmland.GetHoursForNextStage() + 1.0;
			targetFarmland.TryGrowCrop(currentTotalHours);
			SimpleParticleProperties simpleParticleProperties = new SimpleParticleProperties(10f, 15f, ColorUtil.ToRgba(255, 255, 233, 83), targetFarmland.Position.AddCopy(-0.4, 0.8, -0.4), targetFarmland.Position.AddCopy(-0.6, 0.8, -0.6), new Vec3f(-0.25f, 0f, -0.25f), new Vec3f(0.25f, 0f, 0.25f), 2f, 1f, 0.2f);
			simpleParticleProperties.MinPos = targetFarmland.Position.AddCopy(0.5, 1.0, 0.5);
			vas.Entity.World.SpawnParticles(simpleParticleProperties);
		}
	}

	private bool IsValidFarmland(IPointOfInterest poi)
	{
		if (!(poi is BlockEntityFarmland blockEntityFarmland))
		{
			return false;
		}
		return blockEntityFarmland.HasUnripeCrop();
	}

	public override bool IsFinished()
	{
		return isFinished;
	}

	public override void Finish()
	{
		isFinished = true;
		vas.wppathTraverser.Stop();
		vas.Entity.AnimManager.StopAnimation(AnimCode);
		vas.Entity.AnimManager.StopAnimation("hoe-till");
		vas.Entity.Controls.StopAllMovement();
	}

	public override IEntityAction Clone()
	{
		return new CultivateCropsAction
		{
			vas = vas,
			WalkSpeed = WalkSpeed,
			AnimCode = AnimCode,
			Target = Target
		};
	}
}
