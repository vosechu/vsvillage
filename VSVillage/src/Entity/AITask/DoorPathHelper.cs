using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

// Centralised door/gate handling for AI tasks. Activate logic redirects multiblock fillers to anchor;
// a 3x3x3 fallback scan covers third-party gates whose fillers don't forward via BlockMultiblock.OffsetInv.
public static class DoorPathHelper
{
	private const int ClusterScanRadius = 1;

	// Toggle door/gate at target. Redirects multiblock filler -> anchor, then activates one cell per unique code in the 3x3x3 cube.
	public static void ToggleDoor(EntityAgent caller, BlockPos target, bool opened)
	{
		if (caller == null || target == null) return;
		Block block = caller.World.BlockAccessor.GetBlock(target);

		if (block is BlockMultiblock mb)
		{
			BlockPos anchorPos = target.AddCopy(mb.OffsetInv);
			Block anchorBlock = caller.World.BlockAccessor.GetBlock(anchorPos);
			if (IsDoorOrGate(anchorBlock))
			{
				target = anchorPos;
				block = anchorBlock;
			}
		}

		if (!IsDoorOrGate(block)) return;

		ActivateAt(caller, target, block, opened);

		// 1x1 short-circuit: skip cluster scan for narrow doors (most cases).
		int blockWidth  = block.Attributes?["width"].AsInt(1)  ?? 1;
		int blockHeight = block.Attributes?["height"].AsInt(1) ?? 1;
		if (blockWidth <= 1 && blockHeight <= 1) return;

		// Cluster scan: hit each unique-code neighbour once so non-forwarding multiblock gates still toggle.
		HashSet<string> alreadyActivated = new HashSet<string>();
		string targetCode = block.Code?.Path ?? string.Empty;
		alreadyActivated.Add(targetCode);

		IBlockAccessor ba = caller.World.BlockAccessor;
		BlockPos scan = new BlockPos(0);
		for (int dx = -ClusterScanRadius; dx <= ClusterScanRadius; dx++)
		for (int dy = -ClusterScanRadius; dy <= ClusterScanRadius; dy++)
		for (int dz = -ClusterScanRadius; dz <= ClusterScanRadius; dz++)
		{
			if (dx == 0 && dy == 0 && dz == 0) continue;
			scan.Set(target.X + dx, target.Y + dy, target.Z + dz);
			Block neighbour = ba.GetBlock(scan);
			if (!IsDoorOrGate(neighbour)) continue;

			string code = neighbour.Code?.Path ?? string.Empty;
			if (!alreadyActivated.Add(code)) continue;

			ActivateAt(caller, scan.Copy(), neighbour, opened);
		}
	}

	private static void ActivateAt(EntityAgent caller, BlockPos pos, Block block, bool opened)
	{
		BlockSelection sel = new BlockSelection
		{
			Block = block,
			Position = pos,
			HitPosition = new Vec3d(0.5, 0.5, 0.5),
			Face = BlockFacing.NORTH
		};
		TreeAttribute attrs = new TreeAttribute();
		attrs.SetBool("opened", opened);
		try
		{
			block.Activate(caller.World, new Caller
			{
				Entity = caller,
				Type = EnumCallerType.Entity,
				Pos = caller.Pos.XYZ
			}, sel, attrs);
		}
		catch (Exception ex)
		{
			caller.World.Logger.Error("[VsVillage] DoorPathHelper.ActivateAt at " + pos + " failed: " + ex.Message);
		}
	}

	// Close any open door nodes along the given path. Called from FinishExecute so walkers don't leave open doors behind them.
	public static void CloseOpenDoorsAlongPath(EntityAgent caller, IList<VillagerPathNode> path)
	{
		if (caller == null || path == null) return;
		IBlockAccessor ba = caller.World.BlockAccessor;
		for (int i = 0; i < path.Count; i++)
		{
			VillagerPathNode node = path[i];
			if (node == null || !node.IsDoor) continue;
			Block block = ba.GetBlock(node.BlockPos);
			if (IsDoorCurrentlyOpen(block))
				ToggleDoor(caller, node.BlockPos, opened: false);
		}
	}

	public static bool IsDoorOrGate(Block block)
	{
		string p = block?.Code?.Path;
		return !string.IsNullOrEmpty(p) && (p.Contains("door") || p.Contains("gate"));
	}

	public static bool IsDoorCurrentlyOpen(Block block)
	{
		string p = block?.Code?.Path;
		if (string.IsNullOrEmpty(p)) return false;
		return p.Contains("opened") || p.Contains("open");
	}

	// Delayed close with queue-awareness for doors (gates close immediately). Doors re-schedule up to MaxScheduleDepth
	// times when a trailing villager is within TrailingVillagerRadius, so queued villagers don't get doors slammed.
	public static void ScheduleDoorClose(EntityAgent caller, BlockPos doorPos, int delayMs)
	{
		if (caller == null || doorPos == null) return;
		ScheduleCloseWithDepth(caller, doorPos, delayMs, depth: 0);
	}

	private const int MaxScheduleDepth = 6;
	private const float TrailingVillagerRadius = 2.0f;

	private static void ScheduleCloseWithDepth(EntityAgent caller, BlockPos doorPos, int delayMs, int depth)
	{
		caller.World.RegisterCallback(delegate
		{
			if (caller == null || !caller.Alive) return;
			Block block = caller.World.BlockAccessor.GetBlock(doorPos);
			if (!IsDoorOrGate(block)) return;

			if (IsDoor(block) && depth < MaxScheduleDepth
			    && AnotherVillagerNearby(caller.World, doorPos, caller.EntityId))
			{
				ScheduleCloseWithDepth(caller, doorPos, delayMs, depth + 1);
				return;
			}

			ToggleDoor(caller, doorPos, opened: false);
		}, delayMs);
	}

	private static bool IsDoor(Block block)
	{
		string p = block?.Code?.Path;
		return !string.IsNullOrEmpty(p) && p.Contains("door");
	}

	private static bool AnotherVillagerNearby(IWorldAccessor world, BlockPos doorPos, long selfId)
	{
		Vec3d centre = doorPos.ToVec3d().Add(0.5, 0.5, 0.5);
		Entity[] near = world.GetEntitiesAround(centre, TrailingVillagerRadius, TrailingVillagerRadius,
			e => e is EntityAgent && e.EntityId != selfId && IsOwnVillager(e));
		return near != null && near.Length > 0;
	}

	private static bool IsOwnVillager(Entity e)
	{
		string domain = e?.Code?.Domain;
		return domain == "vsvillage" || domain == "vsvillagesakura";
	}
}
