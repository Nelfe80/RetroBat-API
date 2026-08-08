using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Canal RAM communautaire : APIExpose va CHERCHER, périodiquement, les définitions
/// <c>.MEM</c> publiées par la communauté sur le dépôt public RetroBat-RAM-Community
/// et les dépose dans le dossier RAM local (<see cref="RetroBatPaths.RamResourcesRoot"/>).
///
/// Rien n'est poussé : c'est un pull HTTPS sortant vers l'API GitHub (publique, sans
/// jeton), en lecture seule. Le dossier officiel livré par l'installer n'est jamais
/// écrasé par défaut (<c>OverwriteExisting = false</c>) — on n'AJOUTE que les jeux
/// absents en local. La synchro est incrémentale : on ne refait rien tant que le
/// commit HEAD du dépôt n'a pas changé.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CommunityRamSyncService : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<CommunityRamSyncService> _logger;

    public CommunityRamSyncService(
        IHttpClientFactory httpFactory,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<CommunityRamSyncService> logger)
    {
        _httpFactory = httpFactory;
        _options = options;
        _logger = logger;
    }

    private static string StatePath => Path.Combine(RetroBatPaths.RamResourcesRoot, ".community-sync.json");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Laisser le démarrage se faire (détection systèmes, déploiements) avant de sortir sur le net.
        try { await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = _options.CurrentValue.CommunityRam;
            if (opt.Enabled)
            {
                try
                {
                    await SyncOnceAsync(opt, stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Canal RAM communautaire : synchronisation échouée (nouvelle tentative au prochain cycle).");
                }
            }

            var hours = Math.Max(1, opt.IntervalHours);
            try { await Task.Delay(TimeSpan.FromHours(hours), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SyncOnceAsync(CommunityRamOptions opt, CancellationToken ct)
    {
        var repo = opt.Repository.Trim();
        var branch = string.IsNullOrWhiteSpace(opt.Branch) ? "main" : opt.Branch.Trim();
        if (repo.Length == 0)
        {
            return;
        }

        var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("APIExpose-CommunityRamSync");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        // 1) Commit HEAD : si inchangé depuis la dernière synchro, rien à faire.
        var head = await GetJsonAsync(client, $"https://api.github.com/repos/{repo}/commits/{branch}", ct);
        var sha = head.TryGetProperty("sha", out var shaEl) ? shaEl.GetString() : null;
        if (string.IsNullOrEmpty(sha))
        {
            _logger.LogWarning("Canal RAM communautaire : impossible de lire le commit HEAD de {Repo}.", repo);
            return;
        }

        var state = LoadState();
        if (string.Equals(state.LastSha, sha, StringComparison.Ordinal))
        {
            _logger.LogDebug("Canal RAM communautaire : déjà à jour ({Sha}).", Short(sha));
            return;
        }

        // 2) Arbre récursif : la liste des fichiers du dépôt à ce commit.
        var tree = await GetJsonAsync(client, $"https://api.github.com/repos/{repo}/git/trees/{sha}?recursive=1", ct);
        if (!tree.TryGetProperty("tree", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        int added = 0, updated = 0, skipped = 0;
        foreach (var item in items.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();

            if (!item.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "blob")
            {
                continue;
            }
            var path = item.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? "" : "";
            if (!path.EndsWith(".MEM", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // Sécurité chemin : on n'accepte QUE <systeme>/<fichier>.MEM (pas de remontée, pas d'absolu).
            var segments = path.Split('/');
            if (segments.Length != 2 || path.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(path))
            {
                continue;
            }

            var local = Path.Combine(RetroBatPaths.RamResourcesRoot, segments[0], segments[1]);
            var exists = File.Exists(local);
            if (exists && !opt.OverwriteExisting)
            {
                // Le dossier officiel livré par l'installer prime : on n'écrase jamais par défaut.
                skipped++;
                continue;
            }

            var escaped = string.Join('/', segments.Select(Uri.EscapeDataString));
            string content;
            try
            {
                content = await client.GetStringAsync($"https://raw.githubusercontent.com/{repo}/{branch}/{escaped}", ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Canal RAM communautaire : téléchargement de {Path} échoué.", path);
                continue;
            }

            if (exists && string.Equals(await File.ReadAllTextAsync(local, ct), content, StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            await File.WriteAllTextAsync(local, content, ct);
            if (exists) { updated++; } else { added++; }
        }

        SaveState(new SyncState { LastSha = sha, LastSyncUtc = DateTime.UtcNow });
        _logger.LogInformation(
            "Canal RAM communautaire : {Added} ajout(s), {Updated} mise(s) à jour, {Skipped} inchangé(s) — {Repo}@{Sha}.",
            added, updated, skipped, repo, Short(sha));
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var resp = await client.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;

    private SyncState LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                return JsonSerializer.Deserialize<SyncState>(File.ReadAllText(StatePath), Json) ?? new SyncState();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Canal RAM communautaire : état de synchro illisible.");
        }
        return new SyncState();
    }

    private void SaveState(SyncState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, Json));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Canal RAM communautaire : état de synchro non écrit.");
        }
    }

    private sealed class SyncState
    {
        public string? LastSha { get; set; }
        public DateTime LastSyncUtc { get; set; }
    }
}
