using System.Text.Json;
using System.Text.Json.Serialization;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Sharing;

/// <summary>Une borne à qui demander un objet. <paramref name="ApiKey"/> est la clé de CETTE
/// borne-là, obtenue à l'appairage : sans elle, elle répond 401 hors loopback.</summary>
public sealed record ReplayPeer(string Name, string BaseUrl, string? ApiKey,
    /// <summary>Par quelle porte cette borne a été apprise : manuel, lan, ancre, plateforme.</summary>
    string Source = "manuel",
    /// <summary>Identité plateforme annoncée, quand elle est connue. Informative ; elle
    /// n'autorise rien tant que les jetons signés n'existent pas.</summary>
    string? DeviceId = null,
    /// <summary>Gabarit d'URL avec le marqueur <c>{sha}</c>, quand la source n'expose pas la
    /// route d'API d'une borne. Une amorce GitHub sert par exemple
    /// <c>releases/download/objects/{sha}.replay</c> : la connaître ici évite de coder en dur
    /// la forme d'un hébergeur dans le client de transfert.</summary>
    string? UrlTemplate = null);

public sealed record ReplayPeersDoc(string Schema, IReadOnlyList<ReplayPeer> Peers);

/// <summary>
/// Les pairs connus de cette borne, lus dans <c>state/nelfenet/peers.json</c>.
///
/// Ce fichier vit dans <c>state/</c> et NON dans appsettings.json, pour une raison simple :
/// appsettings.json est versionné, et une clé d'API de borne n'a rien à faire dans un dépôt.
///
/// La liste est relue dès que le fichier change, donc ajouter un pair ne demande pas de
/// redémarrer l'API. L'ordre du fichier est l'ordre d'essai : c'est la politique de
/// l'exploitant, pas une préférence codée en dur pour un type de source (CDC §47).
/// </summary>
public sealed class ReplayPeerStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<ReplayPeerStore> _logger;
    private readonly object _gate = new();
    private DateTime _loadedStamp;
    private IReadOnlyList<ReplayPeer> _cache = Array.Empty<ReplayPeer>();

    public ReplayPeerStore(ILogger<ReplayPeerStore> logger) => _logger = logger;

    public string Path => System.IO.Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfenet", "peers.json");

    public IReadOnlyList<ReplayPeer> Peers
    {
        get
        {
            try
            {
                var fi = new FileInfo(Path);
                if (!fi.Exists) { lock (_gate) { _cache = Array.Empty<ReplayPeer>(); _loadedStamp = default; } return _cache; }

                lock (_gate)
                {
                    if (fi.LastWriteTimeUtc == _loadedStamp) return _cache;
                    var doc = JsonSerializer.Deserialize<ReplayPeersDoc>(File.ReadAllBytes(Path), Json);
                    _cache = (doc?.Peers ?? new List<ReplayPeer>())
                        .Where(p => !string.IsNullOrWhiteSpace(p.BaseUrl))
                        .ToList();
                    _loadedStamp = fi.LastWriteTimeUtc;
                    _logger.LogInformation("Replay : {Count} pair(s) NelfeNet chargé(s).", _cache.Count);
                    return _cache;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Replay : liste de pairs illisible ({Path}) — aucun pair.", Path);
                return Array.Empty<ReplayPeer>();
            }
        }
    }
}
