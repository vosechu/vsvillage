using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using VsVillage;

namespace VsVillageTest.Scenarios;

// The push GATE, merged: runs the container-fetch behaviour AND the shepherd-feed-haul behaviour
// CONCURRENTLY in one settle window, instead of two serial scenarios each paying its own window.
// One window bounds both, so the gate costs ~max(fetch, haul) instead of fetch + haul.
//
// Two SEPARATE villages, spatially disjoint near spawn:
//  - Village F (2 farmers): proves scan -> rank -> path -> withdraw and the bounds filter.
//  - Village S (1 shepherd): proves fetch feed -> carry -> fill a needy trough while refusing three decoys.
// They MUST be separate: both fetch grain-flax, so one shared village would enrol the shepherd's feed
// chest as a farmer-reachable container and let a FARMER drain it, falsely satisfying the haul's
// "chest drained" check. The two village radii are sized and placed so neither's ScanContainers reaches
// the other's containers (verified in Setup's layout comment), keeping the behaviours independent.
//
// Justification: both loops only exist at runtime (AI tick + pathfinder + live containers/troughs); no
// pure-value unit test can exercise decision->path->withdraw->deposit. This is the headline inventory
// gate. When it goes RED, the standalone `container` and `feedhaul` suites re-run each behaviour in
// isolation to localise the failure — the merge trades per-run isolation, not the ability to get it.
//
// Durability: same discipline as the two source scenarios — flat floor (TestScene.BuildFlatArea) and
// window-accumulated order-independent invariants (never an end-of-run snapshot). A client parked on the
// arena keeps the chunks loaded, so reads are reliable.
public class HaulGateScenario : IGoldenScenario
{
    public string Name => "haul-gate";
    public string Justification =>
        "Runtime-only fetch + haul loops (AI tick + pathfinder + live containers/troughs); no unit test "
        + "can cover them. The headline inventory gate, run as one window; the standalone container/feedhaul "
        + "suites localise a red gate. Flat floor + window-accumulated invariants keep it durable.";
    public int SettleSeconds => 70;   // upper bound; early exit below once both loops land + negatives got a fair window

    // Early exit, guarded for the NEGATIVE checks: positives + liveness flipping true says both loops
    // worked, but "control/decoy never touched" needs a real observation window to mean anything — so
    // never settle before MinWindowSeconds however fast the loops completed. Matches the haul (slower) leg.
    private const int MinWindowSeconds = 45;
    private int sampleCount;
    public bool IsSettled =>
        sampleCount >= MinWindowSeconds
        // fetch positives
        && sawADrained && sawBDrained && sawFarmerCarry
        // haul positives
        && seededOk && sawTroughFilled && sawChestDrained && sawShepherdCarry;

    private const int GrainPerChest = 16;   // fetch chests A, B, and the out-of-bounds control C
    private const int ChestFlax = 16;       // feed chest: one full 8-capacity trough + surplus
    private const int DecoyItems = 16;      // cobblestone in the non-feed decoy chest
    private const int FullTroughFlax = 8;   // small-trough grain capacity = quantityPerFillLevel 1 * maxFillLevels 8
    private const int FetchRadius = 6;      // village F
    private const int HaulRadius = 8;       // village S
    private const string Feed = "grain-flax";
    private const string DecoyBlock = "cobblestone-granite";

    private ICoreServerAPI api;
    private Village villageF, villageS;
    private BlockPos center;

    // Fetch arena (village F)
    private readonly List<long> farmerIds = new List<long>();
    private BlockPos inA, inB, outC;
    private bool sawADrained, sawBDrained, sawFarmerCarry, fetchControlTouched;

    // Haul arena (village S)
    private readonly List<long> shepherdIds = new List<long>();
    private long chickenId = -1;      // consumer by the empty trough (feed feature refuses a trough with no animal)
    private BlockPos feedChest, decoyChest, emptyTrough, fullTrough;
    private bool sawTroughFilled, sawChestDrained, sawShepherdCarry;
    private bool controlEverChanged, decoyEverDrained, sawNonFeedCarry;
    private bool seededOk;

    private long sampleTickId = -1;

    public void Setup(ICoreServerAPI sapi)
    {
        api = sapi;
        VillageManager vm = api.ModLoader.GetModSystem<VillageManager>();
        BlockPos spawn = api.World.DefaultSpawnPosition.AsBlockPos;

        // Floor must span both arenas (max offset ~ +13 x, +23 z).
        int y = TestScene.BuildFlatArea(api, spawn, 16, 26);
        center = new BlockPos(spawn.X, y, spawn.Z);

        Block chest = api.World.GetBlock(new AssetLocation("game:chest-east"));
        Block troughBlock = api.World.GetBlock(new AssetLocation("game:trough-genericwood-small-ns"));
        Block wall = api.World.GetBlock(new AssetLocation("game:" + DecoyBlock));
        Item grain = api.World.GetItem(new AssetLocation("game:" + Feed));

        // --- Fetch arena: village F at (3,3), radius 6. Chests A & B in-bounds; control C at dist 10, out. ---
        BlockPos cf = At(3, 3);
        villageF = new Village { Pos = cf.Copy(), Radius = FetchRadius, Name = "golden-gate-fetch" };
        villageF.Init(api);
        vm.Villages.TryAdd(villageF.Id, villageF);
        inA  = ScenarioKit.PlaceContainer(api, At(6, 3), chest.Id, new ItemStack(grain, GrainPerChest));    // dist 3 from cf: in
        inB  = ScenarioKit.PlaceContainer(api, At(3, 6), chest.Id, new ItemStack(grain, GrainPerChest));    // dist 3 from cf: in
        outC = ScenarioKit.PlaceContainer(api, At(13, 3), chest.Id, new ItemStack(grain, GrainPerChest));   // dist 10 from cf: control (out)
        villageF.RegisterContainer(inA);
        villageF.RegisterContainer(inB);
        villageF.ScanContainers();

        // --- Haul arena: village S at (3,16), radius 8. Decoys nearest; feed chest & needy trough up the +z lane. ---
        // Placement keeps S's radius 8 clear of every fetch container (nearest is inB at dist 10) and clear of
        // control C (dist ~16); F's radius 6 is clear of every haul container (nearest is fullTrough at dist ~13).
        // So neither village's ScanContainers cross-enrols the other's containers — the behaviours stay independent.
        BlockPos cs = At(3, 16);
        villageS = new Village { Pos = cs.Copy(), Radius = HaulRadius, Name = "golden-gate-haul" };
        villageS.Init(api);
        vm.Villages.TryAdd(villageS.Id, villageS);
        fullTrough  = ScenarioKit.PlaceContainer(api, At(5, 16), troughBlock.Id, new ItemStack(grain, FullTroughFlax)); // full control trough, NEAREST, off-lane
        decoyChest  = ScenarioKit.PlaceContainer(api, At(5, 18), chest.Id, new ItemStack(wall, DecoyItems));   // non-feed decoy chest, off-lane
        feedChest   = ScenarioKit.PlaceContainer(api, At(3, 19), chest.Id, new ItemStack(grain, ChestFlax));   // real source, on +z lane (dist 3)
        emptyTrough = ScenarioKit.PlaceContainer(api, At(3, 22), troughBlock.Id, null);                        // needy target, farthest (dist 6)
        ScenarioKit.WallRing(api, decoyChest, wall.BlockId, 2);   // wrap the decoy: only impedes a shepherd that WRONGLY tries for it
        villageS.RegisterContainer(feedChest);        // troughs are excluded by the source fix; only the feed chest enrols
        villageS.ScanContainers();

        // A penned hen gives the empty trough a consumer — the feed feature refuses to fill a trough with
        // no animal nearby. Small trough + grain-flax = chicken feed. Fenced in place so it stays a fixed
        // consumer once a client revives it (see ScenarioKit.PenAnimal). Off the trough's approach lane.
        chickenId = ScenarioKit.PenAnimal(api, "game:chicken-hen", At(2, 22));

        // Seed-sanity: a mis-seed (wrong feed code / trough won't accept it) makes the haul a silent no-op
        // with every check red and no diagnosis. Capture the precondition so it fails loudly instead.
        if (api.World.BlockAccessor.GetBlockEntity(emptyTrough) is BlockEntityTrough et)
            seededOk = ShepherdTroughs.NeedsFeed(et) && ShepherdTroughs.AcceptsItem(et, new ItemStack(grain, 1));

        // Cast: 2 farmers in village F, 1 shepherd in village S. AlwaysActive MUST precede SpawnEntity.
        EntityProperties farmer = api.World.GetEntityType(new AssetLocation("vsvillage:villager-female-farmer"));
        for (int k = 0; k < 2; k++)
            farmerIds.Add(ScenarioKit.SpawnVillager(api, farmer, At(4 - 2 * k, 4), villageF));   // (4,4) and (2,4), by village F
        EntityProperties shepherd = api.World.GetEntityType(new AssetLocation("vsvillage:villager-female-shepherd"));
        shepherdIds.Add(ScenarioKit.SpawnVillager(api, shepherd, At(3, 16), villageS));          // at village S centre

        sampleTickId = api.Event.RegisterGameTickListener(_ => Sample(), 1000);

        Note("━━━━━ GOLDEN TEST: haul-gate (fetch + feed-haul in one window) ━━━━━");
        Note("Village F: 2 farmers, chests A & B in-bounds, control C 10 out — must never touch C.");
        Note("Village S: 1 shepherd; nearest are a FULL trough and a walled non-feed chest; the real feed chest and empty trough are farther up the lane.");
        Note("WATCH: farmers shuttle A & B home and skip C; the shepherd ignores both decoys and hauls grain into the empty trough. Auto-teardown in " + SettleSeconds + "s.");
    }

    // One observation, folded into the accumulators. Each flag only flips false->true, so a single true
    // reading anywhere in the window is enough and the watch-note fires exactly once (on the transition).
    private void Sample()
    {
        sampleCount++;

        // --- Fetch positives ---
        if (!sawADrained && GrainIn(inA) == 0) { sawADrained = true; Note("✅ In-bounds chest A emptied — a farmer reached it."); }
        if (!sawBDrained && GrainIn(inB) == 0) { sawBDrained = true; Note("✅ In-bounds chest B emptied — the fetch loop reached the second chest too."); }
        if (!sawFarmerCarry && farmerIds.Any(id => CarryPath(id) == Feed)) { sawFarmerCarry = true; Note("🌾 A farmer is carrying grain-flax — withdraw-into-carry works."); }
        // Fetch negative
        if (!fetchControlTouched && GrainIn(outC) != GrainPerChest) { fetchControlTouched = true; Note("❌ Out-of-bounds control C was touched — bounds filter LEAKED."); }

        // --- Haul positives ---
        if (!sawTroughFilled && FlaxIn(emptyTrough) > 0) { sawTroughFilled = true; Note("✅ Empty pen trough now holds grain — hauled in."); }
        if (!sawChestDrained && FlaxIn(feedChest) < ChestFlax) { sawChestDrained = true; Note("✅ Feed chest drained — grain was HAULED, not conjured."); }
        if (!sawShepherdCarry && shepherdIds.Any(id => CarryPath(id) == Feed)) { sawShepherdCarry = true; Note("🌾 Shepherd carrying grain — fetch-into-carry works."); }
        // Haul negatives
        if (!controlEverChanged && FlaxIn(fullTrough) != FullTroughFlax) { controlEverChanged = true; Note("❌ Full control trough changed — NeedsFeed gate LEAKED."); }
        if (!decoyEverDrained && TotalItemsIn(decoyChest) < DecoyItems) { decoyEverDrained = true; Note("❌ Non-feed decoy chest drained — the shepherd fetched junk."); }
        if (!sawNonFeedCarry && shepherdIds.Any(id => { string p = CarryPath(id); return p != null && p != Feed; })) { sawNonFeedCarry = true; Note("❌ Shepherd carried a non-feed item."); }
    }

    public void Assert(ScenarioReport report)
    {
        // Fetch
        report.Check("fetch: in-bounds chest A was fetched from (emptied at least once)", sawADrained);
        report.Check("fetch: in-bounds chest B was fetched from (emptied at least once)", sawBDrained);
        report.Check("fetch: a farmer carried grain-flax at some point", sawFarmerCarry);
        report.Check("fetch: out-of-bounds control was never touched (bounds filter)", !fetchControlTouched);
        // Haul
        report.Check("haul: scenario seeded — empty trough is needy and accepts the feed", seededOk);
        report.Check("haul: empty pen trough was filled at least once", sawTroughFilled);
        report.Check("haul: feed chest was drained at least once (hauled, not conjured)", sawChestDrained);
        report.Check("haul: a shepherd carried feed at some point", sawShepherdCarry);
        report.Check("haul: full control trough was never touched (NeedsFeed gate)", !controlEverChanged);
        report.Check("haul: non-feed decoy chest was never drained (type gate)", !decoyEverDrained);
        report.Check("haul: a shepherd never carried a non-feed item", !sawNonFeedCarry);

        Note("━━━━━ RESULT ━━━━━");
        Note("FETCH  " + (sawADrained ? "✅" : "❌") + " A   " + (sawBDrained ? "✅" : "❌") + " B   " + (sawFarmerCarry ? "✅" : "❌") + " carried   " + (!fetchControlTouched ? "✅" : "❌") + " control untouched");
        Note("HAUL   " + (sawTroughFilled ? "✅" : "❌") + " trough   " + (sawChestDrained ? "✅" : "❌") + " chest   " + (!controlEverChanged ? "✅" : "❌") + " full-trough   " + (!decoyEverDrained ? "✅" : "❌") + " decoy   " + (!sawNonFeedCarry ? "✅" : "❌") + " no junk");
        Note(report.Passed ? "PASS — every invariant held." : "FAIL — see the ❌ above.");
    }

    public void Teardown()
    {
        if (sampleTickId >= 0) { api.Event.UnregisterGameTickListener(sampleTickId); sampleTickId = -1; }
        // Release claims before despawning so none bleeds into the next scenario in one boot (the "all"
        // suite runs several with no server restart between them to reset the process-static registries).
        ScenarioKit.ReleaseClaims(farmerIds.Concat(shepherdIds),
            new[] { inA, inB, outC, feedChest, decoyChest, emptyTrough, fullTrough });
        foreach (long id in farmerIds.Concat(shepherdIds))
            api.World.GetEntityById(id)?.Die(EnumDespawnReason.Removed);
        farmerIds.Clear();
        shepherdIds.Clear();
        if (chickenId >= 0) { api.World.GetEntityById(chickenId)?.Die(EnumDespawnReason.Removed); chickenId = -1; }
        foreach (BlockPos p in new[] { inA, inB, outC, feedChest, decoyChest, emptyTrough, fullTrough }.Where(p => p != null))
        {
            if (api.World.BlockAccessor.GetBlockEntity(p) is BlockEntityContainer be && be.Inventory != null)
                be.Inventory.Clear();
            api.World.BlockAccessor.SetBlock(0, p);
        }
        if (decoyChest != null) ScenarioKit.WallRing(api, decoyChest, 0, 2);
        VillageManager vm = api.ModLoader.GetModSystem<VillageManager>();
        if (villageF != null) vm?.Villages.TryRemove(villageF.Id, out _);
        if (villageS != null) vm?.Villages.TryRemove(villageS.Id, out _);
        Note("🧹 Scene torn down — villagers despawned, blocks removed, both villages unregistered.");
    }

    // Maps an arena offset to a world position.
    private BlockPos At(int dx, int dz) => new BlockPos(center.X + dx, center.Y, center.Z + dz);

    // Watch-mode narration + shared reads: impls live in ScenarioKit; these thin aliases keep the dense
    // Sample()/Assert() lines terse and give the feed-count reads their local names.
    private void Note(string msg) => ScenarioKit.Note(api, msg);
    private string CarryPath(long id) => ScenarioKit.CarryPath(api, id);
    private int FlaxIn(BlockPos pos) => ScenarioKit.ItemCountIn(api, pos, Feed);
    private int GrainIn(BlockPos pos) => FlaxIn(pos);
    private int TotalItemsIn(BlockPos pos) => ScenarioKit.TotalItemsIn(api, pos);
}
