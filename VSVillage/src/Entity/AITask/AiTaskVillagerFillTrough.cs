using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

public class AiTaskVillagerFillTrough : AiTaskGotoAndInteract
{
	private BlockEntityTrough nearestTrough;

	private BlockPos lastTroughPos;

	private Dictionary<BlockPos, long> recentlyFilledTroughs;

	private long troughCooldownMs = 60000L;

	public AiTaskVillagerFillTrough(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
		: base(entity, taskConfig, aiConfig)
	{
		recentlyFilledTroughs = new Dictionary<BlockPos, long>();
		if (taskConfig["troughCooldownSeconds"] != null)
		{
			troughCooldownMs = taskConfig["troughCooldownSeconds"].AsInt(60) * 1000;
		}
	}

	protected override Vec3d GetTargetPos()
	{
		if (!IsShepherd())
		{
			return null;
		}

		POIRegistry poiReg = entity.Api.ModLoader.GetModSystem<POIRegistry>();
		Vec3d myPos = ((Entity)entity).ServerPos.XYZ;
		BlockPos skipPos = lastTroughPos;

		// Prefer EMPTY troughs first (animals need feeding, not topping off)
		nearestTrough = null;
		if (skipPos != null)
		{
			nearestTrough = poiReg.GetNearestPoi(myPos, maxDistance,
				poi => poi is BlockEntityTrough bt && !bt.Pos.Equals(skipPos) && isEmptyTrough(poi))
				as BlockEntityTrough;
		}
		if (nearestTrough == null)
		{
			nearestTrough = poiReg.GetNearestPoi(myPos, maxDistance, isEmptyTrough) as BlockEntityTrough;
		}

		// Fall back to any trough that has room (not full)
		if (nearestTrough == null && skipPos != null)
		{
			nearestTrough = poiReg.GetNearestPoi(myPos, maxDistance,
				poi => poi is BlockEntityTrough bt && !bt.Pos.Equals(skipPos) && isValidTrough(poi))
				as BlockEntityTrough;
		}
		if (nearestTrough == null)
		{
			nearestTrough = poiReg.GetNearestPoi(myPos, maxDistance, isValidTrough) as BlockEntityTrough;
		}

		if (nearestTrough == null)
		{
			return null;
		}

		lastTroughPos = nearestTrough.Pos.Copy();
		entity.World.Logger.Notification("Shepherd found trough at: " + nearestTrough.Position.ToString());
		return GetTroughApproachPos(nearestTrough);
	}

	// Pick the adjacent cell closest to the shepherd that doesn't have a fence post.
	// This lets the shepherd approach from the accessible side when troughs are placed
	// against fences, avoiding forced gate traversal.
	private Vec3d GetTroughApproachPos(BlockEntityTrough trough)
	{
		IBlockAccessor ba = entity.World.BlockAccessor;
		BlockPos troughPos = trough.Pos;
		Vec3d myPos = ((Entity)entity).ServerPos.XYZ;
		Vec3d bestPos = null;
		double bestDist = double.MaxValue;

		foreach (BlockFacing facing in BlockFacing.HORIZONTALS)
		{
			BlockPos neighborPos = troughPos.AddCopy(facing.Normali.X, 0, facing.Normali.Z);
			Block neighborBlock = ba.GetBlock(neighborPos);

			// Skip fence posts (but allow gates — shepherd can open those)
			if (neighborBlock.Code != null
				&& neighborBlock.Code.Path.Contains("fence")
				&& !neighborBlock.Code.Path.Contains("gate"))
			{
				continue;
			}

			// Must have solid ground below and clear head space
			Block above = ba.GetBlock(neighborPos.UpCopy());
			Block below = ba.GetBlock(neighborPos.DownCopy());
			bool headClear = above.CollisionBoxes == null || above.CollisionBoxes.Length == 0;
			bool groundSolid = below.CollisionBoxes != null && below.CollisionBoxes.Length > 0;
			if (!headClear || !groundSolid) continue;

			Vec3d candidate = neighborPos.ToVec3d().Add(0.5, 0.0, 0.5);
			double dist = candidate.SquareDistanceTo(myPos);
			if (dist < bestDist)
			{
				bestDist = dist;
				bestPos = candidate;
			}
		}

		// Fallback: target the trough block itself (pathfinder destination special-case handles it)
		return bestPos ?? trough.Pos.ToVec3d().Add(0.5, 0.5, 0.5);
	}

	// Check interaction against the actual trough, not the approach waypoint,
	// so the animation fires from any adjacent cell regardless of approach direction.
	protected override bool InteractionPossible()
	{
		if (nearestTrough == null) return false;
		Vec3d troughCenter = nearestTrough.Pos.ToVec3d().Add(0.5, 0.5, 0.5);
		return ((Entity)entity).ServerPos.SquareDistanceTo(troughCenter) < 4.0;
	}

	private bool isEmptyTrough(IPointOfInterest poi)
	{
		if (!(poi is BlockEntityTrough blockEntityTrough) || IsTroughOnCooldown(blockEntityTrough.Pos))
		{
			return false;
		}
		ItemSlot itemSlot = blockEntityTrough.Inventory[0];
		return itemSlot == null || itemSlot.Empty;
	}

	protected override void ApplyInteractionEffect()
	{
		if (!IsShepherd() || nearestTrough == null)
		{
			return;
		}
		entity.World.Logger.Notification("Shepherd found trough at: " + nearestTrough.Position.ToString());
		Item item = (nearestTrough.Inventory[0].Empty ? entity.World.GetItem(new AssetLocation("grain-flax")) : nearestTrough.Inventory[0].Itemstack.Item);
		entity.World.Logger.Notification("Item to fill: " + ((item != null) ? item.Code.ToString() : "NULL"));
		if (item == null)
		{
			return;
		}
		ItemSlot itemSlot = new DummySlot(new ItemStack(item, 16));
		ContentConfig contentConfig = ItemSlotTrough.getContentConfig(entity.Api.World, nearestTrough.contentConfigs, itemSlot);
		entity.World.Logger.Notification("ContentConfig: " + ((contentConfig != null) ? "Valid" : "NULL"));
		if (contentConfig != null)
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
				PerformFilling(itemSlot, contentConfig);
			}, 1500);
		}
	}

	public override void FinishExecute(bool cancelled)
	{
		entity.AnimManager.StopAnimation("hoe-till");
		base.FinishExecute(cancelled);
	}

	private bool IsShepherd()
	{
		return entity != null && entity.Code != null && entity.Code.Path != null && entity.Code.Path.EndsWith("-shepherd");
	}

	private bool IsTroughOnCooldown(BlockPos pos)
	{
		long elapsedMilliseconds = entity.World.ElapsedMilliseconds;
		List<BlockPos> list = new List<BlockPos>();
		foreach (KeyValuePair<BlockPos, long> recentlyFilledTrough in recentlyFilledTroughs)
		{
			if (elapsedMilliseconds - recentlyFilledTrough.Value > troughCooldownMs)
			{
				list.Add(recentlyFilledTrough.Key);
			}
		}
		for (int i = 0; i < list.Count; i++)
		{
			recentlyFilledTroughs.Remove(list[i]);
		}
		long value;
		return recentlyFilledTroughs.TryGetValue(pos, out value) && elapsedMilliseconds - value < troughCooldownMs;
	}

	private void MarkTroughFilled(BlockPos pos)
	{
		recentlyFilledTroughs[pos.Copy()] = entity.World.ElapsedMilliseconds;
	}

	private void PerformFilling(ItemSlot itemSlot, ContentConfig contentConfig)
	{
		if (nearestTrough != null)
		{
			int num = itemSlot.TryPutInto(entity.World, nearestTrough.Inventory[0], contentConfig.QuantityPerFillLevel);
			entity.World.Logger.Notification("Amount moved to trough: " + num);
			nearestTrough.Inventory[0].MarkDirty();
			MarkTroughFilled(nearestTrough.Pos);
			SimpleParticleProperties simpleParticleProperties = new SimpleParticleProperties(10f, 15f, ColorUtil.ToRgba(255, 255, 233, 83), nearestTrough.Position.AddCopy(-0.4, 0.8, -0.4), nearestTrough.Position.AddCopy(-0.6, 0.8, -0.6), new Vec3f(-0.25f, 0f, -0.25f), new Vec3f(0.25f, 0f, 0.25f), 2f, 1f, 0.2f);
			simpleParticleProperties.MinPos = nearestTrough.Position.AddCopy(0.5, 1.0, 0.5);
			entity.World.SpawnParticles(simpleParticleProperties);
			entity.AnimManager.StopAnimation("hoe-till");
		}
	}

	private bool isValidTrough(IPointOfInterest poi)
	{
		if (!(poi is BlockEntityTrough blockEntityTrough) || IsTroughOnCooldown(blockEntityTrough.Pos))
		{
			return false;
		}
		ItemSlot itemSlot = blockEntityTrough.Inventory[0];
		if (itemSlot == null || itemSlot.Empty)
		{
			return true;
		}
		int stackSize = itemSlot.StackSize;
		int maxStackSize = itemSlot.Itemstack.Collectible.MaxStackSize;
		return stackSize < maxStackSize;
	}
}
