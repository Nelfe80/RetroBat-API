using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Media;

/// <summary>
/// LOT 4 — the CANONICAL media of a game (the APIExpose store), as qualified candidates. A thin,
/// injectable seam so <see cref="GameMediaCatalogService"/> stays testable: the real implementation
/// delegates to the pragmatic inventory (BuildAssetTable) that HP1-HP3 already build and cache; a
/// test supplies its own. This is the "just enough" alternative to the full IGameMediaDiscovery —
/// the catalog needs "canonical media by kind for this game", not a whole discovery abstraction.
/// </summary>
public interface ICanonicalMediaCatalogSource
{
    IReadOnlyList<QualifiedMediaCandidate> GetCanonicalCandidates(string systemId, string gameSlug);
}

/// <summary>Default source: the APIExpose media store, via the projection's own inventory (targeted,
/// cached). Everything it returns lives under the plugin, so PathRoot is "apiexpose" (falling back
/// to whatever the asset already carries when HP5 stamped one).</summary>
internal sealed class ProjectionCanonicalMediaCatalogSource : ICanonicalMediaCatalogSource
{
    public IReadOnlyList<QualifiedMediaCandidate> GetCanonicalCandidates(string systemId, string gameSlug)
    {
        var roots = PhysicalMediaWebSocketProjectionService.ResolveGameRoots(systemId, gameSlug).ToList();
        var table = PhysicalMediaWebSocketProjectionService.BuildAssetTable(
            roots, PhysicalMediaWebSocketProjectionService.DisplayableMediaKinds);

        var candidates = new List<QualifiedMediaCandidate>(table.Count);
        foreach (var (kind, asset) in table)
        {
            var reference = new MediaAssetRef(
                asset.Path,
                string.IsNullOrEmpty(asset.PathRoot) ? "apiexpose" : asset.PathRoot!,
                asset.Origin,
                asset.Url is { Length: > 0 } ? asset.Url : null,
                asset.Length,
                asset.LastWriteTimeUtc);

            candidates.Add(new QualifiedMediaCandidate(
                kind,
                reference,
                MediaQualifications.ApiExposeIndex,
                Confidence: 80,
                Region: null,
                Language: null,
                Style: null,
                ReferencedByUserGamelist: false));
        }

        return candidates;
    }
}
