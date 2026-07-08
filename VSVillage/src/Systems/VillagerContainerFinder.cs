using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// Finds the nearest BlockEntityContainer (chest, storage vessel, labeled chest — all share
/// the base) around an anchor whose inventory satisfies a caller predicate. Containers are not
/// registered POIs, so this is a bounded block-accessor walk, not a POIRegistry query.
/// Engine-coupled → verified in-game, not unit-tested. v1: any container in radius is fair game.
/// </summary>
public static class VillagerContainerFinder
{
    public static BlockPos FindNearestContainer(
        IWorldAccessor world, BlockPos anchor, int radius, System.Func<BlockEntityContainer, bool> predicate)
    {
        BlockPos best = null;
        long bestSq = long.MaxValue;
        IBlockAccessor acc = world.BlockAccessor;
        acc.WalkBlocks(
            anchor.AddCopy(-radius, -radius, -radius),
            anchor.AddCopy(radius, radius, radius),
            (block, x, y, z) =>
            {
                if (block.Id == 0) return; // air, skip cheaply
                if (acc.GetBlockEntity(new BlockPos(x, y, z)) is BlockEntityContainer be && predicate(be))
                {
                    long dx = x - anchor.X, dy = y - anchor.Y, dz = z - anchor.Z;
                    long sq = dx * dx + dy * dy + dz * dz;
                    if (sq < bestSq) { bestSq = sq; best = new BlockPos(x, y, z); }
                }
            });
        return best;
    }
}
