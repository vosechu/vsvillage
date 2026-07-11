using System.Collections.Concurrent;
using System.Collections.Generic;
using Vintagestory.API.Common;
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

	// Trough claiming so multiple shepherds don't converge on the same one. Owner dict + separate timestamp dict for stale-claim expiry.
	private static readonly ConcurrentDictionary<BlockPos, long> TroughClaimOwner =
		new ConcurrentDictionary<BlockPos, long>();
	private static readonly ConcurrentDictionary<BlockPos, long> TroughClaimTime =
		new ConcurrentDictionary<BlockPos, long>();

	// How long a claim is valid before being treated as stale (ms).
	private const long TroughClaimExpiryMs = 120_000L;

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

		EntityBehaviorVillager bh = entity.GetBehavior<EntityBehaviorVillager>();
		if (bh == null || bh.IsCarryEmpty) return null;
		ItemStack carried = bh.CarrySlot;

		// Release our previous claim and evict globally-stale entries so they
		// don't permanently prevent other shepherds from accessing those troughs.
		ReleaseClaim(lastTroughPos);
		PurgeExpiredClaims(entity.World.ElapsedMilliseconds);

		POIRegistry poiReg = entity.Api.ModLoader.GetModSystem<POIRegistry>();
		Vec3d myPos = entity.Pos.XYZ;
		BlockPos skipPos = lastTroughPos;
		nearestTrough = null;

		// Match BOTH BlockEntityTrough (large trough) and BlockEntityTroughMiniBowl
		// (small trough) - they share no common base class beyond IPointOfInterest, so
		// we fall back to a block-code check for anything that isn't BlockEntityTrough.
		if (skipPos != null)
		{
			nearestTrough = poiReg.GetNearestPoi(myPos, base.maxDistance,
				poi => IsTroughPoi(poi) && !IsClaimedByOther(GetTroughPos(poi))
				    && !GetTroughPos(poi).Equals(skipPos) && isEmptyTrough(poi)) as BlockEntityTrough;
		}
		if (nearestTrough == null)
		{
			nearestTrough = poiReg.GetNearestPoi(myPos, base.maxDistance,
				poi => IsTroughPoi(poi) && !IsClaimedByOther(GetTroughPos(poi))
				    && isEmptyTrough(poi)) as BlockEntityTrough;
		}
		if (nearestTrough == null && skipPos != null)
		{
			nearestTrough = poiReg.GetNearestPoi(myPos, base.maxDistance,
				poi => IsTroughPoi(poi) && !IsClaimedByOther(GetTroughPos(poi))
				    && !GetTroughPos(poi).Equals(skipPos) && isValidTrough(poi)) as BlockEntityTrough;
		}
		if (nearestTrough == null)
		{
			nearestTrough = poiReg.GetNearestPoi(myPos, base.maxDistance,
				poi => IsTroughPoi(poi) && !IsClaimedByOther(GetTroughPos(poi))
				    && isValidTrough(poi)) as BlockEntityTrough;
		}
		if (nearestTrough == null)
		{
			return null;
		}

		// Claim this trough so other shepherds pick a different one.
		lastTroughPos = nearestTrough.Pos.Copy();
		ClaimTrough(lastTroughPos);

		return GetTroughApproachPos(nearestTrough);
	}

	// Returns true for any block entity that represents a creature trough,
	// regardless of whether it is the large (BlockEntityTrough) or small
	// (BlockEntityTroughMiniBowl / any other VS variant) trough type.
	private static bool IsTroughPoi(IPointOfInterest poi) => ShepherdTroughs.IsTroughPoi(poi);

	private static BlockPos GetTroughPos(IPointOfInterest poi) => ShepherdTroughs.GetTroughPos(poi);

	private Vec3d GetTroughApproachPos(BlockEntityTrough trough)
	{
		IBlockAccessor ba = entity.World.BlockAccessor;
		BlockPos troughPos = trough.Pos;
		Vec3d myPos = entity.Pos.XYZ;
		Vec3d bestPos = null;
		double bestDist = double.MaxValue;
		foreach (BlockFacing facing in BlockFacing.HORIZONTALS)
		{
			BlockPos neighborPos = troughPos.AddCopy(facing.Normali.X, 0, facing.Normali.Z);
			Block neighborBlock = ba.GetBlock(neighborPos);
			if (neighborBlock.Code == null) continue;
			string blockPath = neighborBlock.Code.Path;

			// Skip solid fence panels but keep gates/doors (closed gates have collision but villagers push through).
			if (blockPath.Contains("fence") && !blockPath.Contains("gate")) continue;

			Block below = ba.GetBlock(neighborPos.DownCopy());
			bool groundSolid = below.CollisionBoxes != null && below.CollisionBoxes.Length != 0;
			if (!groundSolid) continue;

			bool isGate = blockPath.Contains("gate") || blockPath.Contains("door");
			bool neighborClear = isGate
				|| neighborBlock.CollisionBoxes == null
				|| neighborBlock.CollisionBoxes.Length == 0;
			Block above = ba.GetBlock(neighborPos.UpCopy());
			bool headClear = above.CollisionBoxes == null || above.CollisionBoxes.Length == 0;

			if (neighborClear && headClear)
			{
				Vec3d candidate = neighborPos.ToVec3d().Add(0.5, 0.0, 0.5);
				double dist = candidate.SquareDistanceTo(myPos);
				if (dist < bestDist)
				{
					bestDist = dist;
					bestPos = candidate;
				}
			}
		}
		// Return null rather than navigating into the solid trough block.
		return bestPos;
	}

	protected override bool InteractionPossible()
	{
		if (nearestTrough == null)
		{
			return false;
		}
		Vec3d troughCenter = nearestTrough.Pos.ToVec3d().Add(0.5, 0.5, 0.5);
		return entity.Pos.SquareDistanceTo(troughCenter) < 4.0;
	}

	private bool isEmptyTrough(IPointOfInterest poi)
	{
		if (!(poi is BlockEntityTrough blockEntityTrough) || IsTroughOnCooldown(blockEntityTrough.Pos))
		{
			return false;
		}
		return blockEntityTrough.Inventory[0]?.Empty ?? true;
	}

	protected override void ApplyInteractionEffect()
	{
		if (!IsShepherd() || nearestTrough == null) return;
		EntityBehaviorVillager bh = entity.GetBehavior<EntityBehaviorVillager>();
		if (bh == null || bh.IsCarryEmpty) return;

		ItemSlot source = new DummySlot(bh.CarrySlot);
		ContentConfig contentConfig = ItemSlotTrough.getContentConfig(entity.Api.World, nearestTrough.contentConfigs, source);
		if (contentConfig == null)
		{
			ReleaseClaim(nearestTrough.Pos);   // carried item isn't feed for this trough; return leg reclaims
			return;
		}
		entity.AnimManager.StartAnimation(new AnimationMetaData
		{
			Animation = "hoe-till", Code = "hoe-till", AnimationSpeed = 1f,
			BlendMode = EnumAnimationBlendMode.Average
		}.Init());
		BlockPos claimPos = nearestTrough.Pos.Copy();
		entity.World.RegisterCallback(delegate
		{
			PerformFilling(source, contentConfig, bh);
			ReleaseClaim(claimPos);
		}, 1500);
	}

	public override void FinishExecute(bool cancelled)
	{
		entity.AnimManager.StopAnimation("hoe-till");
		// Release the claim if ApplyInteractionEffect was never called (e.g. task
		// was cancelled before reaching the trough, or no contentConfig found).
		// ReleaseClaim is safe to call redundantly - it's a no-op if already released.
		ReleaseClaim(lastTroughPos);
		base.FinishExecute(cancelled);
	}

	private bool IsShepherd()
	{
		return entity != null && entity.Code != null && entity.Code.Path != null && entity.Code.Path.EndsWith("-shepherd");
	}

	private bool IsTroughOnCooldown(BlockPos pos)
	{
		long elapsedMilliseconds = entity.World.ElapsedMilliseconds;
		// Fast path: nothing has been filled recently - no allocation needed.
		if (recentlyFilledTroughs.Count == 0) return false;
		// Purge expired entries; only allocate the removal list when there are entries.
		List<BlockPos> list = null;
		foreach (KeyValuePair<BlockPos, long> recentlyFilledTrough in recentlyFilledTroughs)
		{
			if (elapsedMilliseconds - recentlyFilledTrough.Value > troughCooldownMs)
			{
				(list ??= new List<BlockPos>()).Add(recentlyFilledTrough.Key);
			}
		}
		if (list != null)
		{
			for (int i = 0; i < list.Count; i++)
				recentlyFilledTroughs.Remove(list[i]);
		}
		return recentlyFilledTroughs.TryGetValue(pos, out long value) && elapsedMilliseconds - value < troughCooldownMs;
	}

	private void MarkTroughFilled(BlockPos pos)
	{
		recentlyFilledTroughs[pos.Copy()] = entity.World.ElapsedMilliseconds;
	}

	private void PerformFilling(ItemSlot source, ContentConfig contentConfig, EntityBehaviorVillager bh)
	{
		if (nearestTrough == null) return;

		int totalMoved = 0, moved;
		do
		{
			moved = source.TryPutInto(entity.World, nearestTrough.Inventory[0], contentConfig.QuantityPerFillLevel);
			totalMoved += moved;
		}
		while (moved > 0 && !source.Empty);

		if (totalMoved > 0)
		{
			nearestTrough.Inventory[0].MarkDirty();
			MarkTroughFilled(nearestTrough.Pos);
			bh.CarrySlot = source.Empty ? null : source.Itemstack;
			SimpleParticleProperties simpleParticleProperties = new SimpleParticleProperties(10f, 15f, ColorUtil.ToRgba(255, 255, 233, 83), nearestTrough.Position.AddCopy(-0.4, 0.8, -0.4), nearestTrough.Position.AddCopy(-0.6, 0.8, -0.6), new Vec3f(-0.25f, 0f, -0.25f), new Vec3f(0.25f, 0f, 0.25f), 2f, 1f, 0.2f);
			simpleParticleProperties.MinPos = nearestTrough.Position.AddCopy(0.5, 1.0, 0.5);
			entity.World.SpawnParticles(simpleParticleProperties);
		}
		entity.AnimManager.StopAnimation("hoe-till");
	}

	private bool isValidTrough(IPointOfInterest poi)
	{
		if (!(poi is BlockEntityTrough blockEntityTrough) || IsTroughOnCooldown(blockEntityTrough.Pos))
		{
			return false;
		}
		return ShepherdTroughs.NeedsFeed(blockEntityTrough);
	}

	// === Claim helpers ===

	private bool IsClaimedByOther(BlockPos pos)
	{
		if (pos == null) return false;
		if (!TroughClaimOwner.TryGetValue(pos, out long owner)) return false;
		if (owner == entity.EntityId) return false; // our own claim
		// Treat the claim as void if it has expired.
		if (TroughClaimTime.TryGetValue(pos, out long claimedAt)
		    && entity.World.ElapsedMilliseconds - claimedAt > TroughClaimExpiryMs)
		{
			TroughClaimOwner.TryRemove(pos, out _);
			TroughClaimTime.TryRemove(pos, out _);
			return false;
		}
		return true;
	}

	private void ClaimTrough(BlockPos pos)
	{
		if (pos == null) return;
		TroughClaimOwner[pos] = entity.EntityId;
		TroughClaimTime[pos]  = entity.World.ElapsedMilliseconds;
	}

	private static void ReleaseClaim(BlockPos pos)
	{
		if (pos == null) return;
		TroughClaimOwner.TryRemove(pos, out _);
		TroughClaimTime.TryRemove(pos, out _);
	}

	// Caller passes world tick time (entity.World.ElapsedMilliseconds) so the clock matches the other claim methods.
	private static void PurgeExpiredClaims(long now)
	{
		foreach (BlockPos pos in TroughClaimTime.Keys)
		{
			if (TroughClaimTime.TryGetValue(pos, out long t) && now - t > TroughClaimExpiryMs)
			{
				TroughClaimOwner.TryRemove(pos, out _);
				TroughClaimTime.TryRemove(pos, out _);
			}
		}
	}
}
