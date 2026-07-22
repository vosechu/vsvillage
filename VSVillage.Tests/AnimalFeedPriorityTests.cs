using System.Collections.Generic;
using Vintagestory.API.Common;
using Xunit;

namespace VsVillage.Tests;

// The tags × animal matrix: each domesticatable animal's real CreatureDiet (verified from the game assets)
// against every troughable feed family, asserting the shepherd's preference weight — or refusal. This is
// the ground truth the shepherd's "feed what it likes best" selection is built on. Expected = -1 means the
// animal REFUSES the feed (PreferenceWeight returns null); otherwise it's the animal's 0..1 liking weight.
public class AnimalFeedPriority_When_weighing_a_feed_for_an_animal
{
    [Theory]
    // Chicken — Grain category only (+ grain/fruitmash tags); no skips. Eats every grain incl. rice + fruitmash; refuses all else.
    [InlineData("chicken", "grain", 1.0f)]
    [InlineData("chicken", "rice", 1.0f)]      // chickens are the only ones that eat rice
    [InlineData("chicken", "carrot", -1f)]     // refuses vegetables even though they fit the small trough
    [InlineData("chicken", "parsnip", -1f)]
    [InlineData("chicken", "drygrass", -1f)]
    [InlineData("chicken", "peanut", -1f)]
    [InlineData("chicken", "soybean", -1f)]
    [InlineData("chicken", "cassava", -1f)]
    [InlineData("chicken", "pumpkin", -1f)]
    [InlineData("chicken", "fruitmash", 0.9f)]
    // Sheep — Veg+Grain categories, grass-eater; skips rice+parsnip.
    [InlineData("sheep", "grain", 0.7f)]
    [InlineData("sheep", "rice", -1f)]
    [InlineData("sheep", "carrot", 1.0f)]
    [InlineData("sheep", "parsnip", -1f)]
    [InlineData("sheep", "drygrass", 0.1f)]
    [InlineData("sheep", "peanut", -1f)]
    [InlineData("sheep", "soybean", 0.2f)]
    [InlineData("sheep", "cassava", -1f)]
    [InlineData("sheep", "pumpkin", 1.0f)]
    [InlineData("sheep", "fruitmash", 0.9f)]
    // Goat — like sheep but has NO grain tag, so grain is accepted by category → the default weight.
    [InlineData("goat", "grain", 0.5f)]
    [InlineData("goat", "rice", -1f)]
    [InlineData("goat", "carrot", 1.0f)]
    [InlineData("goat", "parsnip", -1f)]
    [InlineData("goat", "drygrass", 0.1f)]   // grazes, but hay is its LEAST-liked food
    [InlineData("goat", "peanut", -1f)]
    [InlineData("goat", "soybean", 0.2f)]
    [InlineData("goat", "cassava", -1f)]
    [InlineData("goat", "pumpkin", 1.0f)]
    [InlineData("goat", "fruitmash", 0.9f)]
    // Pig — omnivore (Grain/Fruit/Veg/Protein); refuses grass/hay; eats cassava + both legumes.
    [InlineData("pig", "grain", 0.7f)]
    [InlineData("pig", "rice", -1f)]
    [InlineData("pig", "carrot", 1.0f)]
    [InlineData("pig", "parsnip", -1f)]
    [InlineData("pig", "drygrass", -1f)]     // pigs don't graze
    [InlineData("pig", "peanut", 0.5f)]      // Protein category → default weight
    [InlineData("pig", "soybean", 0.5f)]
    [InlineData("pig", "cassava", 0.2f)]
    [InlineData("pig", "pumpkin", 1.0f)]
    [InlineData("pig", "fruitmash", 0.9f)]
    // Hare — Vegetable only + cassava; no skips (so it's the only one that eats parsnip); refuses grain/hay/legume/fruitmash.
    [InlineData("hare", "grain", -1f)]
    [InlineData("hare", "rice", -1f)]
    [InlineData("hare", "carrot", 1.0f)]
    [InlineData("hare", "parsnip", 1.0f)]    // only the hare eats parsnip
    [InlineData("hare", "drygrass", -1f)]
    [InlineData("hare", "peanut", -1f)]
    [InlineData("hare", "soybean", -1f)]
    [InlineData("hare", "cassava", 0.2f)]
    [InlineData("hare", "pumpkin", 1.0f)]
    [InlineData("hare", "fruitmash", -1f)]
    public void It_returns_the_liking_weight_or_null_when_refused(string animal, string feed, float expected)
    {
        (EnumFoodCategory[] cats, (string, float)[] tags, string[] skip) = DietOf(animal);
        (EnumFoodCategory feedCat, string[] feedTags) = FeedOf(feed);
        float? w = AnimalFeedPriority.PreferenceWeight(cats, tags, skip, feedCat, feedTags, AnimalFeedPriority.CategoryMatchWeight);
        if (expected < 0f) Assert.Null(w);
        else { Assert.NotNull(w); Assert.Equal(expected, w.Value, 3); }
    }

    private static (EnumFoodCategory[], (string, float)[], string[]) DietOf(string animal) => animal switch
    {
        "chicken" => (new[] { EnumFoodCategory.Grain },
            new[] { ("fruitmash", 0.9f), ("grain", 1.0f) }, System.Array.Empty<string>()),
        "sheep" => (new[] { EnumFoodCategory.Vegetable, EnumFoodCategory.Grain },
            new[] { ("grass", 0.1f), ("nibbleCrop", 0.5f), ("grain", 0.7f), ("fruitmash", 0.9f), ("tastyvegetable", 1.0f), ("soybean", 0.2f) },
            new[] { "rice", "parsnip" }),
        "goat" => (new[] { EnumFoodCategory.Vegetable, EnumFoodCategory.Grain },
            new[] { ("nibbleCrop", 0.5f), ("grass", 0.1f), ("fruitmash", 0.9f), ("tastyvegetable", 1.0f), ("soybean", 0.2f) },
            new[] { "rice", "parsnip" }),
        "pig" => (new[] { EnumFoodCategory.Grain, EnumFoodCategory.Fruit, EnumFoodCategory.Vegetable, EnumFoodCategory.Protein },
            new[] { ("grain", 0.7f), ("fruitmash", 0.9f), ("fruit", 0.9f), ("tastyvegetable", 1.0f), ("nibbleCrop", 1.0f), ("cassava", 0.2f) },
            new[] { "rice", "parsnip" }),
        "hare" => (new[] { EnumFoodCategory.Vegetable },
            new[] { ("nibbleCrop", 0.5f), ("tastyvegetable", 1.0f), ("cassava", 0.2f) }, System.Array.Empty<string>()),
        _ => (System.Array.Empty<EnumFoodCategory>(), System.Array.Empty<(string, float)>(), System.Array.Empty<string>()),
    };

    private static (EnumFoodCategory, string[]) FeedOf(string feed) => feed switch
    {
        "grain" => (EnumFoodCategory.Grain, new[] { "grain", "flax" }),
        "rice" => (EnumFoodCategory.Grain, new[] { "grain", "rice" }),
        "carrot" => (EnumFoodCategory.Vegetable, new[] { "carrot", "tastyvegetable" }),
        "parsnip" => (EnumFoodCategory.Vegetable, new[] { "parsnip", "tastyvegetable" }),
        "drygrass" => (EnumFoodCategory.NoNutrition, new[] { "grass" }),
        "peanut" => (EnumFoodCategory.Protein, new[] { "peanut" }),
        "soybean" => (EnumFoodCategory.Protein, new[] { "soybean" }),
        "cassava" => (EnumFoodCategory.NoNutrition, new[] { "cassava" }),
        "pumpkin" => (EnumFoodCategory.Vegetable, new[] { "pumpkin", "tastyvegetable" }),
        "fruitmash" => (EnumFoodCategory.NoNutrition, new[] { "fruitmash" }),
        _ => (EnumFoodCategory.NoNutrition, System.Array.Empty<string>()),
    };
}

public class AnimalFeedPriority_When_choosing_the_most_liked_feed
{
    private static FeedCandidate F(string code, float? weight) => new FeedCandidate(code, weight);

    [Fact]
    public void It_picks_the_highest_weight_edible_feed()
    {
        // A goat over a hay/veg/grain chest: carrot 1.0 > grain 0.5 > hay 0.1 → carrot (veggies, its favourite).
        var chest = new List<FeedCandidate> { F("drygrass", 0.1f), F("vegetable-carrot", 1.0f), F("grain-flax", 0.5f) };
        Assert.Equal("vegetable-carrot", AnimalFeedPriority.ChooseBestFeed(chest));
    }

    [Fact]
    public void It_prefers_grain_over_hay_when_veggies_are_gone()
    {
        // Cascade: no veg stocked. Goat now weighs grain 0.5 above hay 0.1 → grain, not the tempting hay.
        var chest = new List<FeedCandidate> { F("drygrass", 0.1f), F("grain-flax", 0.5f) };
        Assert.Equal("grain-flax", AnimalFeedPriority.ChooseBestFeed(chest));
    }

    [Fact]
    public void It_skips_a_refused_feed_however_tempting()
    {
        // Pig: hay refused (null) even though it's "first" — falls to the edible grain.
        var chest = new List<FeedCandidate> { F("drygrass", null), F("grain-flax", 0.7f) };
        Assert.Equal("grain-flax", AnimalFeedPriority.ChooseBestFeed(chest));
    }

    [Fact]
    public void It_ranks_by_weight_not_enumeration_order()
    {
        var chest = new List<FeedCandidate> { F("grain-flax", 0.5f), F("vegetable-carrot", 1.0f), F("drygrass", 0.1f) };
        Assert.Equal("vegetable-carrot", AnimalFeedPriority.ChooseBestFeed(chest));
    }

    [Fact]
    public void It_breaks_ties_by_first_seen()
    {
        var chest = new List<FeedCandidate> { F("grain-flax", 0.5f), F("grain-rye", 0.5f) };
        Assert.Equal("grain-flax", AnimalFeedPriority.ChooseBestFeed(chest));
    }

    [Fact]
    public void It_returns_null_when_every_candidate_is_refused()
    {
        var chest = new List<FeedCandidate> { F("vegetable-parsnip", null), F("grain-rice", null) };
        Assert.Null(AnimalFeedPriority.ChooseBestFeed(chest));
    }

    [Fact]
    public void It_returns_null_for_an_empty_chest()
    {
        Assert.Null(AnimalFeedPriority.ChooseBestFeed(new List<FeedCandidate>()));
    }
}
