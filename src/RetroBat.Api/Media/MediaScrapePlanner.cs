namespace RetroBat.Api.Media;

/// <summary>LOT 6 - how much the scraper is allowed to want.</summary>
public enum ScrapeNeedMode
{
    /// <summary>Only fetch a kind the catalog cannot satisfy at all (local-first default).</summary>
    MissingOnly,

    /// <summary>Also fetch a kind that only resolves to a region/style FALLBACK, to obtain the
    /// exact variant - but still never touches a kind a user media already provides.</summary>
    EnrichVariants
}

/// <summary>Why a kind was planned for scraping.</summary>
public enum ScrapeNeedReason
{
    Missing,
    FallbackOnly
}

/// <summary>One kind the scraper should fetch, with the reason it was planned.</summary>
public readonly record struct ScrapeNeed(string Kind, ScrapeNeedReason Reason);

/// <summary>
/// LOT 6 - decides which media kinds a game still needs from a remote provider, from the resolver's
/// view of what the catalog already holds rather than from raw gamelist slot presence. Two rules
/// carry the exit criterion ("complete the catalog without reorganizing the user's library"):
/// a kind resolved EXACT is never fetched (local-first), and a kind a VALID user media already
/// provides is never fetched - not even for variant enrichment. Only genuinely missing kinds (and,
/// under <see cref="ScrapeNeedMode.EnrichVariants"/>, kinds stuck on a region/style fallback) are
/// planned. The planner is pure over a <see cref="GameMediaCatalog"/>; it schedules nothing itself.
/// </summary>
public sealed class MediaScrapePlanner
{
    private readonly MediaResolver _resolver;

    public MediaScrapePlanner(MediaResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public IReadOnlyList<ScrapeNeed> Plan(
        GameMediaCatalog catalog,
        IEnumerable<string> requestedKinds,
        ScrapeNeedMode mode,
        string? region = null,
        string? language = null,
        string? style = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(requestedKinds);

        var needs = new List<ScrapeNeed>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kind in requestedKinds)
        {
            if (string.IsNullOrWhiteSpace(kind) || !seen.Add(kind))
            {
                continue;
            }

            // A valid user media is off-limits: never re-scrape a kind the user provided,
            // even when the request profile does not match its region/style.
            if (HasUserMedia(catalog, kind))
            {
                continue;
            }

            // The catalog is already scoped to one game; the resolver keys only on Kind + profile.
            var result = _resolver.Resolve(
                catalog,
                new MediaResolveRequest(string.Empty, string.Empty, kind, region, language, style));

            switch (result.State)
            {
                case MediaResolveState.Missing:
                    needs.Add(new ScrapeNeed(kind, ScrapeNeedReason.Missing));
                    break;
                case MediaResolveState.Fallback when mode == ScrapeNeedMode.EnrichVariants:
                    needs.Add(new ScrapeNeed(kind, ScrapeNeedReason.FallbackOnly));
                    break;
                // Exact, or Fallback in MissingOnly: already satisfied - nothing to fetch.
            }
        }

        return needs;
    }

    /// <summary>A kind counts as user-provided when any candidate for it is referenced by the user
    /// gamelist or carries the explicit-gamelist qualification (§5.2). Provider-explicit and
    /// canonical-store candidates do NOT count - the scraper may still enrich those.</summary>
    private static bool HasUserMedia(GameMediaCatalog catalog, string kind)
    {
        foreach (var candidate in catalog.Candidates)
        {
            if (!string.Equals(candidate.Kind, kind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (candidate.ReferencedByUserGamelist
                || string.Equals(candidate.Qualification, MediaQualifications.ExplicitGamelist, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
