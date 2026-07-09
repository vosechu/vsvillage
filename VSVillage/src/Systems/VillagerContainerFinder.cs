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
            Block nb = ba.GetBlock(neighbour);
            if (nb.Code == null) continue;
            string path = nb.Code.Path;
            if (path.Contains("fence") && !path.Contains("gate")) continue; // solid fence panel blocks the approach

            Block below = ba.GetBlock(neighbour.DownCopy());
            bool groundSolid = below.CollisionBoxes != null && below.CollisionBoxes.Length != 0;
            if (!groundSolid) continue;

            bool isGate = path.Contains("gate") || path.Contains("door");
            bool bodyClear = isGate || nb.CollisionBoxes == null || nb.CollisionBoxes.Length == 0;
            Block above = ba.GetBlock(neighbour.UpCopy());
            bool headClear = above.CollisionBoxes == null || above.CollisionBoxes.Length == 0;
            if (!bodyClear || !headClear) continue;

            Vec3d candidate = neighbour.ToVec3d().Add(0.5, 0.0, 0.5);
            double dist = candidate.SquareDistanceTo(from);
            if (dist < bestDist) { bestDist = dist; best = candidate; }
        }
        return best;
    }
}
