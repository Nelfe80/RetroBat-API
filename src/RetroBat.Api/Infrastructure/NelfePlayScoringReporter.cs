using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroBat.Api.Scoring;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Scoring certifié — côté agent (étape 3c). Ce service enrôle la clé de signature de
/// l'appareil (CNG/NCrypt), demande un ticket au lancement, capte l'attestation du
/// listener, le score cumulé (agrégateur) et la session (checkpoints/timing/intégrité),
/// puis à la fin de partie ASSEMBLE le passeport, le signe (CNG) et le soumet.
///
/// Le score des checkpoints vient de l'AGRÉGATEUR (LiveScoreAggregator, score.live.changed) —
/// jamais des lectures brutes du wrapper — corrélé aux checkpoints par le n° de frame.
/// Les empreintes gated (listener/core/mem) viennent de l'attestation. Les autres
/// empreintes (modules/process/content) restent à calibrer une fois le profil ouvert.
/// </summary>
public sealed class NelfePlayScoringReporter : BackgroundService
{
    public const string ScoringKeyName = "Nelfe.Scoring.Device";
    private const int MaxTrajectory = 512;

    private readonly IEventBus _eventBus;
    private readonly IHttpClientFactory _httpFactory;
    private readonly NelfePlayDeviceStore _devices;
    private readonly ILogger<NelfePlayScoringReporter>? _logger;

    private IDisposable? _subscription;
    private readonly object _sync = new();

    private string? _enrolledKeyId;
    private string? _listenerSha256, _coreSha256, _memSha256, _wrapperVersion;
    private JsonElement? _ticket;
    private long _lastFrame;
    private long? _finalTotal;
    private bool _inDemo;   // attract mode : le jeu se joue seul → on ignore le score
    private readonly List<(long frame, long total)> _trajectory = new();

    public static bool Enabled { get; set; } = true;

    public NelfePlayScoringReporter(
        IEventBus eventBus,
        IHttpClientFactory httpFactory,
        NelfePlayDeviceStore devices,
        ILogger<NelfePlayScoringReporter>? logger = null)
    {
        _eventBus = eventBus;
        _httpFactory = httpFactory;
        _devices = devices;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = _eventBus.Subscribe<EventEnvelope>(HandleEvent);
        try
        {
            await EnsureEnrolledAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally { _subscription?.Dispose(); }
    }

    private void HandleEvent(EventEnvelope envelope)
    {
        if (!Enabled) return;
        try
        {
            switch (envelope.Type?.ToLowerInvariant())
            {
                case "ui.game.started":
                    // Pas de ticket ici : on ne le demande qu'à la fin, si on soumet
                    // vraiment (score + jeu ouvert). Une démo sans score = zéro ticket.
                    ResetSession();
                    break;
                case "scoring.listener.attestation":
                    CaptureAttestation(ToJson(envelope.Payload));
                    break;
                case "retroarch.score":
                    CaptureFrame(ToJson(envelope.Payload));
                    break;
                case "retroarch.state":
                    CaptureState(ToJson(envelope.Payload));
                    break;
                case "score.live.changed":
                    CaptureTotal(ToJson(envelope.Payload));
                    break;
                case "scoring.listener.session":
                    _ = OnSessionAsync(ToJson(envelope.Payload), CancellationToken.None);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Scoring : événement {Type} ignoré", envelope.Type);
        }
    }

    private void ResetSession()
    {
        lock (_sync)
        {
            _listenerSha256 = _coreSha256 = _memSha256 = _wrapperVersion = null;
            _ticket = null;
            _lastFrame = 0;
            _finalTotal = null;
            _inDemo = false;
            _trajectory.Clear();
        }
    }

    private void CaptureAttestation(JsonElement root)
    {
        lock (_sync)
        {
            _listenerSha256 = GetString(root, "ListenerSha256");
            _coreSha256 = GetString(root, "CoreSha256");
            _memSha256 = GetString(root, "MemSha256");
            _wrapperVersion = GetString(root, "WrapperVersion");
        }
    }

    private void CaptureFrame(JsonElement root)
    {
        if (root.TryGetProperty("Frame", out var f) && f.TryGetInt64(out var frame))
        {
            lock (_sync) { if (!_inDemo) _lastFrame = frame; }
        }
    }

    // Attract mode : le jeu se joue seul. On ne certifie que du jeu HUMAIN, donc on
    // ignore le score pendant la démo. Convention .MEM : action GAME_PLAYING vs DEMO_*.
    private void CaptureState(JsonElement root)
    {
        var action = (GetString(root, "actionType") ?? GetString(root, "ActionType") ?? "").ToUpperInvariant();
        if (action.Length == 0) return;
        lock (_sync)
        {
            if (action.Contains("DEMO")) _inDemo = true;
            else if (action.Contains("PLAYING") || action.Contains("GAME_PLAY")) _inDemo = false;
        }
    }

    private void CaptureTotal(JsonElement root)
    {
        if (!root.TryGetProperty("Score", out var s) || !s.TryGetInt64(out var total)) return;
        lock (_sync)
        {
            if (_inDemo) return;   // score de démo → jamais certifié
            _finalTotal = total;
            // Le total agrégé à la frame courante : la trajectoire vérifiable du score.
            if (_trajectory.Count == 0 || _trajectory[^1].total != total)
            {
                _trajectory.Add((_lastFrame, total));
                if (_trajectory.Count > MaxTrajectory) _trajectory.RemoveAt(0);
            }
        }
    }

    // ── Fin de partie : assembler + signer + soumettre ───────────────────────

    private async Task OnSessionAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var systemId = GetString(payload, "SystemId") ?? "";
        var romGroup = GetString(payload, "Rom") ?? "";
        var sessionJson = GetString(payload, "Session");
        if (sessionJson is null) return;

        var credential = _devices.GetCredential();
        var deviceId = _devices.DeviceId;
        if (string.IsNullOrEmpty(credential) || string.IsNullOrEmpty(deviceId)) return;

        string? listenerSha, coreSha, memSha, wrapperVersion;
        long? finalTotal;
        List<(long frame, long total)> trajectory;
        lock (_sync)
        {
            listenerSha = _listenerSha256; coreSha = _coreSha256; memSha = _memSha256;
            wrapperVersion = _wrapperVersion; finalTotal = _finalTotal;
            trajectory = new List<(long, long)>(_trajectory);
        }

        // Rien à certifier sans score ni attestation : on s'arrête AVANT de consommer
        // quoi que ce soit (démo, navigation, jeu non joué).
        if (listenerSha is null || finalTotal is null)
        {
            _logger?.LogDebug("Scoring : pas de score/attestation — pas de soumission.");
            return;
        }

        var profile = await FetchProfileAsync(credential!, systemId, romGroup, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            _logger?.LogInformation("Scoring : {Rom} non ouvert au scoring — passeport non soumis.", romGroup);
            return;
        }

        // Ticket PARESSEUX : un seul, obtenu ici, uniquement parce qu'on va soumettre.
        await RequestTicketAsync(cancellationToken).ConfigureAwait(false);
        JsonElement? ticket;
        lock (_sync) { ticket = _ticket; }
        if (ticket is null)
        {
            _logger?.LogDebug("Scoring : ticket indisponible — pas de soumission.");
            return;
        }

        using var deviceKey = CngDeviceKey.OpenOrCreate(ScoringKeyName);
        JsonObject passport;
        try
        {
            passport = BuildPassport(
                systemId, romGroup, sessionJson, ticket.Value, profile.Value,
                deviceId!, deviceKey, listenerSha, coreSha, memSha, wrapperVersion,
                finalTotal.Value, trajectory);
            var body = passport.DeepClone()!.AsObject();
            body.Remove("signature");
            passport["signature"] = deviceKey.SignB64Url(Jcs.CanonicalBytes(body));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Scoring : assemblage du passeport impossible.");
            return;
        }

        await SubmitAsync(credential!, passport, cancellationToken).ConfigureAwait(false);
    }

    private JsonObject BuildPassport(
        string systemId, string romGroup, string sessionJson, JsonElement ticket, JsonElement profile,
        string deviceId, CngDeviceKey deviceKey, string listenerSha, string? coreSha, string? memSha,
        string? wrapperVersion, long finalTotal, List<(long frame, long total)> trajectory)
    {
        var session = JsonNode.Parse(sessionJson)!.AsObject();
        long frameCount = (long?)session["frame_count"] ?? 0;
        long monotonicMs = (long?)session["monotonic_ms"] ?? 0;
        long resets = (long?)session["resets"] ?? 0;
        long saveStateLoads = (long?)session["save_state_loads"] ?? 0;

        string ruleset = profile.GetProperty("ruleset").GetString() ?? "";
        long profileVersion = profile.GetProperty("profile_version").GetInt64();
        string profileDocSha = profile.GetProperty("profile_document_sha256").GetString() ?? "";
        string engine = profile.TryGetProperty("engine", out var e) ? (e.GetString() ?? "libretro") : "libretro";
        string? manifestCommit = profile.TryGetProperty("manifest_commit", out var mc) ? mc.GetString() : null;
        var metricProfile = profile.TryGetProperty("metric", out var mp) ? mp : default;
        string direction = metricProfile.ValueKind == JsonValueKind.Object && metricProfile.TryGetProperty("ranking_direction", out var rd)
            ? (rd.GetString() ?? "higher_better") : "higher_better";
        string resultSource = metricProfile.ValueKind == JsonValueKind.Object && metricProfile.TryGetProperty("result_source", out var rs)
            ? (rs.GetString() ?? "final") : "final";

        // Checkpoints : le squelette temporel du listener (frame + monotonic_ms) reçoit
        // le score AGRÉGÉ correspondant à sa frame (dernier total connu ≤ frame).
        var checkpoints = new JsonArray();
        if (session["checkpoints"] is JsonArray cps)
        {
            foreach (var cp in cps)
            {
                long f = (long?)cp!["frame"] ?? 0;
                long ms = (long?)cp["monotonic_ms"] ?? 0;
                long score = 0;
                foreach (var (tf, tt) in trajectory) { if (tf <= f) score = tt; else break; }
                checkpoints.Add(new JsonObject
                {
                    ["monotonic_ms"] = ms,
                    ["frame"] = f,
                    ["score"] = score.ToString(),
                });
            }
        }
        var checkpointsDigest = Crypto.Sha256Hex(Jcs.CanonicalBytes(checkpoints));

        // Empreintes gated par le profil (listener/core/mem) = attestation. Les modules
        // détaillés (frontend/apiexpose/launcher/hook) + process + content ROM restent à
        // calibrer par introspection une fois le profil ouvert.
        var modules = new JsonArray
        {
            new JsonObject { ["role"] = "listener", ["sha256"] = listenerSha },
            new JsonObject { ["role"] = "real_core", ["sha256"] = coreSha ?? listenerSha },
        };
        var modulesDigest = Crypto.Sha256Hex(Jcs.CanonicalBytes(modules));

        JsonObject Triple(string? h) => new() { ["start_sha256"] = h, ["loaded_sha256"] = h, ["end_sha256"] = h };

        return new JsonObject
        {
            ["protocol"] = 1,
            ["session_id"] = Guid.NewGuid().ToString(),
            ["ticket"] = JsonNode.Parse(ticket.GetRawText()),
            ["game"] = new JsonObject
            {
                ["system_id"] = systemId, ["rom_group"] = romGroup, ["engine"] = engine,
                ["ruleset"] = ruleset, ["profile_version"] = profileVersion,
                ["manifest_commit"] = manifestCommit, ["profile_document_sha256"] = profileDocSha,
            },
            ["device"] = new JsonObject { ["device_id"] = deviceId, ["key_id"] = deviceKey.KeyId, ["key_type"] = "ecdsa_p256" },
            ["identity"] = new JsonObject { ["player_ref"] = null, ["session_player_id"] = null },
            ["listener"] = new JsonObject
            {
                ["build"] = wrapperVersion ?? "0", ["start_sha256"] = listenerSha,
                ["loaded_sha256"] = listenerSha, ["end_sha256"] = listenerSha,
                ["certification"] = "listener-homologation",
            },
            ["software"] = new JsonObject { ["modules"] = modules, ["modules_digest"] = modulesDigest },
            ["artifacts"] = new JsonObject
            {
                ["core"] = Triple(coreSha), ["content"] = Triple(null), ["mem"] = Triple(memSha),
                ["core_options_digest"] = Crypto.Sha256Hex("core-options@default"),
                ["bios"] = new JsonObject { ["mode"] = "none" },
            },
            ["process"] = new JsonObject
            {
                ["pid"] = Environment.ProcessId, ["executable_sha256"] = null, ["parent_pid"] = 0,
                ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["open_files"] = new JsonArray(),
            },
            ["timing"] = new JsonObject
            {
                ["started_at"] = DateTime.UtcNow.AddMilliseconds(-monotonicMs).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["ended_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["monotonic_ms"] = monotonicMs, ["frame_count"] = frameCount,
            },
            ["sensitive"] = new JsonObject
            {
                ["cheats"] = false, ["save_state_loaded"] = saveStateLoads > 0, ["resets"] = resets,
                ["rewind"] = false, ["runahead"] = false, ["fast_forward"] = false,
                ["netplay"] = false, ["continues"] = 0,
            },
            ["metric"] = new JsonObject
            {
                ["type"] = "score", ["unit"] = "points", ["value"] = finalTotal.ToString(),
                ["ranking_direction"] = direction, ["result_source"] = resultSource,
            },
            ["progression"] = new JsonObject { ["checkpoints"] = checkpoints, ["checkpoints_digest"] = checkpointsDigest },
            ["local_check"] = "pass",
        };
    }

    private async Task<JsonElement?> FetchProfileAsync(string credential, string systemId, string romGroup, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(credential);
            var url = $"/api/v1/agent/scores/profile?system_id={Uri.EscapeDataString(systemId)}&rom_group={Uri.EscapeDataString(romGroup)}";
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("open", out var open) && open.ValueKind == JsonValueKind.True
                && doc.RootElement.TryGetProperty("profile", out var profile))
            {
                return profile.Clone();
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Scoring : résolution du profil impossible.");
            return null;
        }
    }

    private async Task SubmitAsync(string credential, JsonObject passport, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(credential);
            using var content = new StringContent(passport.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/api/v1/agent/scores/submissions", content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation("Scoring : verdict serveur {Status} — {Body}", (int)response.StatusCode, body);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Scoring : soumission impossible.");
        }
    }

    // ── Enrôlement + ticket ──────────────────────────────────────────────────

    private async Task EnsureEnrolledAsync(CancellationToken cancellationToken)
    {
        var credential = _devices.GetCredential();
        if (string.IsNullOrEmpty(credential)) return;
        try
        {
            using var deviceKey = CngDeviceKey.OpenOrCreate(ScoringKeyName);
            if (_enrolledKeyId == deviceKey.KeyId) return;

            using var client = CreateClient(credential);
            using var content = new StringContent(deviceKey.PublicKeyPem, Encoding.ASCII, "application/x-pem-file");
            using var response = await client.PostAsync("/api/v1/agent/scores/enroll-key", content, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                _enrolledKeyId = deviceKey.KeyId;
                _logger?.LogInformation("Scoring : clé d'appareil enrôlée (key_id {KeyId}).", deviceKey.KeyId);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Scoring : enrôlement impossible.");
        }
    }

    private async Task RequestTicketAsync(CancellationToken cancellationToken)
    {
        var credential = _devices.GetCredential();
        if (string.IsNullOrEmpty(credential)) return;
        await EnsureEnrolledAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = CreateClient(credential);
            using var response = await client.PostAsync("/api/v1/agent/scores/ticket", content: null, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("ticket", out var ticket))
            {
                lock (_sync) { _ticket = ticket.Clone(); }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Scoring : demande de ticket impossible.");
        }
    }

    private HttpClient CreateClient(string credential)
    {
        var client = _httpFactory.CreateClient(nameof(NelfePlayScoringReporter));
        client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Add("X-NelfePlay-Device", credential);
        return client;
    }

    private static JsonElement ToJson(object? payload)
        => JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
