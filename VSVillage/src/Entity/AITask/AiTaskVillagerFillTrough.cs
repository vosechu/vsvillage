using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

public class AiTaskVillagerFillTrough : AiTaskGotoAndInteract
{
	private BlockEntityTrough nearestTrough;

	// Every path that returns a target pos must set this. InteractionPossible measures arrival
	// against it, so a path that leaves it stale reports "arrived" at the previous trip's block.
	private BlockPos interactPos;

	// Doubles as the leg discriminator: ApplyInteractionEffect takes feed while this is set and
	// fills the trough while it isn't. GetTargetPos must clear it on every call, or a fill leg
	// runs the fetch branch against last trip's chest.
	private BlockEntityGenericTypedContainer feedChest;

	private bool tookFeed;

	// Third leg, checked before feedChest because the return leg also has a chest set. Put back
	// what no trough wants instead of holding it until one drains.
	private bool returningFeed;

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

		// Release our previous claim and evict globally-stale entries so they
		// don't permanently prevent other shepherds from accessing those troughs.
		ReleaseClaim(lastTroughPos);
		PurgeExpiredClaims(entity.World.ElapsedMilliseconds);

		POIRegistry poiReg = entity.Api.ModLoader.GetModSystem<POIRegistry>();
		Vec3d myPos = entity.Pos.XYZ;
		BlockPos skipPos = lastTroughPos;
		nearestTrough = null;
		interactPos = null;
		// Cleared here rather than after the trough search, so the leg that runs when no trough
		// wants feed cannot inherit last trip's chest.
		feedChest = null;
		tookFeed = false;
		returningFeed = false;

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
			return GetReturnFeedPos();
		}

		// Claim this trough so other shepherds pick a different one.
		lastTroughPos = nearestTrough.Pos.Copy();
		ClaimTrough(lastTroughPos);

		if (FindFeedSlot(VillagerCarrySlots(), nearestTrough) != null)
		{
			interactPos = nearestTrough.Pos.Copy();
			return GetStandingPosBeside(interactPos);
		}

		feedChest = FindFeedChest();
		if (feedChest == null || FindFeedSlot(feedChest.Inventory, nearestTrough) == null)
		{
			// Leave the trough empty. Never fall back to spawning feed from a DummySlot here:
			// a pen that feeds itself with nothing in a chest is the bug this fetch leg exists to fix.
			// Put back anything being carried first, though. A trough this shepherd cannot service is
			// the common way to end up holding feed forever: the trough search finds it, so the "no
			// trough" return leg below never runs, and there are only four carry slots to silt up.
			feedChest = null;
			return GetReturnFeedPos();
		}
		interactPos = feedChest.Pos.Copy();
		return GetStandingPosBeside(interactPos);
	}

	// Must stay the Typed container. Every vanilla chest, basket and storage vessel declares
	// BlockEntityGenericTypedContainer and nothing declares its sibling BlockEntityGenericContainer,
	// so GetBlockEntity<T> is `as T` and yields null for the plain one. Don't widen to their shared
	// base OpenableContainer (firepits, querns) or to Container (troughs); the shepherd raids those.
	private BlockEntityGenericTypedContainer FindFeedChest()
	{
		BlockPos workstation = entity.GetBehavior<EntityBehaviorVillager>()?.Workstation;
		return workstation == null ? null : FindNearbyBlockEntity<BlockEntityGenericTypedContainer>(workstation, 4);
	}

	// The leg that runs when no trough wants feed. Null unless something is actually being carried
	// and there is a chest to put it in, so a shepherd with empty hands and full troughs has no
	// task at all rather than a pointless walk.
	private Vec3d GetReturnFeedPos()
	{
		if (!IsCarryingAnything()) return null;
		feedChest = FindFeedChest();
		if (feedChest == null) return null;
		returningFeed = true;
		interactPos = feedChest.Pos.Copy();
		return GetStandingPosBeside(interactPos);
	}

	// Every transfer this task makes goes through here, at DirectMerge. Feed is perishable, and two
	// stacks of the same feed refuse to merge once their spoilage differs by more than four hours
	// AND three per cent of shelf life, unless the merge asks for DirectMerge
	// (CollectibleObject.TryMergeStacks). ItemSlot.TryPutInto's convenience overload asks for
	// AutoMerge, which is below that bar, so with it a shepherd can top up a trough only for as
	// long as the feed already in it is about as fresh as what it is carrying. A trough left alone
	// for a day silently stops accepting anything and the shepherd retries forever. DirectMerge is
	// what a player's own hand-placement uses, so this only lets a villager do what a player can.
	private int PutInto(ItemSlot source, ItemSlot target, int quantity)
	{
		ItemStackMoveOperation op = new ItemStackMoveOperation(entity.World, EnumMouseButton.Left,
			(EnumModifierKey)0, EnumMergePriority.DirectMerge, quantity);
		return source.TryPutInto(target, ref op);
	}

	private bool IsCarryingAnything()
	{
		foreach (ItemSlot slot in VillagerCarrySlots())
		{
			if (!slot.Empty) return true;
		}
		return false;
	}

	// Empties the carry slots back into the chest. Everything in them, not just feed: this task is
	// the only thing that puts anything there today. If villagers start carrying something else,
	// this needs a filter or it will post their belongings into the nearest chest.
	private void ReturnFeedToChest()
	{
		bool moved = false;
		foreach (ItemSlot slot in VillagerCarrySlots())
		{
			if (slot.Empty) continue;
			// GetBestSuitedSlot is right here and wrong for the villager: a chest has no hand slots
			// to overwrite, so ranking every slot is exactly what we want.
			ItemSlot target = feedChest.Inventory.GetBestSuitedSlot(slot)?.slot;
			if (target == null) continue;
			if (PutInto(slot, target, slot.StackSize) > 0) moved = true;
		}
		if (moved)
		{
			feedChest.MarkDirty(true);
		}
	}

	// Takes slots rather than an inventory so the same scan serves the chest, where every slot is
	// fair game, and the villager, where only the carry slots are.
	// Requires a whole QuantityPerFillLevel in one slot. A partial stack is not a usable portion,
	// and accepting one would let the fill leg take feed it can't actually place.
	private ItemSlot FindFeedSlot(IEnumerable<ItemSlot> slots, BlockEntityTrough trough)
	{
		if (slots == null) return null;
		ItemSlot content = trough.Inventory[0];
		foreach (ItemSlot slot in slots)
		{
			if (slot.Empty) continue;
			// A part-full trough takes only what it already holds: ItemSlotTrough.troughable returns
			// false for anything else, and TryPutInto then moves nothing. Skipping the mismatch here
			// is what stops the task picking hay for a flax trough, walking over, placing zero, and
			// doing it again every cooldown forever.
			if (!content.Empty
			    && !slot.Itemstack.Equals(entity.World, content.Itemstack, GlobalConstants.IgnoredStackAttributes))
			{
				continue;
			}
			ContentConfig config = ItemSlotTrough.getContentConfig(entity.Api.World, trough.contentConfigs, slot);
			if (config != null && slot.StackSize >= config.QuantityPerFillLevel) return slot;
		}
		return null;
	}

	// How much more feed the trough will hold: its own capacity rule, QuantityPerFillLevel times
	// MaxFillLevels, minus what is in it now. Nothing else clamps a transfer to this. TryPutInto
	// stops at the item's max stack size, and the trough's slot only refuses feed once it is
	// already at capacity, so handing it more than this figure overfills the trough.
	private static int RemainingCapacity(BlockEntityTrough trough, ContentConfig config)
	{
		return config.QuantityPerFillLevel * config.MaxFillLevels - trough.Inventory[0].StackSize;
	}

	// True only if feed actually moved. Arriving at the chest is not enough, because the take can
	// find nothing to put the feed into and quietly do nothing. On false the caller must NOT clear
	// the cooldown, or the shepherd re-targets this same chest every targetSearchIntervalMs forever.
	private bool TakeFeedFromChest()
	{
		ItemSlot source = FindFeedSlot(feedChest.Inventory, nearestTrough);
		ContentConfig config = source == null ? null : ItemSlotTrough.getContentConfig(entity.Api.World, nearestTrough.contentConfigs, source);
		ItemSlot target = config == null ? null : FindCarryTarget(source);
		if (target == null) return false;
		// Fetch what the trough is short, not one fill level. One level per round trip means a
		// shepherd walks the chest-to-trough leg eight times to fill an empty large trough.
		// Subtract what the target slot already holds, which is feed from an earlier trip that
		// was too small to be worth a fill leg of its own.
		int wanted = RemainingCapacity(nearestTrough, config) - target.StackSize;
		if (wanted <= 0) return false;
		if (PutInto(source, target, wanted) <= 0) return false;
		feedChest.MarkDirty(true);
		return true;
	}

	// Deliberately not InventoryBase.GetBestSuitedSlot: that ranks every slot, and slots 0 and 1
	// are the villager's hands, so it will happily put grain where a soldier's spear goes.
	// Prefers a stack already holding this feed so a second trip tops it up instead of burning
	// a second slot.
	private ItemSlot FindCarryTarget(ItemSlot source)
	{
		ItemSlot firstEmpty = null;
		foreach (ItemSlot slot in VillagerCarrySlots())
		{
			if (slot.Empty)
			{
				firstEmpty ??= slot;
			}
			else if (slot.Itemstack.Equals(entity.World, source.Itemstack, GlobalConstants.IgnoredStackAttributes))
			{
				return slot;
			}
		}
		return firstEmpty;
	}

	// Returns true for any block entity that represents a creature trough,
	// regardless of whether it is the large (BlockEntityTrough) or small
	// (BlockEntityTroughMiniBowl / any other VS variant) trough type.
	private static bool IsTroughPoi(IPointOfInterest poi)
	{
		if (poi is BlockEntityTrough) return true;
		if (poi is BlockEntity be)
			return be.Block?.Code?.Path?.Contains("trough") == true;
		return false;
	}

	private static BlockPos GetTroughPos(IPointOfInterest poi)
	{
		return (poi as BlockEntity)?.Pos;
	}

	private Vec3d GetStandingPosBeside(BlockPos blockPos)
	{
		IBlockAccessor ba = entity.World.BlockAccessor;
		Vec3d myPos = entity.Pos.XYZ;
		Vec3d bestPos = null;
		double bestDist = double.MaxValue;
		foreach (BlockFacing facing in BlockFacing.HORIZONTALS)
		{
			BlockPos neighborPos = blockPos.AddCopy(facing.Normali.X, 0, facing.Normali.Z);
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
		// Null when all four sides are blocked. Don't "fix" that by returning blockPos:
		// it is the trough or chest itself, and the villager would path into a solid block.
		return bestPos;
	}

	protected override bool InteractionPossible()
	{
		if (interactPos == null)
		{
			return false;
		}
		Vec3d blockCenter = interactPos.ToVec3d().Add(0.5, 0.5, 0.5);
		return entity.Pos.SquareDistanceTo(blockCenter) < 4.0;
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
		if (!IsShepherd())
		{
			return;
		}
		// Before the nearestTrough guard: the return leg runs precisely because no trough was
		// found, so testing that first would drop the feed off nowhere and keep it forever.
		if (returningFeed)
		{
			ReturnFeedToChest();
			return;
		}
		if (nearestTrough == null)
		{
			return;
		}
		if (feedChest != null)
		{
			tookFeed = TakeFeedFromChest();
			return;
		}
		ItemSlot itemSlot = FindFeedSlot(VillagerCarrySlots(), nearestTrough);
		ContentConfig contentConfig = itemSlot == null ? null : ItemSlotTrough.getContentConfig(entity.Api.World, nearestTrough.contentConfigs, itemSlot);
		if (contentConfig != null)
		{
			entity.AnimManager.StartAnimation(new AnimationMetaData
			{
				Animation = "hoe-till",
				Code = "hoe-till",
				AnimationSpeed = 1f,
				BlendMode = EnumAnimationBlendMode.Average
			}.Init());
			// Capture the trough position for the delayed claim release.
			BlockPos claimPos = nearestTrough.Pos.Copy();
			entity.World.RegisterCallback(delegate
			{
				PerformFilling(itemSlot, contentConfig);
				// Release claim here, AFTER filling, so no other shepherd swoops
				// in during the 1500 ms animation window before food is placed.
				ReleaseClaim(claimPos);
			}, 1500);
		}
		else
		{
			// No valid content config - release the claim immediately so another
			// shepherd can try a different item or the trough isn't locked forever.
			ReleaseClaim(nearestTrough.Pos);
		}
	}

	public override void FinishExecute(bool cancelled)
	{
		entity.AnimManager.StopAnimation("hoe-till");
		// Release the claim if ApplyInteractionEffect was never called (e.g. task
		// was cancelled before reaching the trough, or no contentConfig found).
		// ReleaseClaim is safe to call redundantly - it's a no-op if already released.
		ReleaseClaim(lastTroughPos);
		base.FinishExecute(cancelled);

		// Fetch leg done. Both clears are load-bearing: lastTroughPos would rotate the shepherd
		// away from the trough it just fetched for, and the cooldown would park the carried feed
		// in the inventory until the normal task interval elapsed.
		if (tookFeed)
		{
			lastTroughPos = null;
			cooldownUntilMs = entity.World.ElapsedMilliseconds;
		}
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

	private void PerformFilling(ItemSlot itemSlot, ContentConfig contentConfig)
	{
		if (nearestTrough == null) return;

		// Empty the carried feed into the trough up to its capacity, rather than one fill level and
		// then walking away with the rest still in hand.
		int quantity = Math.Min(itemSlot.StackSize, RemainingCapacity(nearestTrough, contentConfig));
		int transferred = quantity <= 0 ? 0 : PutInto(itemSlot, nearestTrough.Inventory[0], quantity);
		if (transferred > 0)
		{
			// Only mark filled and show particles when food was actually placed.
			nearestTrough.Inventory[0].MarkDirty();
			MarkTroughFilled(nearestTrough.Pos);
			SimpleParticleProperties simpleParticleProperties = new SimpleParticleProperties(10f, 15f, ColorUtil.ToRgba(255, 255, 233, 83), nearestTrough.Position.AddCopy(-0.4, 0.8, -0.4), nearestTrough.Position.AddCopy(-0.6, 0.8, -0.6), new Vec3f(-0.25f, 0f, -0.25f), new Vec3f(0.25f, 0f, 0.25f), 2f, 1f, 0.2f);
			simpleParticleProperties.MinPos = nearestTrough.Position.AddCopy(0.5, 1.0, 0.5);
			entity.World.SpawnParticles(simpleParticleProperties);
		}
		entity.AnimManager.StopAnimation("hoe-till");
	}

	private bool isValidTrough(IPointOfInterest poi)
	{
		// IsFull is the trough's own capacity rule (QuantityPerFillLevel x MaxFillLevels), and is false when empty.
		// It reads contentConfigs first, which is an ObjectCache lookup ending in `as ContentConfig[]` and so is
		// null for any trough block with no registered configs - IsFull would throw on it. Vanilla troughs are
		// always registered; a modded one need not be, and this predicate runs over every POI in range.
		return poi is BlockEntityTrough blockEntityTrough
		    && !IsTroughOnCooldown(blockEntityTrough.Pos)
		    && blockEntityTrough.contentConfigs != null
		    && !blockEntityTrough.IsFull;
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
