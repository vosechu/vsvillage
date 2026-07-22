using System.Collections.Generic;
using Vintagestory.API.Common;

namespace VsVillage;

/// <summary>
/// Pure feed-selection policy: pick the feed an animal LIKES BEST among the ones it will eat. We borrow
/// the game's own per-animal preference signal rather than inventing a global order — each animal's
/// <c>CreatureDiet.WeightedFoodTags</c> carries a 0..1 "how much I like this food tag" weight. The trough
/// eat-check ignores those weights (it passes minWeight 0), but they're the game's authored preference, so
/// the shepherd feeds each animal its highest-weight edible feed (e.g. a goat prefers tasty vegetables 1.0
/// over grass/hay 0.1). Kept as plain values (no CreatureDiet/world types) so it is fully unit-tested.
/// </summary>
public static class AnimalFeedPriority
{
    /// <summary>Preference for a feed a diet accepts by CATEGORY alone (no specific weighted tag). The one
    /// imposed constant: a mid value so a category-accepted staple (e.g. grain for a goat) outranks a
    /// barely-liked tag (grass 0.1) but yields to a loved one (tastyvegetable 1.0).</summary>
    public const float CategoryMatchWeight = 0.5f;

    /// <summary>
    /// The animal's preference weight for a feed, or <c>null</c> if it refuses the feed. Mirrors the game's
    /// <c>CreatureDiet.Matches</c> edibility (skip-tags reject FIRST, then category-or-tag accepts) and layers
    /// the liking weight on top: a matched weighted tag yields its weight; a category-only match yields
    /// <paramref name="categoryDefault"/>; nothing matched (or a skip-tag hit) yields null (refused).
    /// All inputs are the diet's deconstructed fields + the feed's category/tags — plain values, no world.
    /// </summary>
    public static float? PreferenceWeight(
        EnumFoodCategory[] dietCategories, (string code, float weight)[] dietWeightedTags, string[] dietSkipTags,
        EnumFoodCategory feedCategory, string[] feedTags, float categoryDefault)
    {
        // 1. Skip tags — hard reject, checked first (parsnip/rice for the ruminants).
        if (dietSkipTags != null && feedTags != null)
            foreach (string ft in feedTags)
                foreach (string skip in dietSkipTags)
                    if (skip == ft) return null;

        // 2. Best-liked matching weighted tag (the specific preference signal).
        float best = -1f;
        if (feedTags != null && dietWeightedTags != null)
            foreach (string ft in feedTags)
                foreach ((string code, float weight) wt in dietWeightedTags)
                    if (wt.code == ft && wt.weight > best) best = wt.weight;
        if (best >= 0f) return best;

        // 3. No tag matched — accepted iff the nutrition category is in the diet (grain for a goat, legume
        //    for a pig): edible but unranked, so the imposed default weight.
        if (dietCategories != null)
            foreach (EnumFoodCategory cat in dietCategories)
                if (cat == feedCategory) return categoryDefault;

        return null;   // refused
    }

    /// <summary>The most-preferred feed among candidates the animal will eat (highest weight), or null when
    /// none is edible. Strict &gt; keeps ties stable on first-seen. A refused candidate (null weight) is
    /// skipped — a tempting-but-refused feed (hay for a pig) never blocks a lower-liked edible one.</summary>
    public static string ChooseBestFeed(IEnumerable<FeedCandidate> candidates)
    {
        string best = null;
        float bestWeight = float.NegativeInfinity;
        foreach (FeedCandidate c in candidates)
        {
            if (c.Weight is not float w) continue;   // refused
            if (w > bestWeight) { bestWeight = w; best = c.CodePath; }
        }
        return best;
    }
}

/// <summary>A source feed and the served animal's preference weight for it (null = the animal refuses it).</summary>
public readonly struct FeedCandidate
{
    public readonly string CodePath;
    public readonly float? Weight;
    public FeedCandidate(string codePath, float? weight) { CodePath = codePath; Weight = weight; }
}
