using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

/// <summary>
/// World-touching half of the shepherd's animal-aware feeding: find the animal a trough serves, and score
/// each candidate feed by how much that animal LIKES it (or reject it). Pairs with the pure
/// <see cref="AnimalFeedPriority"/> (preference weight + pick-best). Not unit-testable — it needs the world,
/// live entities, and block entities — so it is verified by the behavioral golden scenarios.
///
/// A feed is a candidate only if it passes all four game gates:
///   1. physical: the trough accepts the item (<see cref="ShepherdTroughs.AcceptsItem"/>);
///   2. block-suitability: the animal isn't in the trough block's <c>unsuitableFor</c> list;
///   3. diet: the animal's diet accepts it (skip-tags reject parsnip/rice first, else category/tag);
///   4. portion: at least one full <c>QuantityPerFillLevel</c> is available (a sub-portion is inedible; drygrass needs 8).
/// Among the survivors the shepherd takes the one with the highest per-animal preference weight.
/// </summary>
public static class ShepherdFeeding
{
    // Same livestock intent as ShepherdTend. FirstCodePart match so -baby/-male/breed variants qualify.
    private static readonly HashSet<string> LivestockPrefixes = new HashSet<string>
    {
        "sheep", "ram", "ewe", "lamb", "cow", "bull", "calf",
        "chicken", "hen", "rooster", "chick", "pig", "sow", "piglet",
        "goat", "hare", "alpaca", "llama"
    };

    public static bool IsLivestock(Entity e)
    {
        string fp = e?.Code?.FirstCodePart();
        return fp != null && LivestockPrefixes.Contains(fp);
    }

    /// <summary>The static facts the fetch/fill legs need about a trough's consumer, captured once (its diet
    /// deconstructed into plain fields the pure selector consumes) so we never hold a moving entity
    /// reference. Null when there is no suitable consumer (→ don't feed).</summary>
    public sealed class ServedAnimal
    {
        public string CodePath;
        public EnumFoodCategory[] Categories;
        public (string code, float weight)[] WeightedTags;
        public string[] SkipTags;
    }

    // Diet is a property of the entity TYPE (its JSON attributes), identical for every animal of a code,
    // so cache by full code to avoid re-deserialising on every trough/animal probe. Server-tick-thread
    // only, like the claim registries — a plain dictionary is safe.
    private static readonly Dictionary<string, CreatureDiet> DietCache = new Dictionary<string, CreatureDiet>();

    /// <summary>The LIVING livestock a trough serves: the nearest suitable-for-this-trough animal for which
    /// THIS trough is that animal's OWN nearest suitable trough. The mutual-nearest requirement stops a
    /// neighbouring pen's animal from being mistaken for the consumer — critical because pigs and goats/sheep
    /// all share the large trough, so plain "nearest" could hand a pig trough a goat.
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

    /// <summary>Resolve the trough's consumer to the static (code + deconstructed diet) the selector needs.
    /// Null when no suitable animal is nearby OR its diet is missing (null diet → don't feed, never "any feed").</summary>
    public static ServedAnimal FindServed(IWorldAccessor world, POIRegistry poiReg, BlockEntityTrough trough, float radius)
    {
        Entity a = FindServedAnimal(world, poiReg, trough, radius);
        if (a?.Code == null) return null;
        CreatureDiet d = GetDiet(a);
        if (d == null) return null;
        return new ServedAnimal
        {
            CodePath = a.Code.Path,
            Categories = d.FoodCategories ?? System.Array.Empty<EnumFoodCategory>(),
            WeightedTags = ToTuples(d.WeightedFoodTags),
            SkipTags = d.SkipFoodTags ?? System.Array.Empty<string>()
        };
    }

    private static (string code, float weight)[] ToTuples(WeightedFoodTag[] tags)
    {
        if (tags == null) return System.Array.Empty<(string, float)>();
        var result = new (string, float)[tags.Length];
        for (int i = 0; i < tags.Length; i++) result[i] = (tags[i].Code, tags[i].Weight);
        return result;
    }

    /// <summary>Will this trough's served animal EAT this feed? Gate 2 (block-suitability) + gate 3 (diet
    /// accepts). Used by the fill leg to refuse depositing a feed the pen's animal won't touch.</summary>
    public static bool WillEat(IWorldAccessor world, BlockEntityTrough trough, ServedAnimal served, ItemStack feed)
    {
        if (trough?.Block is not BlockTroughBase btb || served == null || feed == null) return false;
        if (btb.UnsuitableForEntity(served.CodePath)) return false;                     // gate 2
        (EnumFoodCategory cat, string[] tags) = GetFeedTags(world, feed);
        return AnimalFeedPriority.PreferenceWeight(served.Categories, served.WeightedTags, served.SkipTags,
                   cat, tags, AnimalFeedPriority.CategoryMatchWeight) != null;           // gate 3
    }

    /// <summary>The served animal's preference weight for a full portion of this slot's feed, or null if it
    /// fails any gate (physical accept, block-suitable, ≥ one portion) or the animal refuses it. Higher = liked more.</summary>
    public static float? FeedPreference(IWorldAccessor world, BlockEntityTrough trough, ServedAnimal served, ItemSlot slot)
    {
        if (world == null || trough?.Block is not BlockTroughBase btb || served == null || slot == null || slot.Empty) return null;
        ItemStack stack = slot.Itemstack;
        if (!ShepherdTroughs.AcceptsItem(trough, stack)) return null;                    // gate 1
        if (btb.UnsuitableForEntity(served.CodePath)) return null;                       // gate 2
        ContentConfig cfg = ItemSlotTrough.getContentConfig(world, trough.contentConfigs, slot);
        if (cfg == null || slot.StackSize < cfg.QuantityPerFillLevel) return null;       // gate 4
        (EnumFoodCategory cat, string[] tags) = GetFeedTags(world, stack);
        return AnimalFeedPriority.PreferenceWeight(served.Categories, served.WeightedTags, served.SkipTags,
                   cat, tags, AnimalFeedPriority.CategoryMatchWeight);                    // gate 3 + weight
    }

    // Extract a feed's nutrition category + food tags the same way the game's CreatureDiet.Matches(ItemStack) does.
    public static (EnumFoodCategory category, string[] tags) GetFeedTags(IWorldAccessor world, ItemStack stack)
    {
        CollectibleObject coll = stack?.Collectible;
        if (coll == null) return (EnumFoodCategory.NoNutrition, null);
        FoodNutritionProperties nutri = coll.GetNutritionProperties(world, stack, null);
        EnumFoodCategory cat = nutri?.FoodCategory ?? EnumFoodCategory.NoNutrition;
        string[] tags = coll.GetCollectibleInterface<ICreatureDietFoodTags>()?.GetFoodTags(stack)
                        ?? coll.Attributes?["foodTags"].AsArray<string>(null, "game");
        return (cat, tags);
    }

    /// <summary>The most-liked appropriate feed slot to withdraw from <paramref name="be"/> for this trough's
    /// animal, or null. Scores each slot with <see cref="FeedPreference"/> and lets the UNIT-TESTED
    /// <see cref="AnimalFeedPriority.ChooseBestFeed"/> pick the highest-weight one. This is the fetch leg's chooser.</summary>
    public static ItemSlot ChooseFeedSlot(IWorldAccessor world, BlockEntityContainer be, BlockEntityTrough trough, ServedAnimal served)
    {
        if (be?.Inventory == null || trough == null || served == null) return null;
        var candidates = new List<FeedCandidate>();
        var likedSlotByCode = new Dictionary<string, ItemSlot>();
        foreach (ItemSlot slot in be.Inventory)
        {
            if (slot.Empty) continue;
            string code = slot.Itemstack.Collectible?.Code?.Path;
            if (code == null) continue;
            float? w = FeedPreference(world, trough, served, slot);
            candidates.Add(new FeedCandidate(code, w));
            if (w != null && !likedSlotByCode.ContainsKey(code)) likedSlotByCode[code] = slot;
        }
        string bestCode = AnimalFeedPriority.ChooseBestFeed(candidates);
        return bestCode != null && likedSlotByCode.TryGetValue(bestCode, out ItemSlot best) ? best : null;
    }
}
