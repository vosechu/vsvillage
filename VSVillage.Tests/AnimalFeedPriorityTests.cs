using System.Collections.Generic;
using Xunit;

namespace VsVillage.Tests;

public class AnimalFeedPriority_When_ranking_a_feed_code
{
    [Theory]
    [InlineData("drygrass", 0)]            // "hay" is the drygrass item -> best
    [InlineData("hay-normal-ud", 0)]       // the placeable hay block -> best
    [InlineData("vegetable-carrot", 1)]
    [InlineData("vegetable-parsnip", 1)]   // ranks as a vegetable; edibility is a separate gate
    [InlineData("grain-flax", 2)]
    [InlineData("grain-rice", 2)]          // ranks as grain; rice being chicken-only is a diet gate
    [InlineData("legume-soybean", 3)]
    [InlineData("legume-peanut", 3)]
    [InlineData("rawcassava-raw", 4)]
    [InlineData("pressedmash-apple", 5)]   // fruitmash wildcard content
    [InlineData("cobblestone-granite", 99)] // not a feed
    [InlineData("", 99)]
    public void It_maps_each_feed_code_to_its_imposed_rank(string codePath, int expected)
    {
        Assert.Equal(expected, AnimalFeedPriority.FeedRank(codePath));
    }
}

public class AnimalFeedPriority_When_choosing_the_best_edible_feed
{
    // Each case models one animal's edibility outcome over a hay/veg/grain chest, per the game diets:
    // goat eats all, pig can't eat hay (grass), chicken eats only grain.
    private static FeedCandidate F(string code, bool edible) => new FeedCandidate(code, edible);

    [Fact]
    public void It_picks_hay_for_a_goat_that_can_eat_everything()
    {
        var chest = new List<FeedCandidate>
        { F("drygrass", true), F("vegetable-carrot", true), F("grain-flax", true) };
        Assert.Equal("drygrass", AnimalFeedPriority.ChooseBestFeed(chest));
    }

    [Fact]
    public void It_cascades_past_hay_to_veggies_for_a_pig_that_cannot_eat_grass()
    {
        var chest = new List<FeedCandidate>
        { F("drygrass", false), F("vegetable-carrot", true), F("grain-flax", true) };
        Assert.Equal("vegetable-carrot", AnimalFeedPriority.ChooseBestFeed(chest));
    }

    [Fact]
    public void It_cascades_to_grain_for_a_chicken_that_eats_neither_hay_nor_veggies()
    {
        var chest = new List<FeedCandidate>
        { F("drygrass", false), F("vegetable-carrot", false), F("grain-flax", true) };
        Assert.Equal("grain-flax", AnimalFeedPriority.ChooseBestFeed(chest));
    }

    [Fact]
    public void It_cascades_to_veggies_for_a_goat_when_hay_is_out_of_stock()
    {
        var chest = new List<FeedCandidate>
        { F("vegetable-carrot", true), F("grain-flax", true) };   // no hay in chest at all
        Assert.Equal("vegetable-carrot", AnimalFeedPriority.ChooseBestFeed(chest));
    }

    [Fact]
    public void It_returns_null_when_nothing_is_edible()
    {
        var chest = new List<FeedCandidate>
        { F("vegetable-parsnip", false), F("grain-rice", false) };  // parsnip inedible; rice not for a mammal
        Assert.Null(AnimalFeedPriority.ChooseBestFeed(chest));
    }

    [Fact]
    public void It_returns_null_for_an_empty_chest()
    {
        Assert.Null(AnimalFeedPriority.ChooseBestFeed(new List<FeedCandidate>()));
    }

    [Fact]
    public void It_ranks_by_priority_not_enumeration_order_hay_last_still_wins()
    {
        var chest = new List<FeedCandidate>
        { F("grain-flax", true), F("vegetable-carrot", true), F("drygrass", true) };  // hay listed LAST
        Assert.Equal("drygrass", AnimalFeedPriority.ChooseBestFeed(chest));
    }

    [Fact]
    public void It_breaks_ties_at_equal_rank_by_first_seen()
    {
        var chest = new List<FeedCandidate>
        { F("grain-flax", true), F("grain-rye", true) };   // same rank (grain) -> first wins
        Assert.Equal("grain-flax", AnimalFeedPriority.ChooseBestFeed(chest));
    }

    [Fact]
    public void It_skips_an_inedible_higher_priority_feed_for_an_edible_lower_one()
    {
        var chest = new List<FeedCandidate>
        { F("drygrass", false), F("grain-flax", true) };   // hay present but inedible -> grain
        Assert.Equal("grain-flax", AnimalFeedPriority.ChooseBestFeed(chest));
    }
}
