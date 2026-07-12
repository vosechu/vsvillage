using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using VsVillage;

namespace VsVillageTest.Scenarios;

// DIAGNOSTIC probe (suite "nav-probe"): calls VillagerAStarNew.FindPath directly — no AI, no settle —
// across a matrix of obstacle configurations. Written to pinpoint why a closed door/gate froze the
// obstacle-nav shepherd; the answer exonerated the pathfinder (8/8 enclosed configurations return direct
// 7-node paths, closed doors and gates included). The real culprit was the engine skipping physics for
// client-untracked entities — fixed by HeadlessPhysicsDriver. Kept: "does A* accept this block?" in seconds.
public class PathfinderProbeScenario : IGoldenScenario
{
    public string Name => "pathfinder-probe";
    public string Justification =>
        "Diagnostic: direct FindPath probes across obstacle configurations to localise the closed-door/"
        + "closed-gate pathing failure seen headless. No AI; results are logged and reported as checks.";
    public int SettleSeconds => 2;

    private ICoreServerAPI api;
    private BlockPos center;
    private long villagerId = -1;
    private readonly List<(string desc, bool pathFound)> results = new List<(string, bool)>();

    public void Setup(ICoreServerAPI sapi)
    {
        api = sapi;
        BlockPos spawn = api.World.DefaultSpawnPosition.AsBlockPos;
        int y = TestScene.BuildFlatArea(api, spawn, 10, 10);
        center = new BlockPos(spawn.X, y, spawn.Z);

        // A probe entity for the pathfinder's collision box (never AI-ticks during Setup).
        EntityProperties etype = api.World.GetEntityType(new AssetLocation("vsvillage:villager-female-shepherd"));
        Entity e = api.World.ClassRegistry.CreateEntity(etype);
        e.Pos.SetPos(center.X + 0.5, center.Y, center.Z + 0.5);
        e.ServerPos.SetPos(center.X + 0.5, center.Y, center.Z + 0.5);
        api.World.SpawnEntity(e);
        villagerId = e.EntityId;

        int wall = BlockId("game:cobblestone-granite");
        int fence = BlockId("game:woodenfence-aged-ns-free");
        int gateClosed = BlockId("game:woodenfencegate-aged-n-closed-left-free");
        int gateOpened = BlockId("game:woodenfencegate-aged-n-opened-left-free");
        int door = BlockId("game:door-solid-aged");

        // ENCLOSE the probe area (2-high perimeter) so a "PATH" answer can only mean "through the
        // passage cell" — an early un-enclosed version returned around-the-row-end paths, which made
        // closed doors look traversable without ever proving the door cell itself was.
        for (int d = -5; d <= 5; d++)
            for (int dy = 0; dy < 2; dy++)
            {
                api.World.BlockAccessor.SetBlock(wall, new BlockPos(center.X + d, center.Y + dy, center.Z - 1));
                api.World.BlockAccessor.SetBlock(wall, new BlockPos(center.X + d, center.Y + dy, center.Z + 7));
                api.World.BlockAccessor.SetBlock(wall, new BlockPos(center.X - 5, center.Y + dy, center.Z + d + 1));
                api.World.BlockAccessor.SetBlock(wall, new BlockPos(center.X + 5, center.Y + dy, center.Z + d + 1));
            }

        BlockPos goal = new BlockPos(center.X, center.Y, center.Z + 6);   // far side of the barrier row at z+3

        // Probe matrix. Row = barrier row across x-4..x+4 at z+3 (passage cell at x=0). Between probes the
        // row is rebuilt from scratch so configurations can't contaminate each other.
        Probe(e, goal, "open floor (control)", null, 0, 0);
        Probe(e, goal, "fence row + CLOSED gate", fence, gateClosed, 1);
        Probe(e, goal, "fence row + OPENED gate", fence, gateOpened, 1);
        Probe(e, goal, "fence row + gap (no gate)", fence, 0, 1);
        Probe(e, goal, "CLOSED gate alone (no fences)", null, gateClosed, 1);
        Probe(e, goal, "2-high wall + CLOSED door", wall, door, 2);
        Probe(e, goal, "CLOSED door alone (no wall)", null, door, 1);
        Probe(e, goal, "2-high wall + gap (control)", wall, 0, 2);
        ClearRow(2);
    }

    // Rebuilds the barrier row (or clears it when rowBlock=null), sets the passage cell (0 = air), then
    // runs one direct FindPath from the probe entity to the goal and records the outcome.
    private void Probe(Entity e, BlockPos goal, string desc, int? rowBlock, int passageBlock, int rowHeight)
    {
        ClearRow(2);
        if (rowBlock != null)
            for (int dx = -4; dx <= 4; dx++)
            {
                if (dx == 0) continue;
                for (int dy = 0; dy < rowHeight; dy++)
                    api.World.BlockAccessor.SetBlock(rowBlock.Value, new BlockPos(center.X + dx, center.Y + dy, center.Z + 3));
            }
        if (passageBlock != 0)
            api.World.BlockAccessor.SetBlock(passageBlock, new BlockPos(center.X, center.Y, center.Z + 3));

        VillagerAStarNew pf = new VillagerAStarNew(api.World.GetCachingBlockAccessor(false, false), api.World, e as EntityAgent);
        pf.blockAccessor.Begin();
        pf.SetEntityCollisionBox(e as EntityAgent);
        BlockPos start = pf.GetStartPos(e.Pos.XYZ);
        List<VillagerPathNode> path = pf.FindPath(start, goal);
        pf.blockAccessor.Commit();

        bool found = path != null && path.Count > 0;
        results.Add((desc, found));
        api.Logger.Notification("[probe] {0}: {1}", desc, found ? "PATH (" + path.Count + " nodes)" : "NO PATH");
    }

    private void ClearRow(int height)
    {
        for (int dx = -4; dx <= 4; dx++)
            for (int dy = 0; dy < height; dy++)
                api.World.BlockAccessor.SetBlock(0, new BlockPos(center.X + dx, center.Y + dy, center.Z + 3));
    }

    public void Assert(ScenarioReport report)
    {
        // Diagnostic: report each probe verbatim; the CHECK line text carries the answer either way.
        foreach ((string desc, bool found) in results)
            report.Check("path found: " + desc, found);
    }

    public void Teardown()
    {
        api.World.GetEntityById(villagerId)?.Die(EnumDespawnReason.Removed);
        ClearRow(2);
        for (int d = -5; d <= 5; d++)   // clear the perimeter ring too
            for (int dy = 0; dy < 2; dy++)
            {
                api.World.BlockAccessor.SetBlock(0, new BlockPos(center.X + d, center.Y + dy, center.Z - 1));
                api.World.BlockAccessor.SetBlock(0, new BlockPos(center.X + d, center.Y + dy, center.Z + 7));
                api.World.BlockAccessor.SetBlock(0, new BlockPos(center.X - 5, center.Y + dy, center.Z + d + 1));
                api.World.BlockAccessor.SetBlock(0, new BlockPos(center.X + 5, center.Y + dy, center.Z + d + 1));
            }
    }

    private int BlockId(string code)
    {
        Block b = api.World.GetBlock(new AssetLocation(code));
        if (b == null) api.Logger.Warning("[probe] block '{0}' did not resolve!", code);
        return b?.BlockId ?? 0;
    }
}
