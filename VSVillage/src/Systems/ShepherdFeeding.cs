using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// World-touching half of the shepherd's animal-aware feeding: find the animal a trough serves and
/// decide whether a (not-yet-deposited) feed is APPROPRIATE for it. Pairs with the pure
/// <see cref="AnimalFeedPriority"/> (rank + cascade). Not unit-testable — it needs the world, live
/// entities, and block entities — so it is verified by the behavioral golden scenarios.
///
/// "Appropriate" replicates the game's own eat-check <c>BlockEntityTrough.IsSuitableFor</c> for a feed
/// that isn't in the trough yet, i.e. all four gates:
///   1. physical: the trough accepts the item (<see cref="ShepherdTroughs.AcceptsItem"/>);
///   2. block-suitability: the animal isn't in the trough block's <c>unsuitableFor</c> list;
///   3. diet: the animal's <c>CreatureDiet.Matches(feed)</c> (respects skipFoodTags — parsnip/rice);
///   4. portion: at least one full <c>QuantityPerFillLevel</c> is available to deposit (a sub-portion
///      is inedible — the game's <c>StackSize &gt;= QuantityPerFillLevel</c> gate; drygrass needs 8).
/// </summary>
public static class ShepherdFeeding
{
    // Same livestock intent as ShepherdTend. FirstCodePart match so -baby/-male/breed variants qualify.
    private static readonly HashSet<string> LivestockPrefixes = new HashSet<string>
    {
        "sheep", "ram", "ewe", "lamb", "cow", "bull", "calf",
        "chicken", "hen", "rooster", "chick", "pig", "sow", "piglet",
        "goat", "alpaca", "llama"
    };

    public static bool IsLivestock(Entity e)
    {
        string fp = e?.Code?.FirstCodePart();
        return fp != null && LivestockPrefixes.Contains(fp);
    }

    /// <summary>The static facts the fetch/fill legs need about a trough's consumer, captured once so we
    /// never hold a moving entity reference. Null when there is no suitable consumer (→ don't feed).</summary>
    public sealed class ServedAnimal
    {
        public string CodePath;
        public CreatureDiet Diet;
    }

    // Diet is a property of the entity TYPE (its JSON attributes), identical for every animal of a code,
    // so cache by full code to avoid re-deserialising on every trough/animal probe. Server-tick-thread
    // only, like the claim registries — a plain dictionary is safe.
    private static readonly Dictionary<string, CreatureDiet> DietCache = new Dictionary<string, CreatureDiet>();

    /// <summary>The LIVING livestock a trough serves: the nearest suitable-for-this-trough animal for which
    /// THIS trough is that animal's OWN nearest suitable trough. The mutual-nearest requirement stops a
    /// neighbouring pen's animal from being mistaken for the consumer — critical because pigs and goats/sheep
    /// all share the large trough, so plain "nearest" could hand a pig trough a goat (→ hay → the pig starves).
    /// A wrong-species bystander (a chicken by a large trough) is already excluded by the block's unsuitableFor.</summary>
    public static Entity FindServedAnimal(IWorldAccessor world, POIRegistry poiReg, BlockEntityTrough trough, float radius)
    {
        if (world == null || trough == null || trough.Block is not BlockTroughBase btb) return null;
        Vec3d c = trough.Pos.ToVec3d().Add(0.5, 0.5, 0.5);
        Entity[] cands = world.GetEntitiesAround(c, radius, radius,
            e => e != null && e.Alive && IsLivestock(e) && e.Code != null && !btb.UnsuitableForEntity(e.Code.Path));
        foreach (Entity e in cands.OrderBy(e => e.Pos.XYZ.SquareDistanceTo(c)))
            if (poiReg == null || IsNearestSuitableTrough(poiReg, e, trough, radius))
                return e;
        return null;
    }

    // True when `trough` is `animal`'s own nearest suitable trough (nothing suitable is closer to it).
    private static bool IsNearestSuitableTrough(POIRegistry poiReg, Entity animal, BlockEntityTrough trough, float radius)
    {
        string code = animal.Code?.Path;
        if (code == null) return false;
        BlockEntityTrough nearest = poiReg.GetNearestPoi(animal.Pos.XYZ, radius,
            poi => poi is BlockEntityTrough t && t.Block is BlockTroughBase b && !b.UnsuitableForEntity(code)) as BlockEntityTrough;
        return nearest == null || nearest.Pos.Equals(trough.Pos);
    }

    public static CreatureDiet GetDiet(Entity animal)
    {
        string code = animal?.Code?.ToString();
        if (code == null) return null;
        if (DietCache.TryGetValue(code, out CreatureDiet cached)) return cached;
        CreatureDiet d = animal.Properties?.Attributes?["creatureDiet"]?.AsObject<CreatureDiet>(null);
        DietCache[code] = d;   // cache even null (this entity type simply has no diet)
        return d;
    }

    /// <summary>Resolve the trough's consumer to the static (code, diet) the selection needs. Null when
    /// no suitable animal is nearby OR its diet is missing (M4: null diet → don't feed, never "any feed").</summary>
    public static ServedAnimal FindServed(IWorldAccessor world, POIRegistry poiReg, BlockEntityTrough trough, float radius)
    {
        Entity a = FindServedAnimal(world, poiReg, trough, radius);
        if (a?.Code == null) return null;
        CreatureDiet d = GetDiet(a);
        if (d == null) return null;
        return new ServedAnimal { CodePath = a.Code.Path, Diet = d };
    }

    /// <summary>Gates 2+3: the animal is allowed on this trough block AND its diet eats this feed.
    /// (Gate 1 physical acceptance and gate 4 portion-availability are applied by the callers.)</summary>
    public static bool WillEat(BlockEntityTrough trough, string animalCodePath, CreatureDiet diet, ItemStack feed)
    {
        if (trough?.Block is not BlockTroughBase btb || animalCodePath == null || diet == null || feed == null) return false;
        if (btb.UnsuitableForEntity(animalCodePath)) return false;
        return diet.Matches(feed);
    }

    /// <summary>Does a full portion of <paramref name="feed"/> fit-and-feed this trough's served animal?
    /// All four gates, for one candidate stack in a source container.</summary>
    public static bool IsAppropriateFeed(IWorldAccessor world, BlockEntityTrough trough, ServedAnimal served, ItemSlot slot)
    {
        if (world == null || trough == null || served == null || slot == null || slot.Empty) return false;
        ItemStack stack = slot.Itemstack;
        if (!ShepherdTroughs.AcceptsItem(trough, stack)) return false;                 // gate 1
        if (!WillEat(trough, served.CodePath, served.Diet, stack)) return false;       // gates 2+3
        ContentConfig cfg = ItemSlotTrough.getContentConfig(world, trough.contentConfigs, slot);
        if (cfg == null) return false;
        return slot.StackSize >= cfg.QuantityPerFillLevel;                             // gate 4: ≥ one portion
    }

    /// <summary>The best-priority appropriate feed slot to withdraw from <paramref name="be"/> for this
    /// trough's animal, or null. Applies all four gates per slot, then <see cref="AnimalFeedPriority"/>'s
    /// rank to cascade (hay → veg → grain …). This is the fetch leg's slot chooser.</summary>
    public static ItemSlot ChooseFeedSlot(IWorldAccessor world, BlockEntityContainer be, BlockEntityTrough trough, ServedAnimal served)
    {
        if (be?.Inventory == null || trough == null || served == null) return null;
        // Classify each source slot (code + whether it's appropriate) and let the UNIT-TESTED pure cascade
        // pick the winning code, so production runs the same rank/cascade logic AnimalFeedPriorityTests cover.
        var candidates = new List<FeedCandidate>();
        var firstEdibleSlotByCode = new Dictionary<string, ItemSlot>();
        foreach (ItemSlot slot in be.Inventory)
        {
            if (slot.Empty) continue;
            string code = slot.Itemstack.Collectible?.Code?.Path;
            if (code == null) continue;
            bool edible = IsAppropriateFeed(world, trough, served, slot);
            candidates.Add(new FeedCandidate(code, edible));
            if (edible && !firstEdibleSlotByCode.ContainsKey(code)) firstEdibleSlotByCode[code] = slot;
        }
        string bestCode = AnimalFeedPriority.ChooseBestFeed(candidates);
        return bestCode != null && firstEdibleSlotByCode.TryGetValue(bestCode, out ItemSlot best) ? best : null;
    }
}
