using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using VsVillage;

namespace VsVillageTest.Scenarios;

// The obstacle each instance of the nav scenario gates the shepherd behind.
public enum NavObstacle { Door, FenceGate, Moat }

// ASPIRATIONAL navigation exploration (separate "nav" suite, NOT the pre-push golden gate).
//
// An isolated, perimeter-walled arena. A shepherd starts at the south end; a feed chest and a needy
// trough sit past a single obstacle at the north end, laid out on a clear collinear axis (same geometry
// test A proved reliable). The shepherd must PATH THROUGH the obstacle to reach the work area, then haul
// feed. We assert it crossed AND fetched (navigation proof), log whether it also filled (bonus), and
// trace its position each second so we can SEE how it solved the obstacle.
//
// Why a separate suite: gating a REQUIRED resource behind built obstacles can fail a CORRECT shepherd,
// so a failure here is a navigation FINDING, not a masked haul regression — it must never touch the
// pre-push golden gate.
//
// Empirical findings (raw SetBlock placement):
//  - Real locomotion requires a nearby connected CLIENT. The engine skips OnPhysicsTick for every entity
//    with IsTracked == 0 (set purely by distance to the nearest connected client — AlwaysActive keeps AI
//    ticking but does not exempt physics), so a playerless server simulates no entity physics at all.
//    Without a client, all apparent movement was AiTaskGotoAndInteract's stuck-recovery teleporting
//    ~2 path nodes at a time — which also meant a passage cell a teleport can't land in (a closed
//    door/gate's collision box) could never be crossed. With a client parked on the arena (golden-suite.sh
//    launches one), villagers genuinely walk and DoorPathHelper opens closed doors and gates. Proven with
//    a control: no client froze the shepherd at spawn (nav-gate FAIL); a client crossed it (nav-gate PASS).
//  - With real locomotion ALL THREE obstacles pass the full haul loop: closed DOOR (opened en route),
//    closed FENCE GATE (opened en route), and the 2-wide MOAT. The pathfinder was never the problem —
//    PathfinderProbeScenario shows 8/8 enclosed configurations return direct paths.
//  - LADDERS remain structurally unsupported in the pathfinder — VillagerAStarNew assigns climbableCodes
//    but never reads it; only the dead, never-instantiated VillagerAStar had climb logic. Upstream author
//    confirmed climb support was consciously shelved as a trade-off — do not treat as a bug to quick-fix.
//  - Scenario worlds are FRESH per suite run (golden-suite.sh wipes the data dir): terrain edits are
//    permanent and imperfect teardowns left ghosts (a stale moat trench once stalled a later run).
public class ShepherdObstacleNavScenario : IGoldenScenario
{
    private readonly NavObstacle obstacle;
    public ShepherdObstacleNavScenario(NavObstacle obstacle) { this.obstacle = obstacle; }

    // Aspirational: a pathfinding limit is a navigation FINDING, not a gate failure, so keep nav out of the
    // auto-discovered `all` run. Still runnable via the `nav` / `nav-door` / ... named suites.
    public bool InAllSuite => false;

    // All obstacle variants — used by the `nav` suite and by auto-discovery (which then filters via InAllSuite).
    public static IEnumerable<IGoldenScenario> Variants() => new IGoldenScenario[]
    {
        new ShepherdObstacleNavScenario(NavObstacle.Door),
        new ShepherdObstacleNavScenario(NavObstacle.FenceGate),
        new ShepherdObstacleNavScenario(NavObstacle.Moat),
    };

    public string Name => "shepherd-nav-" + obstacle.ToString().ToLowerInvariant();
    public string Justification =>
        "Aspirational: exercises villager pathfinding through a built " + obstacle + " to reach a required "
        + "resource, then haul. Separate 'nav' suite so a pathfinding limit is a finding, not a masked "
        + "haul regression.";
    public int SettleSeconds => 90;   // upper bound only — all checks are positive, so we exit early
    public bool IsSettled => sawCrossed && sawShepherdCarry && sawTroughFilled;

    private const int ChestFlax = 16;
    private const int VillageRadius = 24;
    private const string Feed = "grain-flax";
    private const string WallBlock = "cobblestone-granite";

    // Arena extent (offsets from center). Compact (villager moves ~1 block / 8-20s) but roomy enough that
    // the post-obstacle haul has test-A-clean geometry.
    private const int MinX = -6, MaxX = 6, MinZ = -2, MaxZ = 13;
    private const int ObstacleZ = 3;   // the obstacle sits at z=ObstacleZ (moat also fills z+1); passage at x=0
    private const int ChestZ = 7;      // feed chest, past the obstacle
    private const int TroughZ = 10;    // needy trough, collinear 3 past the chest (test-A fill geometry)

    private int ObstacleEndZ => obstacle == NavObstacle.Moat ? ObstacleZ + 1 : ObstacleZ;

    private ICoreServerAPI api;
    private Village village;
    private long shepherdId = -1;
    private BlockPos feedChest, needyTrough, center;
    private long chickenId = -1;      // consumer by the trough (feed feature refuses a trough with no animal)
    private bool sawCrossed, sawShepherdCarry, sawTroughFilled;
    private long sampleTickId = -1;

    public void Setup(ICoreServerAPI sapi)
    {
        api = sapi;
        VillageManager vm = api.ModLoader.GetModSystem<VillageManager>();
        BlockPos spawn = api.World.DefaultSpawnPosition.AsBlockPos;

        int y = TestScene.BuildFlatArea(api, spawn, 9, 16);
        center = new BlockPos(spawn.X, y, spawn.Z);

        village = new Village { Pos = center.Copy(), Radius = VillageRadius, Name = "golden-" + Name };
        village.Init(api);
        vm.Villages.TryAdd(village.Id, village);

        int wall = ScenarioKit.BlockId(api, "game:" + WallBlock);
        BuildPerimeter(wall, 2);   // isolate the course
        BuildObstacle(wall);       // the barrier, with a passage at x=0

        int chestB = ScenarioKit.BlockId(api, "game:chest-east");
        int troughB = ScenarioKit.BlockId(api, "game:trough-genericwood-small-ns");
        Item grain = api.World.GetItem(new AssetLocation("game:" + Feed));

        feedChest  = ScenarioKit.PlaceContainer(api, new BlockPos(center.X, center.Y, center.Z + ChestZ), chestB, new ItemStack(grain, ChestFlax));
        needyTrough = ScenarioKit.PlaceContainer(api, new BlockPos(center.X, center.Y, center.Z + TroughZ), troughB, null);
        // Consumer for the trough past the obstacle: the feed feature refuses to fill a trough with no
        // animal nearby. Small trough + grain-flax = chicken. Placed on the far side, beside the trough.
        chickenId = ScenarioKit.PenAnimal(api, "game:chicken-hen", needyTrough.AddCopy(1, 0, 0));
        village.RegisterContainer(feedChest);
        village.ScanContainers();

        EntityProperties etype = api.World.GetEntityType(new AssetLocation("vsvillage:villager-female-shepherd"));
        shepherdId = ScenarioKit.SpawnVillager(api, etype, new BlockPos(center.X, y, center.Z), village);

        sampleTickId = api.Event.RegisterGameTickListener(_ => Sample(), 1000);
        api.Logger.Notification("[nav-diag] {0}: obstacle at z={1}..{2}, chest z={3}, trough z={4}", Name, ObstacleZ, ObstacleEndZ, ChestZ, TroughZ);
    }

    // Builds the barrier spanning the arena width at z=ObstacleZ, with a 1-wide passage at x=0.
    private void BuildObstacle(int wall)
    {
        switch (obstacle)
        {
            case NavObstacle.Door:
            {
                // With real locomotion (a connected client nearby) the shepherd walks up and
                // DoorPathHelper opens the door en route (the BE 'opened' seed below just matches how a
                // raw-placed door is usually encountered mid-village; the closed state also passes).
                int door = ScenarioKit.BlockId(api, "game:door-solid-aged");
                for (int dx = MinX + 1; dx <= MaxX - 1; dx++)
                {
                    if (dx == 0) continue;
                    for (int dy = 0; dy < 2; dy++)
                        api.World.BlockAccessor.SetBlock(wall, new BlockPos(center.X + dx, center.Y + dy, center.Z + ObstacleZ));
                }
                BlockPos doorPos = new BlockPos(center.X, center.Y, center.Z + ObstacleZ);
                api.World.BlockAccessor.SetBlock(door, doorPos);
                if (api.World.BlockAccessor.GetBlockEntity(doorPos) is BlockEntity dbe)
                {
                    TreeAttribute tree = new TreeAttribute();
                    dbe.ToTreeAttributes(tree);
                    tree.SetBool("opened", true);
                    dbe.FromTreeAttributes(tree, api.World);
                    dbe.MarkDirty(true);
                }
                break;
            }
            case NavObstacle.FenceGate:
            {
                int fence = ScenarioKit.BlockId(api, "game:woodenfence-aged-ns-free");   // hard barrier
                // CLOSED gate: with real locomotion (a connected client nearby), the villager walks
                // up and DoorPathHelper opens it — the realistic case (a shepherd entering a pen).
                int gate = ScenarioKit.BlockId(api, "game:woodenfencegate-aged-n-closed-left-free");
                for (int dx = MinX + 1; dx <= MaxX - 1; dx++)
                    api.World.BlockAccessor.SetBlock(dx == 0 ? gate : fence, new BlockPos(center.X + dx, center.Y, center.Z + ObstacleZ));
                break;
            }
            case NavObstacle.Moat:
            {
                int water = ScenarioKit.BlockId(api, "game:water-still-7");
                int trapdoor = ScenarioKit.BlockId(api, "game:trapdoor-solid-aged-1");   // closed horizontal slab = dry bridge tile
                for (int dz = ObstacleZ; dz <= ObstacleZ + 1; dz++)      // 2-wide moat
                    for (int dx = MinX + 1; dx <= MaxX - 1; dx++)
                    {
                        BlockPos cell = new BlockPos(center.X + dx, center.Y - 1, center.Z + dz);   // dug into the floor
                        if (dx == 0) api.World.BlockAccessor.SetBlock(trapdoor, cell);
                        else api.World.BlockAccessor.SetBlock(water, cell);   // BlockWater auto-reroutes to fluid layer
                    }
                break;
            }
        }
    }

    private void Sample()
    {
        Entity e = api.World.GetEntityById(shepherdId);
        if (e != null)
        {
            BlockPos p = e.Pos.AsBlockPos;
            string carry = e.GetBehavior<EntityBehaviorVillager>()?.CarrySlot?.Collectible?.Code?.Path ?? "empty";
            // Trace: which task holds each AI slot + exact position and walk vector. This is how the
            // teleport-only-locomotion finding was made (walk commanded, zero displacement) — keep it;
            // reading how a run moved is this suite's whole point.
            var tm = e.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager;
            string slots = tm == null ? "?" : string.Join(",", tm.ActiveTasksBySlot.Select((t, i) => t == null ? null : i + ":" + t.GetType().Name).Where(s => s != null));
            var agent = e as EntityAgent;
            api.Logger.Notification("[nav-diag] {0} xyz=({1:F2},{2:F2},{3:F2}) walk=({4:F4},{5:F4}) carry={6} active=[{7}]",
                Name, e.Pos.X, e.Pos.Y, e.Pos.Z,
                agent?.Controls?.WalkVector?.X ?? 0, agent?.Controls?.WalkVector?.Z ?? 0, carry, slots);
            if (p.Z - center.Z > ObstacleEndZ) sawCrossed = true;
            if (carry == Feed) sawShepherdCarry = true;
        }
        if (!sawTroughFilled && FlaxIn(needyTrough) > 0) sawTroughFilled = true;
    }

    public void Assert(ScenarioReport report)
    {
        // Navigation proof: carrying feed means the shepherd pathed THROUGH the obstacle to the chest on
        // the far side. Filling is the bonus full-loop check (test A already gates the haul loop).
        report.Check("shepherd crossed the " + obstacle + " to the far side", sawCrossed);
        report.Check("shepherd fetched feed from beyond the " + obstacle, sawShepherdCarry);
        report.Check("shepherd filled the trough past the " + obstacle + " (full loop)", sawTroughFilled);
    }

    public void Teardown()
    {
        if (sampleTickId >= 0) { api.Event.UnregisterGameTickListener(sampleTickId); sampleTickId = -1; }
        api.World.GetEntityById(shepherdId)?.Die(EnumDespawnReason.Removed);
        if (chickenId >= 0) { api.World.GetEntityById(chickenId)?.Die(EnumDespawnReason.Removed); chickenId = -1; }
        // Clear the whole arena volume (incl. one below, for the moat trench) back to air; floor is rebuilt.
        int floor = ScenarioKit.BlockId(api, "game:" + WallBlock);
        for (int dx = MinX; dx <= MaxX; dx++)
            for (int dz = MinZ; dz <= MaxZ; dz++)
            {
                api.World.BlockAccessor.SetBlock(floor, new BlockPos(center.X + dx, center.Y - 1, center.Z + dz));  // restore floor (fills moat)
                for (int dy = 0; dy < 3; dy++)
                    api.World.BlockAccessor.SetBlock(0, new BlockPos(center.X + dx, center.Y + dy, center.Z + dz));
            }
        if (village != null)
            api.ModLoader.GetModSystem<VillageManager>()?.Villages.TryRemove(village.Id, out _);
    }

    private void BuildPerimeter(int wallBlockId, int height)
    {
        for (int dx = MinX; dx <= MaxX; dx++)
            for (int dy = 0; dy < height; dy++)
            {
                api.World.BlockAccessor.SetBlock(wallBlockId, new BlockPos(center.X + dx, center.Y + dy, center.Z + MinZ));
                api.World.BlockAccessor.SetBlock(wallBlockId, new BlockPos(center.X + dx, center.Y + dy, center.Z + MaxZ));
            }
        for (int dz = MinZ; dz <= MaxZ; dz++)
            for (int dy = 0; dy < height; dy++)
            {
                api.World.BlockAccessor.SetBlock(wallBlockId, new BlockPos(center.X + MinX, center.Y + dy, center.Z + dz));
                api.World.BlockAccessor.SetBlock(wallBlockId, new BlockPos(center.X + MaxX, center.Y + dy, center.Z + dz));
            }
    }

    private int FlaxIn(BlockPos pos) => ScenarioKit.ItemCountIn(api, pos, Feed);
}
