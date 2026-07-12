using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using VsVillage;

namespace VsVillageTest.Scenarios;

// Behavioral: proves the shepherd feeds EACH pen animal the feed it LIKES BEST among the ones it will eat
// (the game's own per-animal preference weights), cascading to its next-favourite when the best is out of
// stock, and that a housed baker never raids the feed chest.
//
// The chest always holds hay (drygrass) as a tempting but LEAST-liked option the shepherd should skip.
// Three pens, each with two animals and the correct trough for that species:
//   - chicken pen: SMALL trough. Chickens eat grain only → grain.
//   - pig pen:     LARGE trough. Prefers vegetables; never grass/hay.
//   - goat pen:    LARGE trough. Grazes, but weighs vegetables (1.0) far above hay (0.1) → prefers vegetables.
// Priority mode (veg stocked): chicken→grain, pig→veg, goat→veg — and NO animal touches the hay.
// Cascade mode (veg removed): pig & goat fall to grain (0.5), still NOT hay (0.1) — proving it's the weight
// order, not mere edibility, that drives the choice.
//
// Justification: the whole loop is runtime-only (AI tick + pathfinder + live chest/troughs + live animals'
// CreatureDiet); no unit test can exercise decision→path→withdraw→deposit against real diets. The pure
// preference/pick-best is unit-tested (AnimalFeedPriorityTests); this proves the integrated, animal-aware
// behavior and the baker exclusion. Animals do NOT eat on a playerless server, so we assert what the shepherd FILLS.
//
// Durability: flat floor; every read-critical BE (chest, all troughs incl. the large trough's head) inside
// spawn's own chunk; window-accumulated order-independent invariants; a full PORTION asserted (a sub-portion
// is inedible); every negative paired with a liveness flag; animals are stationary (non-AlwaysActive) diet
// sources so they can't wander out of range.
public class ShepherdFeedPensScenario : IGoldenScenario
{
    public enum Mode { Priority, Cascade, TwoShepherds }

    private readonly Mode mode;
    public ShepherdFeedPensScenario(Mode mode) { this.mode = mode; }

    private bool VegStocked => mode != Mode.Cascade;             // cascade case removes VEG — forcing grazers off their favourite
    private int ShepherdCount => mode == Mode.TwoShepherds ? 2 : 1;

    public string Name => mode switch
    {
        Mode.Priority => "feed-pens-priority",
        Mode.Cascade => "feed-pens-cascade",
        _ => "feed-pens-2shepherds",
    };

    public string Justification =>
        "Runtime-only animal-aware feed loop (AI + pathfinder + live diets/troughs); no unit test can cover "
        + "decision→path→withdraw→deposit per species. Proves priority (goat→hay), per-animal edibility "
        + "(pig→veg, chicken→grain), availability cascade (goat→veg when hay absent), and baker exclusion.";

    public int SettleSeconds => 90;

    // Feeds and their per-portion sizes in the relevant trough (from the game content configs).
    private const string Hay = "drygrass";                 // grass — goats/sheep only; large trough, 8 / portion
    private const string Veg = "vegetable-carrot";         // pig/goat/sheep; large trough, 2 / portion
    private const string Grain = "grain-flax";             // all incl. chicken; small trough, 1 / portion
    private const int VegPortion = 2, GrainPortion = 1, MammalPortion = 2;   // large-trough veg & grain are 2/portion; the chicken's small-trough grain is 1
    private const int Stock = 64;                           // ≥ one portion of each so gate-4 (portion) is satisfiable
    private const int VillageRadius = 14;
    private const float PenRadius = 8f;

    private ICoreServerAPI api;
    private Village village;
    private BlockPos center, feedChest;
    private BlockPos chickenTrough, pigTrough, goatTrough;   // pig/goat = large-trough HEAD (carries the BE/POI)
    private int dirX = 1, dirZ = 1;

    private readonly List<long> shepherdIds = new List<long>();
    private readonly List<long> animalIds = new List<long>();
    private long bakerId = -1;

    private int sampleCount;
    // Positive fills (content == expected feed AND ≥ one portion), accumulated across the window.
    private bool chickenFedGrain, pigFedExpected, goatFedExpected;
    // Negatives: a trough ever held a feed its animal refuses OR (for grazers) the least-liked hay.
    private bool pigGotHay, goatGotHay, chickenGotVegOrHay;
    // Liveness so negatives aren't vacuous.
    private bool chickenReadable, pigReadable, goatReadable, bakerLive;
    // Baker must never carry feed (the raid signal — chest census is unreliable headless).
    private bool bakerCarriedFeed;
    // 2-shepherd conflict resolution: both must work AND at some instant carry DIFFERENT feeds (proving
    // they provision different pens concurrently, not both piling onto the nearest one).
    private readonly HashSet<long> shepherdsThatCarried = new HashSet<long>();
    private bool sawConcurrentDifferentFeeds;
    private long sampleTickId = -1;

    // Grazers (pig/goat) most-like vegetables; with veg absent they fall to grain (0.5) — never the hay (0.1).
    private string ExpectedMammalFeed => VegStocked ? Veg : Grain;
    private string MammalFeedLabel => VegStocked ? "vegetables (its favourite)" : "grain (cascade — veg gone, still not hay)";

    public bool IsSettled =>
        sampleCount >= 50
        && chickenFedGrain && pigFedExpected && goatFedExpected
        && chickenReadable && pigReadable && goatReadable && bakerLive
        && (mode != Mode.TwoShepherds || (shepherdsThatCarried.Count >= 2 && sawConcurrentDifferentFeeds));

    public void Setup(ICoreServerAPI sapi)
    {
        api = sapi;
        VillageManager vm = api.ModLoader.GetModSystem<VillageManager>();
        BlockPos spawn = api.World.DefaultSpawnPosition.AsBlockPos;

        int y = TestScene.BuildFlatArea(api, spawn, 14, 16);
        center = new BlockPos(spawn.X, y, spawn.Z);
        dirX = (spawn.X - ((spawn.X >> 5) << 5) <= 15) ? 1 : -1;
        dirZ = (spawn.Z - ((spawn.Z >> 5) << 5) <= 15) ? 1 : -1;

        village = new Village { Pos = At(4, 4), Radius = VillageRadius, Name = "golden-" + Name };
        village.Init(api);
        vm.Villages.TryAdd(village.Id, village);

        // Feed chest at the village centre. Hay only stocked in the priority/2-shepherd modes.
        Block chest = api.World.GetBlock(new AssetLocation("game:chest-east"));
        feedChest = At(4, 5);
        api.World.BlockAccessor.SetBlock(chest.Id, feedChest);
        if (api.World.BlockAccessor.GetBlockEntity(feedChest) is BlockEntityContainer be && be.Inventory != null)
        {
            int slot = 0;
            SeedSlot(be, slot++, Hay, Stock);              // always stocked — the tempting LEAST-liked feed the shepherd should skip
            if (VegStocked) SeedSlot(be, slot++, Veg, Stock);
            SeedSlot(be, slot++, Grain, Stock);
            be.MarkDirty(true);
        }
        village.RegisterContainer(feedChest);
        village.ScanContainers();

        // Three pens spread along +x (troughs 4 apart), animals adjacent to their OWN trough. Small trough
        // for chickens; large (2-block) troughs for pig & goat. Everything stays within ~12 of centre — inside
        // spawn's own chunk for every seed offset (the anchoring guarantees ≥16 blocks of room). Adjacency
        // gives each animal an unambiguous nearest trough, so the served-animal (mutual-nearest) is clean.
        chickenTrough = PlaceSmallTrough(2, 10);
        SpawnAnimals("game:chicken-hen", chickenTrough, 2);

        pigTrough = PlaceLargeTrough(6, 10);
        SpawnAnimals("game:pig-eurasian-adult-female", pigTrough, 2);

        goatTrough = PlaceLargeTrough(10, 10);
        SpawnAnimals("game:goat-nubian-adult-female", goatTrough, 2);

        // Shepherd(s) at the chest.
        for (int i = 0; i < ShepherdCount; i++)
            shepherdIds.Add(SpawnVillager("vsvillage:villager-female-shepherd", At(3 + i, 4), assignVillage: true));

        // A HOUSED baker beside the chest — could raid it if the exclusion failed. Village-assigned so the
        // control is non-vacuous (an unhoused baker never fetches for the wrong reason).
        bakerId = SpawnVillager("vsvillage:villager-female-baker", At(5, 4), assignVillage: true);

        sampleTickId = api.Event.RegisterGameTickListener(_ => Sample(), 1000);

        api.Logger.Notification("[feed-diag] {0}: chest={1} chickenTrough={2} pigTrough={3} goatTrough={4} veg={5} shepherds={6}",
            Name, feedChest, chickenTrough, pigTrough, goatTrough, VegStocked, ShepherdCount);
        api.Logger.Notification("[feed-diag] trough readable at setup: chicken={0} pig={1} goat={2}",
            IsReadable(chickenTrough), IsReadable(pigTrough), IsReadable(goatTrough));
    }

    private void Sample()
    {
        sampleCount++;

        // Chicken pen — grain only.
        if (IsReadable(chickenTrough)) chickenReadable = true;
        string ct = TroughContent(chickenTrough);
        if (ct == Grain && TroughStack(chickenTrough) >= GrainPortion) chickenFedGrain = true;
        if (ct == Veg || ct == Hay) chickenGotVegOrHay = true;

        // Pig pen — its favourite available (veg, else grain); NEVER hay (pigs can't eat grass).
        if (IsReadable(pigTrough)) pigReadable = true;
        string pt = TroughContent(pigTrough);
        if (pt == ExpectedMammalFeed && TroughStack(pigTrough) >= MammalPortion) pigFedExpected = true;
        if (pt == Hay) pigGotHay = true;

        // Goat pen — its favourite available (veg, else grain); NEVER hay (grazes, but likes hay LEAST).
        if (IsReadable(goatTrough)) goatReadable = true;
        string gt = TroughContent(goatTrough);
        if (gt == ExpectedMammalFeed && TroughStack(goatTrough) >= MammalPortion) goatFedExpected = true;
        if (gt == Hay) goatGotHay = true;

        // Baker must never carry feed.
        Entity baker = api.World.GetEntityById(bakerId);
        if (baker != null && baker.Alive) bakerLive = true;
        string bakerCarry = CarryPath(bakerId);
        if (bakerCarry == Hay || bakerCarry == Veg || bakerCarry == Grain) bakerCarriedFeed = true;

        // Which shepherds actually carried feed (2-shepherd work split).
        foreach (long id in shepherdIds)
        {
            string c = CarryPath(id);
            if (c == Hay || c == Veg || c == Grain) shepherdsThatCarried.Add(id);
        }
        // Two shepherds carrying DIFFERENT feeds at the same instant → concurrently provisioning different pens.
        if (ShepherdCount >= 2
            && shepherdIds.Select(CarryPath).Where(c => c == Hay || c == Veg || c == Grain).Distinct().Count() >= 2)
            sawConcurrentDifferentFeeds = true;
    }

    public void Assert(ScenarioReport report)
    {
        report.Check("chicken trough was filled with grain (≥1 portion)", chickenFedGrain);
        report.Check("pig trough was filled with " + MammalFeedLabel + " (≥1 portion)", pigFedExpected);
        report.Check("goat trough was filled with " + MammalFeedLabel + " (≥1 portion)", goatFedExpected);

        report.Check("chicken trough was observed readable (negatives non-vacuous)", chickenReadable);
        report.Check("pig trough was observed readable (negatives non-vacuous)", pigReadable);
        report.Check("goat trough was observed readable", goatReadable);

        report.Check("pig trough never received hay (pigs don't graze)", !pigGotHay);
        report.Check("goat trough never received hay (goats like hay LEAST — they prefer " + (VegStocked ? "veg" : "grain") + ")", !goatGotHay);
        report.Check("chicken trough never received veg or hay (chickens are grain-only)", !chickenGotVegOrHay);

        report.Check("a baker was observed live beside the chest (control non-vacuous)", bakerLive);
        report.Check("the baker never carried feed (did not raid the chest)", !bakerCarriedFeed);

        if (mode == Mode.TwoShepherds)
        {
            report.Check("both shepherds carried feed (both working)", shepherdsThatCarried.Count >= 2);
            report.Check("two shepherds carried different feeds at once (provisioning different pens concurrently)", sawConcurrentDifferentFeeds);
        }
    }

    public void Teardown()
    {
        if (sampleTickId >= 0) { api.Event.UnregisterGameTickListener(sampleTickId); sampleTickId = -1; }
        // Release any trough/chest claims our shepherds still hold BEFORE despawning them — the claim
        // registries are process-static and the next scenario reuses these exact positions within the
        // same boot, so a claim orphaned by a despawned shepherd would block it for up to the 120s expiry.
        foreach (long sid in shepherdIds)
        {
            foreach (BlockPos tp in new[] { chickenTrough, pigTrough, goatTrough }.Where(p => p != null))
                VsVillage.VsVillage.TroughClaims.Release(tp, sid);   // namespace.class.field (class shares the namespace name)
            if (feedChest != null) VsVillage.VsVillage.ContainerClaims.Release(feedChest, sid);
        }
        foreach (long id in shepherdIds.Concat(animalIds).Append(bakerId))
            api.World.GetEntityById(id)?.Die(EnumDespawnReason.Removed);
        shepherdIds.Clear();
        animalIds.Clear();
        bakerId = -1;
        foreach (BlockPos p in new[] { feedChest, chickenTrough, pigTrough, pigTrough?.AddCopy(1, 0, 0),
                                       goatTrough, goatTrough?.AddCopy(1, 0, 0) }.Where(p => p != null))
        {
            if (api.World.BlockAccessor.GetBlockEntity(p) is BlockEntityContainer bec && bec.Inventory != null)
                bec.Inventory.Clear();
            api.World.BlockAccessor.SetBlock(0, p);
        }
        if (village != null)
            api.ModLoader.GetModSystem<VillageManager>()?.Villages.TryRemove(village.Id, out _);
    }

    private BlockPos At(int dx, int dz) => new BlockPos(center.X + dirX * dx, center.Y, center.Z + dirZ * dz);

    private void SeedSlot(BlockEntityContainer be, int index, string code, int count)
    {
        if (index >= be.Inventory.Count) return;
        Item it = api.World.GetItem(new AssetLocation("game:" + code));
        if (it == null) { api.Logger.Warning("[feed-diag] unknown feed item {0}", code); return; }
        be.Inventory[index].Itemstack = new ItemStack(it, count);
        be.Inventory[index].MarkDirty();
    }

    private BlockPos PlaceSmallTrough(int dx, int dz)
    {
        BlockPos tp = At(dx, dz);
        api.World.BlockAccessor.SetBlock(api.World.GetBlock(new AssetLocation("game:trough-genericwood-small-ns")).BlockId, tp);
        return tp;
    }

    // Large (2-block) trough: head at At(dx,dz), feet one cell EAST (side=east → feet = head + East.Normali).
    // Head carries the block entity + POI. Feet placed first so the head BE initialises with its partner present.
    private BlockPos PlaceLargeTrough(int dx, int dz)
    {
        BlockPos head = At(dx, dz);
        BlockPos feet = head.AddCopy(1, 0, 0);
        api.World.BlockAccessor.SetBlock(api.World.GetBlock(new AssetLocation("game:trough-genericwood-large-feet-east")).BlockId, feet);
        api.World.BlockAccessor.SetBlock(api.World.GetBlock(new AssetLocation("game:trough-genericwood-large-head-east")).BlockId, head);
        return head;
    }

    // Spawn `count` stationary animals directly north of the trough (adjacent, so each animal's nearest
    // trough is unambiguously its own; within PenRadius; off the shepherd's south/west approach). Non-
    // AlwaysActive so they stay put and read as diet sources.
    private void SpawnAnimals(string code, BlockPos trough, int count)
    {
        for (int i = 0; i < count; i++)
        {
            BlockPos p = new BlockPos(trough.X, trough.Y, trough.Z + dirZ * (1 + i));   // 1,2 north (adjacent)
            long id = TestScene.SpawnStationaryAnimal(api, code, p);
            if (id >= 0) animalIds.Add(id);
        }
    }

    private long SpawnVillager(string code, BlockPos vp, bool assignVillage)
    {
        EntityProperties etype = api.World.GetEntityType(new AssetLocation(code));
        Entity e = api.World.ClassRegistry.CreateEntity(etype);
        e.Pos.SetPos(vp.X + 0.5, vp.Y, vp.Z + 0.5);
        e.ServerPos.SetPos(vp.X + 0.5, vp.Y, vp.Z + 0.5);
        e.AlwaysActive = true;
        api.World.SpawnEntity(e);
        if (assignVillage) e.GetBehavior<EntityBehaviorVillager>().Village = village;
        return e.EntityId;
    }

    private string CarryPath(long id)
        => api.World.GetEntityById(id)?.GetBehavior<EntityBehaviorVillager>()?.CarrySlot?.Collectible?.Code?.Path;

    private bool IsReadable(BlockPos pos)
        => api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityContainer be && be.Inventory != null;

    private string TroughContent(BlockPos pos)
    {
        if (api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityContainer be && be.Inventory != null
            && be.Inventory.Count > 0 && !be.Inventory[0].Empty)
            return be.Inventory[0].Itemstack.Collectible?.Code?.Path;
        return null;
    }

    private int TroughStack(BlockPos pos)
    {
        if (api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityContainer be && be.Inventory != null
            && be.Inventory.Count > 0 && !be.Inventory[0].Empty)
            return be.Inventory[0].StackSize;
        return 0;
    }
}
