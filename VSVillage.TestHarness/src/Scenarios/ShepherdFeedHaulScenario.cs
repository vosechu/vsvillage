using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using VsVillage;

namespace VsVillageTest.Scenarios;

// Behavioral: proves the shepherd feed-haul loop end-to-end — fetch feed from a village container
// (AI tick + pathfinder + live chest) -> carry it -> fill a pen trough that needs feed -> while
// correctly refusing three decoys that a proximity- or type-blind shepherd would fall for.
//
// Justification: the haul loop only exists at runtime (AI tick + pathfinder + live containers and
// troughs); no pure-value unit test can exercise decision->path->withdraw->deposit. It protects the
// headline "haul, don't conjure" behavior AND the source fix that keeps troughs out of the storage
// container index (troughs are BlockEntityContainers; enrolling them let a shepherd fetch feed OUT of
// a full trough and let ReturnCarry dump carry INTO one, silently satisfying "trough filled").
//
// Discrimination confounds (all INSIDE the village; a correct shepherd must ignore all three):
//  - a FULL control trough, the NEAREST object of all — a proximity-blind or NeedsFeed-broken shepherd
//    targets it; a correct one skips it (not needy) and it stays untouched.
//  - a NON-FEED decoy chest (cobblestone), also nearer than the feed chest and walled off — a
//    type-blind "nearest container" fetch drains it; a correct one rejects it (trough won't accept it).
//  - the feed chest, farther than both decoys — proves selection is need/type-based, not nearest.
//
// Durability notes:
//  - Deterministic FLAT floor (TestScene.BuildFlatArea); the feed chest and needy trough sit on one
//    clear collinear axis so the fetch and fill legs are short, open, and reliable headless.
//  - Day-gated tasks (villager.json duringDayTimeFrames 8-17): the golden runner issues "/time set day"
//    before running, so the clock sits in the window; we do not perturb global calendar state.
//  - Overlapping tasks (fetch/fill/return) make chest/trough/carry oscillate, so we accumulate
//    order-independent invariants over the whole settle window, never an end-of-run snapshot.
//  - A client parked on the arena (golden-suite.sh) keeps the chunks loaded, so BE reads are reliable.
//    An earlier headless harness needed per-read readability guards and spawn-chunk anchoring; the client
//    route made both unnecessary.
public class ShepherdFeedHaulScenario : IGoldenScenario
{
    public string Name => "shepherd-feed-haul";
    public string Justification =>
        "Runtime-only haul loop (AI tick + pathfinder + live chest and trough); no unit test can cover it. "
        + "Proves haul-don't-conjure, the NeedsFeed gate, and that troughs/non-feed are rejected as sources. "
        + "Flat floor + window-accumulated invariants keep it durable.";
    public int SettleSeconds => 70;   // upper bound; early exit below once positives land + negatives got a fair window

    // Early exit, guarded for the NEGATIVE checks: the positives + liveness flipping true says the loop
    // worked, but "control/decoy never touched" needs a real observation window to mean anything — so
    // never settle before MinWindowSeconds regardless of how fast the haul completed.
    private const int MinWindowSeconds = 45;
    private int sampleCount;
    public bool IsSettled =>
        sampleCount >= MinWindowSeconds
        && seededOk && sawTroughFilled && sawChestDrained && sawShepherdCarry;

    private const int ChestFlax = 16;       // feed stock: one full 8-capacity trough + surplus
    private const int DecoyItems = 16;      // cobblestone in the non-feed decoy chest
    private const int FullTroughFlax = 8;   // small-trough grain capacity = quantityPerFillLevel 1 * maxFillLevels 8
    private const int VillageRadius = 12;
    private const string Feed = "grain-flax";
    private const string DecoyBlock = "cobblestone-granite";

    private ICoreServerAPI api;
    private Village village;
    private readonly List<long> shepherdIds = new List<long>();
    private long chickenId = -1;      // consumer by the empty trough (feed feature refuses a trough with no animal)
    private BlockPos feedChest, decoyChest, emptyTrough, fullTrough, center;

    // Accumulated over the settle window (sampled each second), so oscillation can't hide a result.
    private bool sawTroughFilled, sawChestDrained, sawShepherdCarry;
    private bool controlEverChanged, decoyEverDrained, sawNonFeedCarry;
    private bool seededOk;                               // scenario precondition captured at Setup
    private long sampleTickId = -1;

    public void Setup(ICoreServerAPI sapi)
    {
        api = sapi;
        VillageManager vm = api.ModLoader.GetModSystem<VillageManager>();
        BlockPos spawn = api.World.DefaultSpawnPosition.AsBlockPos;

        int y = TestScene.BuildFlatArea(api, spawn, 16, 16);
        center = new BlockPos(spawn.X, y, spawn.Z);

        village = new Village { Pos = center.Copy(), Radius = VillageRadius, Name = "golden-shepherd-feed-haul" };
        village.Init(api);
        vm.Villages.TryAdd(village.Id, village);

        Block chest = api.World.GetBlock(new AssetLocation("game:chest-east"));
        Block troughBlock = api.World.GetBlock(new AssetLocation("game:trough-genericwood-small-ns"));
        Block wall = api.World.GetBlock(new AssetLocation("game:" + DecoyBlock));
        Item grain = api.World.GetItem(new AssetLocation("game:" + Feed));

        // Decoys are NEARER than the feed chest, so proximity alone would pick the wrong thing. The empty
        // trough is collinear 3 past the feed chest (short, clear fill leg — the proven geometry). The
        // shepherd starts at center; the work lane runs along the +x axis, decoys sit off-lane.
        fullTrough  = ScenarioKit.PlaceContainer(api, At(0, 2), troughBlock.Id, new ItemStack(grain, FullTroughFlax)); // full control trough, NEAREST (dist 2), off-lane
        decoyChest  = ScenarioKit.PlaceContainer(api, At(2, 2), chest.Id, new ItemStack(wall, DecoyItems));    // non-feed decoy chest (dist ~2.8 < 3), off-lane
        feedChest   = ScenarioKit.PlaceContainer(api, At(3, 0), chest.Id, new ItemStack(grain, ChestFlax));    // real source (dist 3)
        emptyTrough = ScenarioKit.PlaceContainer(api, At(6, 0), troughBlock.Id, null);                         // needy target, FARTHEST (dist 6)
        ScenarioKit.WallRing(api, decoyChest, wall.BlockId, 2);   // wrap the decoy: a naive shepherd that tries for it gets walled out

        // A penned hen gives the empty trough a real consumer — the feed feature now refuses to fill a
        // trough with no animal nearby (no consumer = no need). Small trough + grain-flax = chicken feed.
        // Fenced in place so it stays put once a client revives it (see ScenarioKit.PenAnimal).
        chickenId = ScenarioKit.PenAnimal(api, "game:chicken-hen", At(6, 1));

        // Register only the feed chest by hand; the scan enrols every in-radius storage container. It
        // must NOT enrol the troughs (source fix) — if it does, the shepherd robs the full control trough.
        village.RegisterContainer(feedChest);
        village.ScanContainers();

        // Seed-sanity: a mis-seed (wrong feed code, trough won't accept it) makes the whole loop a silent
        // no-op with every check red and no diagnosis. Capture the precondition so it fails loudly instead.
        if (api.World.BlockAccessor.GetBlockEntity(emptyTrough) is BlockEntityTrough et)
            seededOk = ShepherdTroughs.NeedsFeed(et) && ShepherdTroughs.AcceptsItem(et, new ItemStack(grain, 1));

        EntityProperties etype = api.World.GetEntityType(new AssetLocation("vsvillage:villager-female-shepherd"));
        shepherdIds.Add(ScenarioKit.SpawnVillager(api, etype, new BlockPos(center.X, y, center.Z), village));

        sampleTickId = api.Event.RegisterGameTickListener(_ => Sample(), 1000);

        Note("━━━━━ GOLDEN TEST: shepherd-feed-haul ━━━━━");
        Note("Arena: flat platform. NEAREST to the shepherd sit two decoys — a FULL trough and a walled non-feed (cobblestone) chest.");
        Note("The real feed chest and the EMPTY needy trough are farther up the lane.");
        Note("WATCH: the shepherd should ignore both nearby decoys, haul grain from the farther feed chest into the empty trough. Auto-teardown in " + SettleSeconds + "s.");
    }

    // One observation of the world state, folded into the accumulators. Each positive/negative accumulator
    // only ever flips false->true, so a single true reading anywhere in the window is enough.
    private void Sample()
    {
        sampleCount++;
        // Positives — the loop actually ran.
        if (!sawTroughFilled && FlaxIn(emptyTrough) > 0) { sawTroughFilled = true; Note("✅ Empty pen trough now holds grain — hauled in."); }
        if (!sawChestDrained && FlaxIn(feedChest) < ChestFlax) { sawChestDrained = true; Note("✅ Feed chest drained — grain was HAULED, not conjured."); }
        if (!sawShepherdCarry && shepherdIds.Any(id => CarryPath(id) == Feed)) { sawShepherdCarry = true; Note("🌾 Shepherd carrying grain — fetch-into-carry works."); }

        // Negatives.
        if (!controlEverChanged && FlaxIn(fullTrough) != FullTroughFlax) { controlEverChanged = true; Note("❌ Full control trough changed — NeedsFeed gate LEAKED."); }
        if (!decoyEverDrained && TotalItemsIn(decoyChest) < DecoyItems) { decoyEverDrained = true; Note("❌ Non-feed decoy chest drained — the shepherd fetched junk."); }
        if (!sawNonFeedCarry && shepherdIds.Any(id => { string p = CarryPath(id); return p != null && p != Feed; })) { sawNonFeedCarry = true; Note("❌ Shepherd carried a non-feed item."); }
    }

    public void Assert(ScenarioReport report)
    {
        report.Check("scenario seeded: empty trough is needy and accepts the feed", seededOk);
        // Positives
        report.Check("empty pen trough was filled at least once", sawTroughFilled);
        report.Check("feed chest was drained at least once (hauled, not conjured)", sawChestDrained);
        report.Check("a shepherd carried feed at some point", sawShepherdCarry);
        // Negatives
        report.Check("full control trough was never touched (NeedsFeed gate)", !controlEverChanged);
        report.Check("non-feed decoy chest was never drained (type gate)", !decoyEverDrained);
        report.Check("a shepherd never carried a non-feed item", !sawNonFeedCarry);

        Note("━━━━━ RESULT ━━━━━");
        Note((sawTroughFilled ? "✅" : "❌") + " trough filled   " + (sawChestDrained ? "✅" : "❌") + " chest drained   " + (sawShepherdCarry ? "✅" : "❌") + " carried feed");
        Note((!controlEverChanged ? "✅" : "❌") + " control untouched   " + (!decoyEverDrained ? "✅" : "❌") + " decoy untouched   " + (!sawNonFeedCarry ? "✅" : "❌") + " no junk carried");
        Note(report.Passed ? "PASS — every invariant held." : "FAIL — see the ❌ above.");
    }

    public void Teardown()
    {
        if (sampleTickId >= 0) { api.Event.UnregisterGameTickListener(sampleTickId); sampleTickId = -1; }
        // Release claims before despawning so none bleeds into the next scenario in one boot.
        ScenarioKit.ReleaseClaims(shepherdIds, new[] { feedChest, decoyChest, emptyTrough, fullTrough });
        foreach (long id in shepherdIds)
            api.World.GetEntityById(id)?.Die(EnumDespawnReason.Removed);
        shepherdIds.Clear();
        if (chickenId >= 0) { api.World.GetEntityById(chickenId)?.Die(EnumDespawnReason.Removed); chickenId = -1; }
        foreach (BlockPos p in new[] { feedChest, decoyChest, emptyTrough, fullTrough }.Where(p => p != null))
        {
            if (api.World.BlockAccessor.GetBlockEntity(p) is BlockEntityContainer be && be.Inventory != null)
                be.Inventory.Clear();
            api.World.BlockAccessor.SetBlock(0, p);
        }
        if (decoyChest != null) ScenarioKit.WallRing(api, decoyChest, 0, 2);
        if (village != null)
            api.ModLoader.GetModSystem<VillageManager>()?.Villages.TryRemove(village.Id, out _);
        Note("🧹 Scene torn down — shepherd despawned, blocks removed, village unregistered.");
    }

    // Maps an arena offset to a world position.
    private BlockPos At(int dx, int dz) => new BlockPos(center.X + dx, center.Y, center.Z + dz);

    // Watch-mode narration + shared reads: impls live in ScenarioKit; these thin aliases keep the dense
    // Sample()/Assert() lines terse and give the feed-count read its local name.
    private void Note(string msg) => ScenarioKit.Note(api, msg);
    private string CarryPath(long id) => ScenarioKit.CarryPath(api, id);
    private int FlaxIn(BlockPos pos) => ScenarioKit.ItemCountIn(api, pos, Feed);
    private int TotalItemsIn(BlockPos pos) => ScenarioKit.TotalItemsIn(api, pos);
}
