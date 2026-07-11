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
// Empirical findings (headless, raw SetBlock placement):
//  - HEADLESS LOCOMOTION IS TELEPORT-ONLY. With no player connected the server does not simulate entity
//    locomotion physics: a commanded walk vector produces zero displacement, and entities never even
//    settle under gravity (they float at the +0.1 Y a recovery teleport leaves them at). All observed
//    "movement" is AiTaskGotoAndInteract's stuck-recovery teleporting ~2 path nodes at a time along the
//    A* route — positions always land on cell centres (x.50/z.50) in ~3-block jumps at ~18s cadence.
//  - The PATHFINDER is innocent: it routes through every configuration tested — closed doors, closed
//    gates, open gates, gaps, walls-with-holes — see PathfinderProbeScenario (8/8 enclosed probes return
//    direct 7-node paths). Live-game villagers walk and open doors normally; nothing here contradicts it.
//  - Consequently a passage cell a teleport cannot land IN can never be crossed headless: closed doors
//    and closed gates have collision boxes, so the recovery's unsafe-destination check refuses them and
//    the villager freezes at spawn with a walk vector uselessly commanded. The Door case is kept as the
//    re-runnable nav-door probe documenting exactly this (expected: FAIL headless, fine in live play).
//  - Gate (OPENED) and moat pass because every node on their A* route is a legal teleport target. The
//    moat run follows the wading route A* prefers over the trapdoor bridge (+150/cell water penalty
//    notwithstanding) — a routing-preference observation, not a physical-swimming test.
//  - LADDERS: structurally unsupported in the pathfinder itself — VillagerAStarNew assigns climbableCodes
//    but never reads it; only the dead, never-instantiated VillagerAStar had climb logic. Upstream author
//    confirmed climb support was consciously shelved as a trade-off — do not treat as a bug to quick-fix.
// Net: this suite validates A*-route existence and the haul decision layer behind obstacles. It CANNOT
// validate physical locomotion — watch a scenario in a live client for that.
public class ShepherdObstacleNavScenario : IGoldenScenario
{
    private readonly NavObstacle obstacle;
    public ShepherdObstacleNavScenario(NavObstacle obstacle) { this.obstacle = obstacle; }

    public string Name => "shepherd-nav-" + obstacle.ToString().ToLowerInvariant();
    public string Justification =>
        "Aspirational: exercises villager pathfinding through a built " + obstacle + " to reach a required "
        + "resource, then haul. Separate 'nav' suite so a pathfinding limit is a finding, not a masked "
        + "haul regression.";
    public int SettleSeconds => 120;   // full loop lands by ~75s in green runs; margin for slow paths

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

        int wall = BlockId("game:" + WallBlock);
        BuildPerimeter(wall, 2);   // isolate the course
        BuildObstacle(wall);       // the barrier, with a passage at x=0

        int chestB = BlockId("game:chest-east");
        int troughB = BlockId("game:trough-genericwood-small-ns");
        Item grain = api.World.GetItem(new AssetLocation("game:" + Feed));

        feedChest  = PlaceChestWith(0, ChestZ, chestB, new ItemStack(grain, ChestFlax));
        needyTrough = PlaceTroughAt(0, TroughZ, troughB);
        village.RegisterContainer(feedChest);
        village.ScanContainers();

        EntityProperties etype = api.World.GetEntityType(new AssetLocation("vsvillage:villager-female-shepherd"));
        BlockPos vp = new BlockPos(center.X, y, center.Z);
        Entity e = api.World.ClassRegistry.CreateEntity(etype);
        e.Pos.SetPos(vp.X + 0.5, vp.Y, vp.Z + 0.5);
        e.ServerPos.SetPos(vp.X + 0.5, vp.Y, vp.Z + 0.5);
        e.AlwaysActive = true;
        api.World.SpawnEntity(e);
        e.GetBehavior<EntityBehaviorVillager>().Village = village;
        shepherdId = e.EntityId;

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
                // Expected to FAIL headless: teleport-only locomotion can't land in the door cell (its
                // collision box trips the recovery's unsafe-destination check), whether the door is
                // closed or BE-opened — the BE 'opened' flag alone doesn't drop the collision boxes of a
                // raw-placed door (SetupRotationsAndColSelBoxes never re-runs). See the header. Kept as
                // the nav-door probe; in live play villagers open and walk through doors normally.
                int door = BlockId("game:door-solid-aged");
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
                int fence = BlockId("game:woodenfence-aged-ns-free");   // hard barrier
                // OPENED variant on purpose: headless teleport-locomotion can't land in a CLOSED gate's
                // collision box (see header), so this tests traversal through the gate cell, not opening.
                int gate = BlockId("game:woodenfencegate-aged-n-opened-left-free");
                for (int dx = MinX + 1; dx <= MaxX - 1; dx++)
                    api.World.BlockAccessor.SetBlock(dx == 0 ? gate : fence, new BlockPos(center.X + dx, center.Y, center.Z + ObstacleZ));
                break;
            }
            case NavObstacle.Moat:
            {
                int water = BlockId("game:water-still-7");
                int trapdoor = BlockId("game:trapdoor-solid-aged-1");   // closed horizontal slab = dry bridge tile
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
        // Clear the whole arena volume (incl. one below, for the moat trench) back to air; floor is rebuilt.
        int floor = BlockId("game:" + WallBlock);
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

    private int BlockId(string code)
    {
        Block b = api.World.GetBlock(new AssetLocation(code));
        if (b == null) { api.Logger.Warning("[nav-diag] {0}: block '{1}' did not resolve!", Name, code); return 0; }
        return b.BlockId;
    }

    private BlockPos PlaceChestWith(int dx, int dz, int chestBlockId, ItemStack stack)
    {
        BlockPos cp = new BlockPos(center.X + dx, center.Y, center.Z + dz);
        api.World.BlockAccessor.SetBlock(chestBlockId, cp);
        if (api.World.BlockAccessor.GetBlockEntity(cp) is BlockEntityContainer be && be.Inventory != null && be.Inventory.Count > 0)
        {
            be.Inventory[0].Itemstack = stack;
            be.Inventory[0].MarkDirty();
            be.MarkDirty(true);
        }
        return cp;
    }

    private BlockPos PlaceTroughAt(int dx, int dz, int troughBlockId)
    {
        BlockPos tp = new BlockPos(center.X + dx, center.Y, center.Z + dz);
        api.World.BlockAccessor.SetBlock(troughBlockId, tp);
        return tp;
    }

    private int FlaxIn(BlockPos pos)
    {
        if (api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityContainer be && be.Inventory != null)
            return be.Inventory.Where(s => !s.Empty && s.Itemstack.Collectible.Code.Path == Feed).Sum(s => s.StackSize);
        return 0;
    }
}
