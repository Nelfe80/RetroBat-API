using System.Text.Json;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Sharing;

/// <summary>Une intention de semis : ce replay doit atteindre l'amorce, quoi qu'il arrive.</summary>
public sealed record ReplaySeedIntent(
    string ReplayId,
    string ObjectSha256,
    DateTime EnqueuedUtc,
    int Attempts = 0,
    DateTime? LastPushUtc = null,
    string? LastError = null);

public sealed record ReplaySeedQueueDoc(string Schema, IReadOnlyList<ReplaySeedIntent> Intents);

/// <summary>
/// La file de semis (CDC DEV §101.5).
///
/// Une borne chez un particulier s'éteint. Souvent juste après la partie, c'est-à-dire au pire
/// moment : celui où le record vient d'être établi et où personne d'autre ne le détient encore.
/// D'où la règle qui commande tout ici : l'intention est écrite sur DISQUE avant la moindre
/// tentative réseau. Une extinction en cours d'envoi ne fait perdre que la progression, jamais la
/// décision de semer.
///
/// La file est minuscule et lue rarement : un fichier JSON réécrit atomiquement suffit, et ça
/// reste inspectable à l'œil nu quand quelque chose cloche.
/// </summary>
public sealed class ReplaySeedQueue
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<ReplaySeedQueue> _logger;
    private readonly object _gate = new();

    public ReplaySeedQueue(ILogger<ReplaySeedQueue> logger) => _logger = logger;

    public string Path => System.IO.Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfenet", "seed-queue.json");

    public IReadOnlyList<ReplaySeedIntent> Read()
    {
        lock (_gate) return ReadUnlocked();
    }

    /// <summary>Inscrit l'intention. Idempotent : un replay déjà en file n'est pas dupliqué.</summary>
    public void Enqueue(string replayId, string objectSha256)
    {
        lock (_gate)
        {
            var intents = ReadUnlocked().ToList();
            if (intents.Any(i => string.Equals(i.ReplayId, replayId, StringComparison.Ordinal))) return;
            intents.Add(new ReplaySeedIntent(replayId, objectSha256, DateTime.UtcNow));
            WriteUnlocked(intents);
            _logger.LogInformation("Replay : semis inscrit pour {ReplayId} ({Count} en file).", replayId, intents.Count);
        }
    }

    /// <summary>Le semis a abouti : l'objet est sur l'amorce, il n'y a plus rien à faire.</summary>
    public void Complete(string replayId)
    {
        lock (_gate)
        {
            var intents = ReadUnlocked().Where(i => !string.Equals(i.ReplayId, replayId, StringComparison.Ordinal)).ToList();
            WriteUnlocked(intents);
            _logger.LogInformation("Replay : semis terminé pour {ReplayId}, il reste {Count} en file.", replayId, intents.Count);
        }
    }

    /// <summary>Note une tentative. L'intention RESTE en file : c'est tout l'intérêt.</summary>
    public void Note(string replayId, bool pushed, string? error)
    {
        lock (_gate)
        {
            var intents = ReadUnlocked().Select(i => string.Equals(i.ReplayId, replayId, StringComparison.Ordinal)
                ? i with
                {
                    Attempts = i.Attempts + 1,
                    LastPushUtc = pushed ? DateTime.UtcNow : i.LastPushUtc,
                    LastError = error,
                }
                : i).ToList();
            WriteUnlocked(intents);
        }
    }

    private IReadOnlyList<ReplaySeedIntent> ReadUnlocked()
    {
        try
        {
            if (!File.Exists(Path)) return Array.Empty<ReplaySeedIntent>();
            var doc = JsonSerializer.Deserialize<ReplaySeedQueueDoc>(File.ReadAllBytes(Path), Json);
            return (doc?.Intents ?? new List<ReplaySeedIntent>())
                .Where(i => !string.IsNullOrWhiteSpace(i.ReplayId) && !string.IsNullOrWhiteSpace(i.ObjectSha256))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Replay : file de semis illisible, traitée comme vide.");
            return Array.Empty<ReplaySeedIntent>();
        }
    }

    private void WriteUnlocked(IReadOnlyList<ReplaySeedIntent> intents)
    {
        try
        {
            var path = Path;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            // Écriture atomique : une coupure d'alimentation ne doit jamais laisser une file
            // à moitié écrite, qui serait pire que pas de file du tout.
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(JsonSerializer.SerializeToUtf8Bytes(new ReplaySeedQueueDoc("nelfe.replay.seed-queue.v1", intents), Json));
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Replay : file de semis non enregistrée."); }
    }
}
