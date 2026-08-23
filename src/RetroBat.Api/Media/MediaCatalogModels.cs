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
