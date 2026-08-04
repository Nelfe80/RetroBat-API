using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroBat.Api.Media;
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
    private readonly ClaimOverlayService? _claimOverlay;
    private readonly NelfePlayScoringSessionService? _scoringSession;
    private readonly ILogger<NelfePlayScoringReporter>? _logger;

    private IDisposable? _subscription;
    private readonly object _sync = new();

    private string? _enrolledKeyId;
    private string? _listenerSha256, _coreSha256, _memSha256, _contentSha256, _wrapperVersion;
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
        ClaimOverlayService? claimOverlay = null,
        NelfePlayScoringSessionService? scoringSession = null,
        ILogger<NelfePlayScoringReporter>? logger = null)
    {
        _eventBus = eventBus;
        _httpFactory = httpFactory;
        _devices = devices;
        _claimOverlay = claimOverlay;
        _scoringSession = scoringSession;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = _eventBus.Subscribe<EventEnvelope>(HandleEvent);
        try
        {
            await EnsureEnrolledAsync(stoppingToken).ConfigureAwait(false);

            // La mesure du score est ÉVÉNEMENTIELLE (pipe → HandleEvent). En fond, un
            // battement calme vérifie l'état recovery « share datas » : si le serveur
            // reconstruit sa base, cette machine re-verse ses records auto-conservés.
            // Marche pour les machines ANONYMES (contrairement à /agent/work lié à un
            // compte) car ResolveCredential retombe sur le credential anonyme.
            var delay = TimeSpan.FromSeconds(20); // premier contrôle peu après le boot
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                await RecoveryCheckAsync(stoppingToken).ConfigureAwait(false);
                await ClaimCheckAsync(stoppingToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(180);
            }
        }
        catch (OperationCanceledException) { }
        finally { _subscription?.Dispose(); }
    }

    /// <summary>
    /// Battement recovery : interroge l'état « share datas » (endpoint public, sans SQL)
    /// et, s'il est armé, re-verse les records auto-conservés. On n'agit JAMAIS
    /// spontanément — uniquement quand l'admin a explicitement armé une reconstruction.
    /// </summary>
    private async Task RecoveryCheckAsync(CancellationToken cancellationToken)
    {
        if (!Enabled) return;
        var credential = ResolveCredential();
        if (string.IsNullOrEmpty(credential)) return;
        try
        {
            using var client = CreateClient(credential);
            using var response = await client.GetAsync("/api/v1/scores/recovery-status", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var root = JsonNode.Parse(body) as JsonObject;
            var contribute = (bool?)root?["contribute"] ?? false;
            if (!contribute) return;

            // NOUVEL ÉPISODE : une époque inédite (nouvel armement admin) → on RÉ-ARME les
            // records déjà versés (*.sent → *.json) pour qu'une NOUVELLE récupération les
            // re-verse aussi. Le .sent ne vaut donc que POUR l'épisode courant. L'époque est
            // persistée pour survivre à un redémarrage au milieu d'un même épisode.
            var epoch = (string?)root?["epoch"] ?? "";
            if (!string.IsNullOrEmpty(epoch) && epoch != ReadLastEpoch())
            {
                RearmSentFiles(CertifiedDir());
                WriteLastEpoch(epoch);
            }

            await ContributeCertifiedAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace($"recovery-check échec : {ex.Message}");
        }
    }

    /// <summary>
    /// À l'identification de la machine (appairée), rattache ses scores ANONYMES au
    /// compte. La machine prouve qu'elle possède les deux credentials (appairé pour
    /// l'auth, anonyme dans le corps). Une seule fois (marqueur claimed.flag).
    /// </summary>
    private async Task ClaimCheckAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || !_devices.IsPaired) return;
        var paired = _devices.GetCredential();
        if (string.IsNullOrEmpty(paired)) return;

        var flag = System.IO.Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "claimed.flag");
        if (System.IO.File.Exists(flag)) return;

        var anon = ReadAnonymousCredential();
        if (string.IsNullOrEmpty(anon))
        {
            try { System.IO.File.WriteAllText(flag, "no-anon"); } catch { }
            return; // rien d'anonyme à réclamer
        }

        try
        {
            using var client = CreateClient(paired);
            var body = new JsonObject { ["anonymous_credential"] = anon };
            using var content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json");
            using var response = await client.PostAsync("/api/v1/agent/scores/claim", content, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var b = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                Trace($"claim scores anonymes : {b}");
                try { System.IO.File.WriteAllText(flag, DateTime.UtcNow.ToString("o")); } catch { }
            }
        }
        catch (Exception ex)
        {
            Trace($"claim échec : {ex.Message}");
        }
    }

    private static string? ReadAnonymousCredential()
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "anonymous.json");
            if (System.IO.File.Exists(path))
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("credential", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    return c.GetString();
                }
            }
        }
        catch { }
        return null;
    }

    private static string CertifiedDir() =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "certified");

    private static string EpochFile() =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "recovery-epoch.txt");

    private static string ReadLastEpoch()
    {
        try { return System.IO.File.Exists(EpochFile()) ? System.IO.File.ReadAllText(EpochFile()).Trim() : ""; }
        catch { return ""; }
    }

    private static void WriteLastEpoch(string epoch)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(EpochFile())!);
            System.IO.File.WriteAllText(EpochFile(), epoch, new UTF8Encoding(false));
        }
        catch { /* best-effort : au pire on ré-arme une fois de trop, sans dommage (idempotent) */ }
    }

    /// <summary>Ré-arme les records d'un épisode précédent : *.sent → *.json.</summary>
    private void RearmSentFiles(string dir)
    {
        if (!System.IO.Directory.Exists(dir)) return;
        var n = 0;
        foreach (var sent in System.IO.Directory.EnumerateFiles(dir, "*.sent"))
        {
            try { System.IO.File.Move(sent, sent[..^5], overwrite: true); n++; } catch { /* ignore */ }
        }
        if (n > 0) Trace($"recovery : {n} record(s) ré-armé(s) (nouvel épisode).");
    }

    /// <summary>
    /// Re-verse les passeports auto-conservés dans certified/ vers le serveur en
    /// reconstruction. Chaque record REPASSE le pipeline vérifié (signature + règles) et
    /// est idempotent (déjà présent = duplicate). On marque le fichier .sent après envoi
    /// pour ne pas le renvoyer ; un échec transport le laisse pour le prochain battement.
    /// </summary>
    private async Task ContributeCertifiedAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var dir = CertifiedDir();
        if (!System.IO.Directory.Exists(dir)) return;

        var sent = 0;
        foreach (var path in System.IO.Directory.EnumerateFiles(dir, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string body;
            try { body = await System.IO.File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false); }
            catch { continue; }

            using var content = new StringContent(body, new UTF8Encoding(false), "application/json");
            using var response = await client.PostAsync("/api/v1/agent/scores/contribute", content, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
            {
                try { System.IO.File.Move(path, path + ".sent", overwrite: true); } catch { /* on retentera */ }
                sent++;
            }
        }

        if (sent > 0)
        {
            Trace($"recovery : {sent} record(s) auto-conservé(s) re-versé(s).");
            _logger?.LogInformation("Scoring : {Count} record(s) re-versé(s) (recovery « share datas »).", sent);
        }
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
                    // Une nouvelle partie retire aussitôt une éventuelle surimpression
                    // de réclamation restée à l'écran.
                    _claimOverlay?.HideNow();
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
            _listenerSha256 = _coreSha256 = _memSha256 = _contentSha256 = _wrapperVersion = null;
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
            _contentSha256 = GetString(root, "ContentSha256");
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
        Trace($"session reçue sys={systemId} rom={romGroup} sessionLen={sessionJson?.Length ?? -1}");
        if (sessionJson is null) return;

        // Appairé OU anonyme : le scoring accepte les deux (anonyme = score « anonyme »).
        var credential = ResolveCredential();
        if (string.IsNullOrEmpty(credential))
        {
            Trace("STOP: pas de credential (ni appairé ni anonyme)");
            return;
        }

        string? listenerSha, coreSha, memSha, contentSha, wrapperVersion;
        long? finalTotal;
        List<(long frame, long total)> trajectory;
        lock (_sync)
        {
            listenerSha = _listenerSha256; coreSha = _coreSha256; memSha = _memSha256;
            contentSha = _contentSha256; wrapperVersion = _wrapperVersion; finalTotal = _finalTotal;
            trajectory = new List<(long, long)>(_trajectory);
        }

        // Rien à certifier sans score ni attestation : on s'arrête AVANT de consommer
        // quoi que ce soit (démo, navigation, jeu non joué).
        Trace($"état: listener={listenerSha is not null} core={coreSha is not null} content={contentSha is not null} finalTotal={finalTotal} trajPts={trajectory.Count} inDemo={_inDemo}");
        if (listenerSha is null || finalTotal is null)
        {
            Trace("STOP: pas de score/attestation");
            return;
        }

        var profile = await FetchProfileAsync(credential!, systemId, romGroup, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            Trace($"STOP: profil {romGroup} non ouvert (fetch null)");
            return;
        }

        // Ticket PARESSEUX : un seul, obtenu ici, uniquement parce qu'on va soumettre.
        await RequestTicketAsync(cancellationToken).ConfigureAwait(false);
        JsonElement? ticket;
        lock (_sync) { ticket = _ticket; }
        if (ticket is null)
        {
            Trace("STOP: ticket indisponible");
            return;
        }
        // L'identité de l'appareil vient du ticket (résolue par le serveur : appairé ou
        // anonyme) — l'agent n'a pas besoin de la connaître lui-même.
        var deviceId = ticket.Value.TryGetProperty("device_id", out var did) ? did.GetString() : null;
        if (string.IsNullOrEmpty(deviceId))
        {
            Trace("STOP: ticket sans device_id");
            return;
        }
        Trace($"assemblage du passeport (device={deviceId})…");

        using var deviceKey = CngDeviceKey.OpenOrCreate(ScoringKeyName);
        JsonObject passport;
        try
        {
            passport = BuildPassport(
                systemId, romGroup, sessionJson, ticket.Value, profile.Value,
                deviceId!, deviceKey, listenerSha, coreSha, memSha, contentSha, wrapperVersion,
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
        string? contentSha, string? wrapperVersion, long finalTotal, List<(long frame, long total)> trajectory)
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

        // Checkpoints : squelette temporel du listener (frame + monotonic_ms) + le score
        // AGRÉGÉ à cette frame (dernier total ≤ frame) porté dans `metric` (string). Le
        // dernier checkpoint porte l'événement `game_end` (exigé par le vérifieur), et son
        // metric doit égaler metric.value (result_source=final).
        var checkpoints = new JsonArray();
        var srcCps = session["checkpoints"] as JsonArray ?? new JsonArray();
        for (var i = 0; i < srcCps.Count; i++)
        {
            long f = (long?)srcCps[i]!["frame"] ?? 0;
            long ms = (long?)srcCps[i]!["monotonic_ms"] ?? 0;
            long score = 0;
            foreach (var (tf, tt) in trajectory) { if (tf <= f) score = tt; else break; }
            var last = i == srcCps.Count - 1;
            var node = new JsonObject { ["monotonic_ms"] = ms, ["frame"] = f, ["metric"] = (last ? finalTotal : score).ToString() };
            if (last) node["event"] = "game_end";
            checkpoints.Add(node);
        }
        // Filet : le vérifieur exige au moins un checkpoint game_end.
        if (checkpoints.Count == 0)
        {
            checkpoints.Add(new JsonObject { ["monotonic_ms"] = monotonicMs, ["frame"] = frameCount, ["metric"] = finalTotal.ToString(), ["event"] = "game_end" });
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

        // MONDE de la partie : la session le porte.
        //  - STATION : joueur checké-in en salle par le hub → provenance salle (nom/ville).
        //  - STREAM  : participation à un contest de streamer (salle OU maison) → chaîne +
        //    contest_id. Armée par APIExpose (enrôlement contest) ou par le hub (borne).
        //  - HOME    : aucune session (machine perso / borne libre).
        // Le record est attribué au code joueur (RGPC) de la session. Champs ADDITIFS et
        // SIGNÉS (JCS re-trie ; un vérifieur qui les ignore reste valide).
        var sessionPlayer = _scoringSession?.Get();
        var world = sessionPlayer?.World ?? "home";
        JsonObject? contextVenue = sessionPlayer is not null
            && (sessionPlayer.VenueName is not null || sessionPlayer.VenueCity is not null)
            ? new JsonObject { ["name"] = sessionPlayer.VenueName, ["city"] = sessionPlayer.VenueCity }
            : null;

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
            ["identity"] = new JsonObject { ["player_ref"] = null, ["session_player_id"] = sessionPlayer?.PlayerCode },
            ["context"] = new JsonObject
            {
                ["world"] = world,
                ["venue"] = contextVenue,
                ["channel"] = sessionPlayer?.Channel,
                ["contest_id"] = sessionPlayer?.ContestId,
            },
            ["listener"] = new JsonObject
            {
                ["build"] = wrapperVersion ?? "0", ["start_sha256"] = listenerSha,
                ["loaded_sha256"] = listenerSha, ["end_sha256"] = listenerSha,
                ["certification"] = "listener-homologation",
            },
            ["software"] = new JsonObject { ["modules"] = modules, ["modules_digest"] = modulesDigest },
            ["artifacts"] = new JsonObject
            {
                ["core"] = Triple(coreSha), ["content"] = Triple(contentSha), ["mem"] = Triple(memSha),
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
            Trace($"VERDICT HTTP {(int)response.StatusCode} — {body}");
            _logger?.LogInformation("Scoring : verdict serveur {Status} — {Body}", (int)response.StatusCode, body);
            PersistCertified(passport, body);
            MaybeShowClaimOverlay(passport, body);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Scoring : soumission impossible.");
        }
    }

    /// <summary>
    /// Score anonyme PUBLIÉ → le verdict porte un <c>claim_code</c> : on affiche la
    /// surimpression « Réclame ton record ! » sur l'écran de la machine. Une machine
    /// appairée/liée soumet un score attribué (non anonyme) : pas de code, donc pas
    /// d'overlay — le gating par identité est implicite côté serveur.
    /// </summary>
    private void MaybeShowClaimOverlay(JsonObject passport, string responseBody)
    {
        if (_claimOverlay is null)
        {
            return;
        }

        try
        {
            var obj = JsonNode.Parse(responseBody) as JsonObject;
            var code = (string?)(obj?["claim_code"]);
            if (string.IsNullOrWhiteSpace(code))
            {
                return;
            }

            var game = passport["game"] as JsonObject;
            var systemId = (string?)(game?["system_id"]) ?? "";
            var ruleset = (string?)(game?["ruleset"]) ?? "";
            var scoreText = (string?)((passport["metric"] as JsonObject)?["value"]) ?? "0";
            _ = long.TryParse(scoreText, out var score);

            int? rank = null;
            if (obj?["rank"] is JsonValue rv && rv.TryGetValue<int>(out var r))
            {
                rank = r;
            }

            _ = _claimOverlay.ShowAsync(systemId, ruleset, score, code!, rank);
        }
        catch (Exception ex)
        {
            Trace($"overlay claim non affiché : {ex.Message}");
        }
    }

    /// <summary>
    /// SELF-CUSTODY : conserve localement le passeport SIGNÉ + le verdict serveur dans
    /// <c>state/nelfeplay/certified/{session_id}.json</c>. C'est la sauvegarde distribuée
    /// de la flotte : en cas de recovery serveur (mode « contribute »), cette machine
    /// re-verse ses propres exploits, chacun re-vérifiable (signature d'appareil + OTS).
    /// Le joueur DÉTIENT ses records — rien ne dépend d'un seul serveur.
    /// </summary>
    private void PersistCertified(JsonObject passport, string responseBody)
    {
        try
        {
            var sessionId = (string?)passport["session_id"];
            if (string.IsNullOrEmpty(sessionId)) return;

            string? verdict = null;
            try
            {
                var obj = JsonNode.Parse(responseBody) as JsonObject;
                verdict = (string?)(obj?["status"] ?? obj?["verdict"]); // le serveur renvoie `status`
            }
            catch { /* corps non-JSON : on garde le passeport quand même */ }

            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "certified");
            System.IO.Directory.CreateDirectory(dir);
            var record = new JsonObject
            {
                ["session_id"] = sessionId,
                ["verdict"] = verdict,
                ["submitted_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["server_response"] = responseBody,
                ["passport"] = passport.DeepClone(),
            };
            var path = System.IO.Path.Combine(dir, sessionId + ".json");
            // SANS BOM : l'archive doit être universellement parsable (json_decode PHP,
            // outils tiers, miroirs). Encoding.UTF8 écrirait un BOM qui les fait échouer.
            System.IO.File.WriteAllText(path, record.ToJsonString(), new UTF8Encoding(false));
            Trace($"certified/ écrit : {sessionId}.json (verdict={verdict ?? "?"})");
        }
        catch (Exception ex)
        {
            Trace($"certified/ échec : {ex.Message}");
        }
    }

    // ── Enrôlement + ticket ──────────────────────────────────────────────────

    private async Task EnsureEnrolledAsync(CancellationToken cancellationToken)
    {
        var credential = ResolveCredential();
        if (string.IsNullOrEmpty(credential)) { Trace($"enroll: pas de credential (paired={_devices.IsPaired})"); return; }
        try
        {
            using var deviceKey = CngDeviceKey.OpenOrCreate(ScoringKeyName);
            if (_enrolledKeyId == deviceKey.KeyId) return;

            using var client = CreateClient(credential);
            using var content = new StringContent(deviceKey.PublicKeyPem, Encoding.ASCII, "application/x-pem-file");
            using var response = await client.PostAsync("/api/v1/agent/scores/enroll-key", content, cancellationToken).ConfigureAwait(false);
            Trace($"enroll HTTP {(int)response.StatusCode} key_id={deviceKey.KeyId}");
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
        var credential = ResolveCredential();
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

    // Le secret à présenter : celui de l'appareil APPAIRÉ, sinon celui de l'install
    // ANONYME (déjà enregistrée par NelfePlayPlayReporter dans anonymous.json).
    private string? ResolveCredential()
    {
        var paired = _devices.GetCredential();
        if (!string.IsNullOrEmpty(paired)) return paired;
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "anonymous.json");
            if (System.IO.File.Exists(path))
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("credential", out var c) && c.ValueKind == JsonValueKind.String)
                    return c.GetString();
            }
        }
        catch { }
        return null;
    }

    private HttpClient CreateClient(string credential)
    {
        var client = _httpFactory.CreateClient(nameof(NelfePlayScoringReporter));
        client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Add("X-NelfePlay-Device", credential);
        return client;
    }

    // Trace de diagnostic best-effort (le log ILogger n'a pas de sink fichier ici).
    private static void Trace(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nelfe_scoring.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
        }
        catch { }
    }

    private static JsonElement ToJson(object? payload)
        => JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
