using System.Collections.Generic;

namespace VsVillage;

/// <summary>
/// Pure feed-selection policy for the shepherd: rank feeds by an IMPOSED preference and pick the
/// best one an animal will actually eat, cascading when the preferred feed isn't stocked.
///
/// Two halves live in two places on purpose (see .claude/rules/testing.md boundary): the DIETARY
/// question "will this animal eat this feed?" is world-touching (the game's
/// <c>CreatureDiet.Matches</c> + trough <c>UnsuitableForEntity</c> + physical <c>AcceptsItem</c>),
/// computed in the AI task and passed in as <see cref="FeedCandidate.Edible"/>. The PRIORITY +
/// cascade over already-classified candidates is pure value logic and lives here, unit-tested.
///
/// The rank is a design choice — VS gives no feed-quality signal (every trough portion = 1
/// saturation; diet tag weights don't gate the trough path). Order: hay/drygrass &gt; vegetable &gt;
/// grain &gt; legume &gt; cassava &gt; fruitmash &gt; anything else.
/// </summary>
public static class AnimalFeedPriority
{
    public const int RankHay = 0;
    public const int RankVegetable = 1;
    public const int RankGrain = 2;
    public const int RankLegume = 3;
    public const int RankCassava = 4;
    public const int RankFruitmash = 5;
    public const int RankOther = 99;

    // Classify a collectible code PATH (e.g. "grain-flax", "drygrass") into its imposed rank.
    // Category prefixes come straight from the trough content-config content codes (see research).
    public static int FeedRank(string codePath)
    {
        if (string.IsNullOrEmpty(codePath)) return RankOther;
        // "hay" == the drygrass item or the hay-normal-* block; both carry the game's "grass" food tag.
        if (codePath == "drygrass" || codePath.StartsWith("hay", System.StringComparison.Ordinal)) return RankHay;
        if (codePath.StartsWith("vegetable-", System.StringComparison.Ordinal)) return RankVegetable;
        if (codePath.StartsWith("grain-", System.StringComparison.Ordinal)) return RankGrain;
        if (codePath.StartsWith("legume-", System.StringComparison.Ordinal)) return RankLegume;
        if (codePath.StartsWith("rawcassava", System.StringComparison.Ordinal)) return RankCassava;
        if (codePath.StartsWith("pressedmash", System.StringComparison.Ordinal)) return RankFruitmash;
        return RankOther;
    }

    // The cascade: the lowest-rank EDIBLE candidate, or null if none is edible. Strict &lt; keeps ties
    // stable on first-seen. Inedible candidates are skipped entirely — a preferred-but-inedible feed
    // (hay for a pig, veggies/parsnip for a chicken) never blocks a lower-ranked edible one.
    public static string ChooseBestFeed(IEnumerable<FeedCandidate> candidates)
    {
        string best = null;
        int bestRank = int.MaxValue;
        foreach (FeedCandidate c in candidates)
        {
            if (!c.Edible) continue;
            int r = FeedRank(c.CodePath);
            if (r < bestRank) { bestRank = r; best = c.CodePath; }
        }
        return best;
    }
}

/// <summary>A source feed and whether the target animal will eat it (dietary gate computed upstream).</summary>
public readonly struct FeedCandidate
{
    public readonly string CodePath;
    public readonly bool Edible;
    public FeedCandidate(string codePath, bool edible) { CodePath = codePath; Edible = edible; }
}
