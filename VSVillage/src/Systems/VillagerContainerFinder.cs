using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// Ranks a village's known container positions for a villager: filters to live containers that pass
/// the caller's predicate, are not claimed by another villager, and are not on the caller's cooldown,
/// then orders them nearest-first. Reads the Village.Containers index (populated by events + the
/// periodic scan) instead of walking blocks — the bed-finder's .Where().OrderBy(sqDist) shape from
/// VillageManager.cs:830. The caller (an AI task) path-probes the results and commits the first
/// reachable one.
/// </summary>
public static class VillagerContainerFinder
{
    public static List<BlockPos> RankContainers(
        IWorldAccessor world, Village village, Vec3d from, long villagerId,
        ContainerCooldownTracker cooldown, long nowMs, System.Func<BlockEntityContainer, bool> predicate)
    {
        if (village == null) return new List<BlockPos>();
        IBlockAccessor ba = world.BlockAccessor;
        return village.Containers
            .Where(pos =>
                ba.GetBlockEntity(pos) is BlockEntityContainer be // live: null if gone/unloaded
                && !VsVillage.ContainerClaims.IsClaimedByOther(pos, villagerId, nowMs)
                && !cooldown.IsOnCooldown(pos, nowMs)
                && predicate(be))
            .OrderBy(pos => from.SquareDistanceTo(pos.ToVec3d().Add(0.5, 0.5, 0.5)))
            .ToList();
    }

    /// <summary>
    /// A standable horizontal neighbour of the container to path to (never inside the container
    /// block). Solid ground below, clear body and head, skip solid fence panels but keep gates/doors.
    /// Mirrors AiTaskVillagerFillTrough.GetTroughApproachPos. Returns null if no side is standable.
    /// </summary>
    public static Vec3d ApproachPos(IWorldAccessor world, BlockPos containerPos, Vec3d from)
    {
        IBlockAccessor ba = world.BlockAccessor;
        Vec3d best = null;
        double bestDist = double.MaxValue;
        foreach (BlockFacing facing in BlockFacing.HORIZONTALS)
        {
            BlockPos neighbour = containerPos.AddCopy(facing.Normali.X, 0, facing.Normali.Z);
            if (!IsStandable(ba, neighbour)) continue;

            Vec3d candidate = neighbour.ToVec3d().Add(0.5, 0.0, 0.5);
            double dist = candidate.SquareDistanceTo(from);
            if (dist < bestDist) { bestDist = dist; best = candidate; }
        }
        return best;
    }

    /// <summary>
    /// Can a villager stand at <paramref name="pos"/> to reach the adjacent container: solid ground
    /// below, body and head clear, and not a solid fence panel (gates/doors stay passable — the
    /// pathfinder opens them). Occupancy uses the engine's position-aware GetCollisionBoxes, so a
    /// fence/slab/door reports the box for its actual state rather than the block default.
    /// </summary>
    private static bool IsStandable(IBlockAccessor ba, BlockPos pos)
    {
        Block block = ba.GetBlock(pos);
        if (block.Code == null) return false;
        string path = block.Code.Path;
        if (path.Contains("fence") && !path.Contains("gate")) return false; // solid fence panel blocks the approach

        if (!Occupies(ba, pos.DownCopy())) return false; // need solid ground under foot

        bool isGate = path.Contains("gate") || path.Contains("door");
        bool bodyClear = isGate || !Occupies(ba, pos);
        return bodyClear && !Occupies(ba, pos.UpCopy()); // head clear
    }

    // Does the block at pos physically occupy space? Uses the position-aware GetCollisionBoxes overload
    // (fences/doors/slabs override it to report their real collision) rather than the raw block default.
    private static bool Occupies(IBlockAccessor ba, BlockPos pos)
    {
        Cuboidf[] boxes = ba.GetBlock(pos).GetCollisionBoxes(ba, pos);
        return boxes != null && boxes.Length != 0;
    }
}
