using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VsVillageTest;

// Shared deterministic-environment helpers for behavioral scenarios.
//
// Behavioral tests MUST NOT depend on random-world terrain: near spawn the ground varies in
// height column-to-column, and can be water or a pit, so villagers can't reliably path to a
// block placed at each column's own surface. Flatten a loaded area first, then place everything
// coplanar on it — A* over a uniform floor is deterministic and every target is reachable.
public static class TestScene
{
    // Flattens a rectangular area to a single walkable floor centred on `center`. Lays a uniform
    // solid floor at the spawn column's surface Y (reusing that column's own solid block, so no
    // block-code lookup can fail) and clears headroom above it. Anything placed at the returned Y
    // (floorY+1) is coplanar and trivially path-connected. Call before placing chests/villagers.
    // Keep halfX/halfZ within the headless chunk-load radius of spawn (well under ~60 blocks).
    public static int BuildFlatArea(ICoreServerAPI api, BlockPos center, int halfX, int halfZ)
    {
        IBlockAccessor ba = api.World.BlockAccessor;
        int floorY = ba.GetTerrainMapheightAt(center);
        int floorId = ba.GetBlock(new BlockPos(center.X, floorY, center.Z)).BlockId; // a valid solid block
        for (int x = center.X - halfX; x <= center.X + halfX; x++)
        {
            for (int z = center.Z - halfZ; z <= center.Z + halfZ; z++)
            {
                ba.SetBlock(floorId, new BlockPos(x, floorY, z));
                for (int dy = 1; dy <= 5; dy++) ba.SetBlock(0, new BlockPos(x, floorY + dy, z));
            }
        }
        return floorY + 1;
    }
}
