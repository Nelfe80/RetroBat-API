namespace RetroBat.Api.Media;

/// <summary>
/// LOT 2 — a physical media file, WITHOUT assigning it an ES role on its own. The lean domain
/// counterpart of the projection's <c>MediaStreamAsset</c>: the catalog reasons about files with
/// this, the projection keeps serving its own DTO (they map to one another rather than duplicate).
/// <paramref name="PathRoot"/> matches the HP5 contract ("apiexpose" / "retrobat" / "theme" /
/// "external-local"); "external-local" stays local-only unless the root is explicitly allowed.
/// </summary>
public sealed record MediaAssetRef(
    string Path,
    string PathRoot,
    string Origin,
    string? Url,
    long? Length,
    DateTime? LastWriteTimeUtc);

/// <summary>
/// LOT 2 — a file qualified to a media <paramref name="Kind"/>, carrying WHERE the type came from
/// (<paramref name="Qualification"/>) so provenance is never lost. <paramref name="Confidence"/>
/// only breaks ties between inferences; it never overrides an explicit source. Region / Language /
/// Style are filled when determinable (e.g. from the scraper, LOT 6) and null otherwise.
/// </summary>
public sealed record QualifiedMediaCandidate(
    string Kind,
    MediaAssetRef Asset,
    string Qualification,
    int Confidence,
    string? Region,
    string? Language,
    string? Style,
    bool ReferencedByUserGamelist);

/// <summary>
/// LOT 2 — what the user gamelist currently asks ES to put in a slot (image / marquee /
/// thumbnail). A generic slot alone never implies a semantic Kind (see
/// <see cref="MediaQualifications"/> and §7.3): the Kind, if any, comes from qualifying the file.
/// </summary>
public sealed record MediaBinding(
    string Slot,
    MediaAssetRef Asset,
    string SourceField,
    bool ManagedByApiExpose);

/// <summary>The qualification-source labels, most certain to least (§5.2 / §7.2). A weaker source
/// never overrides a stronger one already attached to the same file.</summary>
public static class MediaQualifications
{
    public const string ExplicitGamelist = "explicit-gamelist";
    public const string ExplicitProvider = "explicit-provider";
    public const string ApiExposeIndex = "apiexpose-index";
    public const string FilenameConvention = "filename-convention";
    public const string FolderConvention = "folder-convention";
    public const string Heuristic = "heuristic";
}

/// <summary>LOT 4 — the outcome of a resolution (§9.2). MISSING: nothing for the kind. EXACT: a
/// candidate that satisfies the requested profile. FALLBACK: the best available, but its
/// region/language/style does not match what was asked.</summary>
public enum MediaResolveState
{
    Missing,
    Exact,
    Fallback
}

/// <summary>LOT 4 — ask the catalog for one media KIND of a game, optionally for a region /
/// language / style profile. A null part of the profile means "no preference".</summary>
public sealed record MediaResolveRequest(
    string SystemId,
    string GameSlug,
    string Kind,
    string? Region = null,
    string? Language = null,
    string? Style = null);

/// <summary>LOT 4 — what the resolver returns: the chosen asset (root-transparent via its
/// PathRoot), the kind it satisfies and the qualification source it came from.</summary>
public sealed record MediaResolveResult(
    MediaResolveState State,
    MediaAssetRef? Asset,
    string? Kind,
    string? Source)
{
    public static readonly MediaResolveResult Missing = new(MediaResolveState.Missing, null, null, null);
}

/// <summary>LOT 4 — a game's media from EVERY source unified: the user gamelist bindings and all
/// qualified candidates (gamelist + canonical store), each still carrying its provenance. The
/// resolver reduces this to one asset per request; the catalog never picks on its own.</summary>
public sealed class GameMediaCatalog
{
    public IReadOnlyList<MediaBinding> Bindings { get; }
    public IReadOnlyList<QualifiedMediaCandidate> Candidates { get; }

    public GameMediaCatalog(
        IReadOnlyList<MediaBinding> bindings,
        IReadOnlyList<QualifiedMediaCandidate> candidates)
    {
        Bindings = bindings;
        Candidates = candidates;
    }
}
