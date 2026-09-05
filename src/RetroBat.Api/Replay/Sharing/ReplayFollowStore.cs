using System.Text.Json;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Sharing;

/// <summary>Un classement que cette borne accepte d'aider à diffuser.</summary>
public sealed record ReplayFollow(string RomGroup, string Ruleset);

public sealed record ReplayFollowDoc(string Schema, IReadOnlyList<ReplayFollow> Follows);

/// <summary>
/// Les classements suivis par cette borne (CDC DEV §101.8, agent de réplication).
///
/// Suivre un classement, c'est accepter d'en héberger des replays pour que d'autres puissent les
/// regarder. C'est donc un choix du propriétaire de la machine, jamais un défaut : on ne
/// télécharge pas les parties d'inconnus sur le PC de quelqu'un sans qu'il l'ait demandé.
///
/// Le fichier vit dans <c>state/</c>, hors git, et est relu à chaud.
/// </summary>
public sealed class ReplayFollowStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ILogger<ReplayFollowStore> _logger;
    private readonly object _gate = new();
    private DateTime _stamp;
    private IReadOnlyList<ReplayFollow> _cache = Array.Empty<ReplayFollow>();

    public ReplayFollowStore(ILogger<ReplayFollowStore> logger) => _logger = logger;

    public string Path => System.IO.Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfenet", "follow.json");

    public IReadOnlyList<ReplayFollow> Follows
    {
        get
        {
            try
            {
                var fi = new FileInfo(Path);
                if (!fi.Exists) return Array.Empty<ReplayFollow>();
                lock (_gate)
                {
                    if (fi.LastWriteTimeUtc == _stamp) return _cache;
                    var doc = JsonSerializer.Deserialize<ReplayFollowDoc>(File.ReadAllBytes(Path), Json);
                    _cache = (doc?.Follows ?? new List<ReplayFollow>())
                        .Where(f => !string.IsNullOrWhiteSpace(f.RomGroup) && !string.IsNullOrWhiteSpace(f.Ruleset))
                        .ToList();
                    _stamp = fi.LastWriteTimeUtc;
                    return _cache;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Replay : liste des classements suivis illisible.");
                return Array.Empty<ReplayFollow>();
            }
        }
    }
}
