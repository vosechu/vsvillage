using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
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
        // A NON-gravity solid block: reusing the spawn column's own surface block would drop the
        // floor if that block is sand/gravel over a terrain dip (seed-dependent flake). Cobblestone
        // has no falling behaviour. Fall back to the surface block only if none of these resolve.
        int floorId = 0;
        foreach (string code in new[] { "game:cobblestone-granite", "game:rock-granite", "game:cobblestone-andesite" })
        {
            Block b = api.World.GetBlock(new AssetLocation(code));
            if (b != null) { floorId = b.BlockId; break; }
        }
        if (floorId == 0) floorId = ba.GetBlock(new BlockPos(center.X, floorY, center.Z)).BlockId;
        for (int x = center.X - halfX; x <= center.X + halfX; x++)
        {
            for (int z = center.Z - halfZ; z <= center.Z + halfZ; z++)
            {
                // Two layers deep: the world is REUSED across suite runs, and a single-layer floor laid
                // at each column's own (possibly dug-out) height left trench ghosts from old scenario
                // versions. Solid to floorY-1 self-heals 1-deep digs and displaces stray water.
                ba.SetBlock(floorId, new BlockPos(x, floorY - 1, z));
                ba.SetBlock(floorId, new BlockPos(x, floorY, z));
                for (int dy = 1; dy <= 5; dy++) ba.SetBlock(0, new BlockPos(x, floorY + dy, z));
            }
        }
        api.Logger.Notification("[harness] BuildFlatArea: floor y={0} at {1} ({2}x{3})", floorY, center, halfX * 2 + 1, halfZ * 2 + 1);
        return floorY + 1;
    }

    // Spawn a livestock entity as a diet source for the shepherd's feed selection. NOT AlwaysActive — but the
    // movement-suite client parked on the arena revives it (the 128-block simulation range that runs villager
    // physics ticks its AI too), so it WOULD wander; PenAnimals rings it with a connected fence to hold it in
    // place. In daytime (the golden suite sets /time set day) its despawn behaviour — gated on belowLightLevel
    // — never fires within a scenario window. Returns the entity id, or -1 if the code doesn't resolve.
    // NOTE: a revived animal may nibble the deposited feed, so scenarios assert what the SHEPHERD fills
    // (a "filled at least once" latch), never what the animal consumes.
    public static long SpawnStationaryAnimal(ICoreServerAPI api, string code, BlockPos pos)
    {
        EntityProperties etype = api.World.GetEntityType(new AssetLocation(code));
        if (etype == null) { api.Logger.Warning("[harness] SpawnStationaryAnimal: unknown entity code {0}", code); return -1; }
        Entity e = api.World.ClassRegistry.CreateEntity(etype);
        e.Pos.SetPos(pos.X + 0.5, pos.Y, pos.Z + 0.5);
        e.ServerPos.SetPos(pos.X + 0.5, pos.Y, pos.Z + 0.5);
        api.World.SpawnEntity(e);
        return e.EntityId;
    }
}
