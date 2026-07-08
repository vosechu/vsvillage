using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// Finds the nearest BlockEntityContainer (chest, storage vessel, labeled chest — all share the
/// base) around an anchor whose inventory satisfies a caller predicate. Containers are not
/// registered POIs, so this is a bounded block-accessor walk, not a POIRegistry query.
/// v1: any container in radius is fair game.
///
/// FIXME(perf): this walks a (2*radius+1)^3 cube every call — ~15.6k blocks at radius 12 — and
/// runs per villager on the AI search cadence. Fine for a handful of villagers; a populated
/// village will feel it. Before that ships, cap the radius, cache results between searches, or
/// register containers as POIs so this becomes an index lookup instead of a block walk.
/// </summary>
public static class VillagerContainerFinder
{
    public static BlockPos FindNearestContainer(
        IWorldAccessor world, BlockPos anchor, int radius, System.Func<BlockEntityContainer, bool> predicate)
    {
        BlockPos best = null;
        long bestSq = long.MaxValue;
        IBlockAccessor acc = world.BlockAccessor;
        BlockPos probe = new BlockPos(0, 0, 0); // reused per block to avoid a fresh alloc on every step
        acc.WalkBlocks(
            anchor.AddCopy(-radius, -radius, -radius),
            anchor.AddCopy(radius, radius, radius),
            (block, x, y, z) =>
            {
                if (block.Id == 0) return; // air, skip cheaply
                probe.X = x; probe.Y = y; probe.Z = z;
                if (acc.GetBlockEntity(probe) is BlockEntityContainer be && predicate(be))
                {
                    long dx = x - anchor.X, dy = y - anchor.Y, dz = z - anchor.Z;
                    long sq = dx * dx + dy * dy + dz * dz;
                    if (sq < bestSq) { bestSq = sq; best = new BlockPos(x, y, z); } // only the winner is kept
                }
            });
        return best;
    }
}
