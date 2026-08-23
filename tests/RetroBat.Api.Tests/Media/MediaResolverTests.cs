using System;
using RetroBat.Api.Media;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// LOT 4 — the resolver reduces a unified catalog to ONE asset per request, root-transparent. These
/// pin the §9.3 precedence (user gamelist beats the canonical store), the confidence tie-break, and
/// the EXACT vs FALLBACK verdict against a requested region/style profile.
/// </summary>
public class MediaResolverTests
{
    private readonly MediaResolver _resolver = new();

    private static QualifiedMediaCandidate Candidate(
        string kind, string qualification, int confidence = 50, bool userGamelist = false, string? region = null)
        => new(
            kind,
            new MediaAssetRef($"{kind}-{qualification}-{confidence}.png", "apiexpose", "local", null, null, null),
            qualification,
            confidence,
            region,
            Language: null,
            Style: null,
            ReferencedByUserGamelist: userGamelist);

    private static GameMediaCatalog Catalog(params QualifiedMediaCandidate[] candidates)
        => new(Array.Empty<MediaBinding>(), candidates);

    private MediaResolveResult Resolve(GameMediaCatalog catalog, string kind, string? region = null)
        => _resolver.Resolve(catalog, new MediaResolveRequest("sys", "game", kind, region));

    [Fact]
    public void ExplicitGamelist_beatsCanonicalStore()
    {
        var catalog = Catalog(
            Candidate(MediaKinds.Wheel, MediaQualifications.ApiExposeIndex, 80),
            Candidate(MediaKinds.Wheel, MediaQualifications.ExplicitGamelist, 100));

        var result = Resolve(catalog, MediaKinds.Wheel);

        Assert.Equal(MediaResolveState.Exact, result.State);
        Assert.Equal(MediaQualifications.ExplicitGamelist, result.Source);
    }

    [Fact]
    public void UserGamelistFilename_beatsCanonicalStore()
    {
        // A user-gamelist file typed by its own name (rank 400) outranks apiexpose-index (300).
        var catalog = Catalog(
            Candidate(MediaKinds.Wheel, MediaQualifications.ApiExposeIndex, 99),
            Candidate(MediaKinds.Wheel, MediaQualifications.FilenameConvention, 40, userGamelist: true));

        var result = Resolve(catalog, MediaKinds.Wheel);

        Assert.Equal(MediaQualifications.FilenameConvention, result.Source);
    }

    [Fact]
    public void CanonicalStore_wins_whenNoUserMedia()
    {
        var catalog = Catalog(Candidate(MediaKinds.Wheel, MediaQualifications.ApiExposeIndex, 80));

        var result = Resolve(catalog, MediaKinds.Wheel);

        Assert.Equal(MediaResolveState.Exact, result.State);
        Assert.Equal(MediaQualifications.ApiExposeIndex, result.Source);
        Assert.NotNull(result.Asset);
    }

    [Fact]
    public void SameSource_higherConfidenceWins()
    {
        var catalog = Catalog(
            Candidate(MediaKinds.Fanart, MediaQualifications.ApiExposeIndex, 40),
            Candidate(MediaKinds.Fanart, MediaQualifications.ApiExposeIndex, 90));

        var result = Resolve(catalog, MediaKinds.Fanart);

        Assert.Equal(MediaResolveState.Exact, result.State);
        Assert.Contains("-90", result.Asset!.Path); // the higher-confidence asset won
    }

    [Fact]
    public void NoCandidateForKind_isMissing()
    {
        var catalog = Catalog(Candidate(MediaKinds.Wheel, MediaQualifications.ApiExposeIndex));

        var result = Resolve(catalog, MediaKinds.Marquee);

        Assert.Equal(MediaResolveState.Missing, result.State);
        Assert.Null(result.Asset);
    }

    [Fact]
    public void GenericCandidate_satisfiesAnyRegion_asExact()
    {
        // Candidate carries no region: it satisfies a region request as EXACT (generic media).
        var catalog = Catalog(Candidate(MediaKinds.Wheel, MediaQualifications.ApiExposeIndex, region: null));

        var result = Resolve(catalog, MediaKinds.Wheel, region: "eu");

        Assert.Equal(MediaResolveState.Exact, result.State);
    }

    [Fact]
    public void SpecificRegionMismatch_isFallback()
    {
        // Only a US wheel exists but EU was asked: still returned, but as a FALLBACK.
        var catalog = Catalog(Candidate(MediaKinds.Wheel, MediaQualifications.ApiExposeIndex, region: "us"));

        var result = Resolve(catalog, MediaKinds.Wheel, region: "eu");

        Assert.Equal(MediaResolveState.Fallback, result.State);
        Assert.NotNull(result.Asset);
    }
}
