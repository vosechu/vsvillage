using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

public class FillTroughAction : EntityActionBase
{
	public const string ActionType = "FillTrough";

	public float WalkSpeed = 0.02f;

	public string AnimCode = "walk";

	private bool isFinished = true;

	private BlockEntityTrough targetTrough;

	public override string Type => "FillTrough";

	public override void Start(EntityActivity entityActivity)
	{
		EntityBehaviorVillager behavior = vas.Entity.GetBehavior<EntityBehaviorVillager>();
		if (behavior == null)
		{
			isFinished = true;
			return;
		}
		POIRegistry modSystem = vas.Entity.Api.ModLoader.GetModSystem<POIRegistry>();
		targetTrough = modSystem.GetNearestPoi(((Entity)vas.Entity).ServerPos.XYZ, 40f, (IPointOfInterest poi) => poi is BlockEntityTrough) as BlockEntityTrough;
		if (targetTrough == null)
		{
			isFinished = true;
			return;
		}
		isFinished = false;
		BlockPos asBlockPos = ((Entity)vas.Entity).ServerPos.AsBlockPos;
		BlockPos asBlockPos2 = targetTrough.Position.AsBlockPos;
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
		if (targetTrough == null)
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
			ApplyFill();
			Finish();
		}, 1500);
	}

	private void OnStuck()
	{
		Finish();
	}

	private void ApplyFill()
	{
		if (targetTrough == null)
		{
			return;
		}
		Item item = (targetTrough.Inventory[0].Empty ? vas.Entity.World.GetItem(new AssetLocation("grain-flax")) : targetTrough.Inventory[0].Itemstack.Item);
		if (item != null)
		{
			ItemSlot itemSlot = new DummySlot(new ItemStack(item, 16));
			ContentConfig contentConfig = ItemSlotTrough.getContentConfig(vas.Entity.World, targetTrough.contentConfigs, itemSlot);
			if (contentConfig != null)
			{
				itemSlot.TryPutInto(vas.Entity.World, targetTrough.Inventory[0], contentConfig.QuantityPerFillLevel);
				targetTrough.Inventory[0].MarkDirty();
				SimpleParticleProperties simpleParticleProperties = new SimpleParticleProperties(10f, 15f, ColorUtil.ToRgba(255, 255, 233, 83), targetTrough.Position.AddCopy(-0.4, 0.8, -0.4), targetTrough.Position.AddCopy(-0.6, 0.8, -0.6), new Vec3f(-0.25f, 0f, -0.25f), new Vec3f(0.25f, 0f, 0.25f), 2f, 1f, 0.2f);
				simpleParticleProperties.MinPos = targetTrough.Position.AddCopy(0.5, 1.0, 0.5);
				vas.Entity.World.SpawnParticles(simpleParticleProperties);
			}
		}
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
		return new FillTroughAction
		{
			vas = vas,
			WalkSpeed = WalkSpeed,
			AnimCode = AnimCode
		};
	}
}
