namespace RetroBat.Api.Media;

/// <summary>
/// LOT 4 — builds a <see cref="GameMediaCatalog"/> by unifying the user gamelist (LOT 3) and the
/// canonical store (the thin canonical source). It only AGGREGATES, with provenance kept intact;
/// deciding which asset wins for a request is the <see cref="MediaResolver"/>'s job (§9.3). Purely
/// read-only — no file is moved or written.
/// </summary>
public sealed class GameMediaCatalogService
{
    private readonly GamelistMediaCatalogReader _gamelist;
    private readonly ICanonicalMediaCatalogSource _canonical;

    public GameMediaCatalogService(GamelistMediaCatalogReader gamelist, ICanonicalMediaCatalogSource canonical)
    {
        _gamelist = gamelist;
        _canonical = canonical;
    }

    /// <summary>Unify the gamelist media (when <paramref name="romPath"/> is known) and the
    /// canonical store into one catalog for a game.</summary>
    public GameMediaCatalog BuildCatalog(string systemId, string gameSlug, string? romPath = null)
    {
        var bindings = new List<MediaBinding>();
        var candidates = new List<QualifiedMediaCandidate>();

        if (!string.IsNullOrWhiteSpace(romPath))
        {
            var gamelistMedia = _gamelist.GetGameMedia(systemId, romPath);
            if (gamelistMedia != null)
            {
                bindings.AddRange(gamelistMedia.Bindings);
                candidates.AddRange(gamelistMedia.Candidates);
            }
        }

        candidates.AddRange(_canonical.GetCanonicalCandidates(systemId, gameSlug));
        return new GameMediaCatalog(bindings, candidates);
    }
}

/// <summary>
/// LOT 4 — resolves ONE media from a catalog WITHOUT the caller knowing the physical root (the
/// returned asset carries its own PathRoot). Applies the §9.3 precedence, then reports EXACT when
/// the winner satisfies the requested region/language/style profile and FALLBACK when only that
/// profile differs. This is the resolver the plan's §9 asks for; wiring it into the live
/// projection is LOT 7.
/// </summary>
public sealed class MediaResolver
{
    public MediaResolveResult Resolve(GameMediaCatalog catalog, MediaResolveRequest request)
    {
        var forKind = catalog.Candidates
            .Where(c => string.Equals(c.Kind, request.Kind, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (forKind.Count == 0)
        {
            return MediaResolveResult.Missing;
        }

        var winner = forKind
            .OrderByDescending(Rank)
            .ThenByDescending(c => c.Confidence)
            .First();

        var state = ProfileMatches(winner, request) ? MediaResolveState.Exact : MediaResolveState.Fallback;
        return new MediaResolveResult(state, winner.Asset, winner.Kind, winner.Qualification);
    }

    // §9.3 precedence, highest first.
    private static int Rank(QualifiedMediaCandidate c) => c.Qualification switch
    {
        MediaQualifications.ExplicitGamelist => 500,
        MediaQualifications.ExplicitProvider => 450,
        // a user-gamelist file typed by its own name is a strong, user-chosen signal
        MediaQualifications.FilenameConvention when c.ReferencedByUserGamelist => 400,
        MediaQualifications.ApiExposeIndex => 300,
        MediaQualifications.FilenameConvention => 200,
        MediaQualifications.FolderConvention => 150,
        MediaQualifications.Heuristic => 50,
        _ => 100
    };

    // A candidate with no region/language/style of its own satisfies any request (generic media);
    // only a SPECIFIC value that differs from a requested one demotes the result to a fallback.
    private static bool ProfileMatches(QualifiedMediaCandidate c, MediaResolveRequest r)
        => Compatible(c.Region, r.Region)
           && Compatible(c.Language, r.Language)
           && Compatible(c.Style, r.Style);

    private static bool Compatible(string? candidate, string? requested)
        => string.IsNullOrEmpty(requested)
           || string.IsNullOrEmpty(candidate)
           || string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase);
}
