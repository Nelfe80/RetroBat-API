using System;
using System.Linq;
using RetroBat.Api.Media;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// LOT 6 - the planner decides scrape needs from the resolver's view of the catalog, not from raw
/// slots. These pin the exit criterion: only genuinely missing kinds are fetched (local-first), a
/// user media is never re-scraped (even on a region mismatch), and variant enrichment is opt-in.
/// </summary>
public class MediaScrapePlannerTests
{
    private readonly MediaScrapePlanner _planner = new(new MediaResolver());

    private static QualifiedMediaCandidate Candidate(
        string kind, string qualification, int confidence = 50, bool userGamelist = false, string? region = null)
        => new(
            kind,
            new MediaAssetRef($"{kind}.png", "apiexpose", "local", null, null, null),
            qualification,
            confidence,
            region,
            Language: null,
            Style: null,
            ReferencedByUserGamelist: userGamelist);

    private static GameMediaCatalog Catalog(params QualifiedMediaCandidate[] candidates)
        => new(Array.Empty<MediaBinding>(), candidates);

    [Fact]
    public void MissingKind_isPlanned()
    {
        var plan = _planner.Plan(Catalog(), new[] { MediaKinds.Wheel }, ScrapeNeedMode.MissingOnly);

        var need = Assert.Single(plan);
        Assert.Equal(MediaKinds.Wheel, need.Kind);
        Assert.Equal(ScrapeNeedReason.Missing, need.Reason);
    }

    [Fact]
    public void CanonicalExactKind_isNotPlanned()
    {
        // Already satisfied by the canonical store → local-first, no fetch.
        var plan = _planner.Plan(
            Catalog(Candidate(MediaKinds.Wheel, MediaQualifications.ApiExposeIndex, 80)),
            new[] { MediaKinds.Wheel },
            ScrapeNeedMode.MissingOnly);

        Assert.Empty(plan);
    }

    [Fact]
    public void UserMedia_isNeverReScraped_evenOnRegionMismatch()
    {
        // User provided a US wheel; EU is requested. It resolves only as FALLBACK, but a user media
        // is off-limits - not planned even in EnrichVariants.
        var plan = _planner.Plan(
            Catalog(Candidate(MediaKinds.Wheel, MediaQualifications.FilenameConvention, 40, userGamelist: true, region: "us")),
            new[] { MediaKinds.Wheel },
            ScrapeNeedMode.EnrichVariants,
            region: "eu");

        Assert.Empty(plan);
    }

    [Fact]
    public void ExplicitGamelistBinding_isNeverReScraped()
    {
        var plan = _planner.Plan(
            Catalog(Candidate(MediaKinds.Marquee, MediaQualifications.ExplicitGamelist, 100)),
            new[] { MediaKinds.Marquee },
            ScrapeNeedMode.EnrichVariants);

        Assert.Empty(plan);
    }

    [Fact]
    public void RegionFallback_isSatisfied_inMissingOnly()
    {
        // Only a US wheel from the canonical store, EU asked. MissingOnly leaves it - a fallback is
        // still a usable media.
        var plan = _planner.Plan(
            Catalog(Candidate(MediaKinds.Wheel, MediaQualifications.ApiExposeIndex, 80, region: "us")),
            new[] { MediaKinds.Wheel },
            ScrapeNeedMode.MissingOnly,
            region: "eu");

        Assert.Empty(plan);
    }

    [Fact]
    public void RegionFallback_isEnriched_inEnrichVariants()
    {
        var plan = _planner.Plan(
            Catalog(Candidate(MediaKinds.Wheel, MediaQualifications.ApiExposeIndex, 80, region: "us")),
            new[] { MediaKinds.Wheel },
            ScrapeNeedMode.EnrichVariants,
            region: "eu");

        var need = Assert.Single(plan);
        Assert.Equal(ScrapeNeedReason.FallbackOnly, need.Reason);
    }

    [Fact]
    public void MixedKinds_planOnlyTheMissingAndFallback()
    {
        var catalog = Catalog(
            Candidate(MediaKinds.Wheel, MediaQualifications.ApiExposeIndex, 80),                 // exact -> skip
            Candidate(MediaKinds.Marquee, MediaQualifications.ExplicitGamelist, 100),            // user  -> skip
            Candidate(MediaKinds.Thumbnail, MediaQualifications.ApiExposeIndex, 60, region: "us")); // fallback for eu

        var plan = _planner.Plan(
            catalog,
            new[] { MediaKinds.Wheel, MediaKinds.Marquee, MediaKinds.Thumbnail, MediaKinds.Fanart },
            ScrapeNeedMode.EnrichVariants,
            region: "eu");

        var kinds = plan.Select(p => p.Kind).ToHashSet();
        Assert.Contains(MediaKinds.Fanart, kinds);     // missing
        Assert.Contains(MediaKinds.Thumbnail, kinds);  // fallback-only
        Assert.DoesNotContain(MediaKinds.Wheel, kinds);
        Assert.DoesNotContain(MediaKinds.Marquee, kinds);
    }

    [Fact]
    public void BlankAndDuplicateKinds_areIgnored()
    {
        var plan = _planner.Plan(
            Catalog(),
            new[] { MediaKinds.Wheel, MediaKinds.Wheel, "", "   " },
            ScrapeNeedMode.MissingOnly);

        Assert.Single(plan);
    }
}
