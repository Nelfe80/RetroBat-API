using System.Collections.Generic;
using RetroBat.Api.Media;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// LOT 6 (2/2) - the merge that lets the resolver refine the raw-slot needs. Invariant: the planner
/// may only SUPPRESS a scrape (a kind the canonical store already satisfies), never add one - so a
/// genuinely missing kind is never skipped and a present media is never disturbed.
/// </summary>
public class ScrapeNeedSuppressionTests
{
    private static MediaNeed Need(string kind, bool missing) => new() { Kind = kind, IsMissing = missing };

    [Fact]
    public void MissingKind_satisfiedByCanonical_isSuppressed()
    {
        // Raw slot said missing, but the planner did NOT return it -> canonical satisfies it.
        var needs = new List<MediaNeed> { Need(MediaKinds.Wheel, missing: true) };

        MediaNeedEvaluator.SuppressSatisfiedNeeds(needs, new HashSet<string>());

        Assert.False(needs[0].IsMissing);
    }

    [Fact]
    public void MissingKind_stillMissingEverywhere_staysMissing()
    {
        var needs = new List<MediaNeed> { Need(MediaKinds.Wheel, missing: true) };

        MediaNeedEvaluator.SuppressSatisfiedNeeds(needs, new HashSet<string> { MediaKinds.Wheel });

        Assert.True(needs[0].IsMissing);
    }

    [Fact]
    public void PresentKind_isNeverTurnedIntoAScrape()
    {
        // Already present (not missing). Even if the planner "would" want it, we never add a scrape.
        var needs = new List<MediaNeed> { Need(MediaKinds.Marquee, missing: false) };

        MediaNeedEvaluator.SuppressSatisfiedNeeds(needs, new HashSet<string> { MediaKinds.Marquee });

        Assert.False(needs[0].IsMissing);
    }

    [Fact]
    public void MixedNeeds_onlyUnsatisfiedMissingSurvive()
    {
        var needs = new List<MediaNeed>
        {
            Need(MediaKinds.Wheel, missing: true),      // satisfied by canonical -> suppress
            Need(MediaKinds.Fanart, missing: true),     // still missing -> keep
            Need(MediaKinds.Image, missing: false)      // present -> stays present
        };

        MediaNeedEvaluator.SuppressSatisfiedNeeds(needs, new HashSet<string> { MediaKinds.Fanart });

        Assert.False(needs[0].IsMissing);
        Assert.True(needs[1].IsMissing);
        Assert.False(needs[2].IsMissing);
    }
}
