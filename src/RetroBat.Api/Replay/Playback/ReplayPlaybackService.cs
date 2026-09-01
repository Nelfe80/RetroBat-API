using System.Diagnostics;
using System.Globalization;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Runtime;
using RetroBat.Api.Replay.Storage;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Playback;

/// <summary>
/// Player Replay (R2). Résout un replay -> vérifie le runtime -> lance RetroArch en lecture
/// (-P &lt;objet&gt;) avec le VRAI core (cores_real/, pas le wrapper de scoring, pour un playback
/// déterministe SANS armer de session de score) -> suit la frame via active_replay -> fin propre.
/// Singleton, une seule lecture active à la fois.
/// </summary>
public sealed class ReplayPlaybackService
{
    private readonly RetroArchReplayClient _ra;
    private readonly ReplayStore _store;
    private readonly IEventBus _bus;
    private readonly ILogger<ReplayPlaybackService> _logger;

    private readonly object _gate = new();
    private ReplayPlaybackState _state = ReplayPlaybackState.Idle;
    private string? _replayId;
    private long _frame;
    private long? _runStart, _runEnd, _replayEnd;
    private double _nominalFps = 60;
    private bool _paused;
    private ReplayErrorCode _error = ReplayErrorCode.None;
    private ReplayCard? _card;
    private Process? _process;
    private CancellationTokenSource? _monitorCts;

    public ReplayPlaybackService(RetroArchReplayClient ra, ReplayStore store, IEventBus bus,
        ILogger<ReplayPlaybackService> logger)
    {
        _ra = ra; _store = store; _bus = bus; _logger = logger;
    }

    /// <summary>Vrai pendant qu'une lecture est en cours (le recorder s'abstient d'enregistrer).</summary>
    public bool IsBusy
    {
        get { lock (_gate) return _state is ReplayPlaybackState.Resolving or ReplayPlaybackState.Verifying
            or ReplayPlaybackState.Preparing or ReplayPlaybackState.Launching or ReplayPlaybackState.Playing
            or ReplayPlaybackState.Paused or ReplayPlaybackState.Stopping; }
    }

    public sealed record PlayResult(bool Accepted, string State, ReplayErrorCode Error);
    public sealed record StateSnapshot(string Mode, string State, string? ReplayId, long Frame,
        long? RunStartFrame, long? RunEndFrame, long? ReplayEndFrame, bool Paused, string? Error,
        double NominalFps, ReplayCard? Card);

    /// <summary>Fiche « performance NelfePlay » de l'overlay (record sportif/esport). En R1
    /// seuls Game/System/Date sont réels ; Player/Score/Rank/Certified sont des emplacements
    /// (à brancher au record + au lien scoring, lot ultérieur).</summary>
    public sealed record ReplayCard(string Game, string System, string DateText, string Player,
        long? Score, int? Rank, bool Certified);

    public StateSnapshot GetState()
    {
        lock (_gate)
        {
            var mode = _state is ReplayPlaybackState.Idle ? "none" : "replay";
            return new StateSnapshot(mode, _state.ToString().ToLowerInvariant(), _replayId, _frame,
                _runStart, _runEnd, _replayEnd, _paused,
                _error == ReplayErrorCode.None ? null : _error.ToString(),
                _nominalFps <= 0 ? 60 : _nominalFps, _card);
        }
    }

    public async Task<PlayResult> PlayAsync(string replayId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (IsBusy) return new PlayResult(false, _state.ToString().ToLowerInvariant(), ReplayErrorCode.ReplayAlreadyRunning);
            _state = ReplayPlaybackState.Resolving; _replayId = replayId; _error = ReplayErrorCode.None;
            _frame = 0; _paused = false; _card = null;
        }

        var manifest = _store.GetManifest(replayId);
        if (manifest is null) return Fail(ReplayErrorCode.ReplayNotFound);
        var meta = _store.GetMeta(replayId);
        lock (_gate) _card = BuildCard(manifest, meta);
        var hint = meta?.Launch;
        var objectPath = _store.ObjectPath(manifest.Object.Sha256);
        if (!File.Exists(objectPath)) return Fail(ReplayErrorCode.ReplayObjectUnavailable);

        // ── vérification runtime (R2 MVP : présence ; empreintes strictes quand le manifeste les portera) ──
        lock (_gate) { _state = ReplayPlaybackState.Verifying; _replayEnd = manifest.Frames.ReplayEnd;
            _runStart = manifest.Frames.RunStart; _runEnd = manifest.Frames.RunEnd; _nominalFps = manifest.Frames.NominalFps; }
        if (hint is null) return Fail(ReplayErrorCode.RuntimeIncompatible);
        var coreDll = ResolveRealCore(hint.Core) ?? hint.CoreDll;
        if (!File.Exists(coreDll)) return Fail(ReplayErrorCode.CoreNotFound);
        if (string.IsNullOrEmpty(hint.RomPath) || !File.Exists(hint.RomPath)) return Fail(ReplayErrorCode.RomNotFound);

        // ── pas de jeu déjà en cours (on lance notre propre RetroArch) ──
        var status = await _ra.GetStatusAsync(ct).ConfigureAwait(false);
        if (status is { ContentLoaded: true }) return Fail(ReplayErrorCode.GameAlreadyRunning);

        // ── ReplayLaunchProfile : neutralise les hotkeys gamepad de RetroArch le temps de la
        //    lecture (sinon SELECT = input_enable_hotkey, et SELECT+bouton ouvre le menu RA au
        //    lieu d'aller à notre routeur). config_save_on_exit=false => AUCUNE persistance :
        //    la config normale de l'utilisateur reste intacte. Appliqué via --appendconfig. ──
        var sessionCfg = Path.Combine(_store.TempRoot, "replay-session.cfg");
        try
        {
            File.WriteAllText(sessionCfg, string.Join('\n', new[]
            {
                "config_save_on_exit = \"false\"",
                "input_menu_toggle_btn = \"nul\"",
                "input_exit_emulator_btn = \"nul\"",
                "input_menu_toggle_gamepad_combo = \"0\"",
                "input_quit_gamepad_combo = \"0\"",
                // NB : la vitesse de lecture est NORMALE à l'écran (confirmé visuellement) ; le
                // compteur active_replay.frame avance ~2x mais c'est une sémantique interne, pas la
                // cadence réelle. Aucun override de cadence n'est donc nécessaire.
            }) + "\n");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Replay : écriture du profil de session échouée (hotkeys non neutralisées)"); }

        // ── lancement ──
        lock (_gate) _state = ReplayPlaybackState.Launching;
        var exe = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "retroarch.exe");
        var psi = new ProcessStartInfo { FileName = exe, WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false };
        psi.ArgumentList.Add("-L"); psi.ArgumentList.Add(coreDll);
        psi.ArgumentList.Add(hint.RomPath);
        psi.ArgumentList.Add("-P"); psi.ArgumentList.Add(objectPath);
        psi.ArgumentList.Add("--config"); psi.ArgumentList.Add(RetroBatPaths.RetroArchConfigPath);
        psi.ArgumentList.Add("--appendconfig"); psi.ArgumentList.Add(sessionCfg);
        psi.ArgumentList.Add("--eof-exit");

        Process proc;
        try { proc = Process.Start(psi)!; }
        catch (Exception ex) { _logger.LogWarning(ex, "Replay : lancement RetroArch échoué"); return Fail(ReplayErrorCode.RetroArchUnavailable); }
        lock (_gate) _process = proc;
        _logger.LogInformation("Replay : lecture lancée {ReplayId} (core={Core}, rom={Rom}).", replayId, Path.GetFileName(coreDll), Path.GetFileName(hint.RomPath));
        await Publish("replay.launching", new { replayId }).ConfigureAwait(false);

        // ── attente du démarrage effectif de la lecture (active_replay flags=4), timeout 15 s ──
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (proc.HasExited) return Fail(ReplayErrorCode.RetroArchUnavailable);
            var active = await _ra.GetActiveReplayAsync(ct).ConfigureAwait(false);
            if (active is { Playing: true }) { lock (_gate) { _state = ReplayPlaybackState.Playing; _frame = active.Frame; } break; }
            try { await Task.Delay(500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
        if (GetState().State != "playing")
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            return Fail(ReplayErrorCode.ReplayLaunchTimeout);
        }

        StartMonitor();
        await Publish("replay.started", new { replayId }).ConfigureAwait(false);
        return new PlayResult(true, "playing", ReplayErrorCode.None);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        Process? proc;
        lock (_gate)
        {
            if (_state is ReplayPlaybackState.Idle) return;
            _state = ReplayPlaybackState.Stopping; proc = _process;
        }
        _monitorCts?.Cancel();
        await _ra.HaltAsync(ct).ConfigureAwait(false);
        try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
        lock (_gate) { _process = null; _replayId = null; _state = ReplayPlaybackState.Idle; _frame = 0; _card = null; }
        await Publish("replay.finished", new { reason = "user" }).ConfigureAwait(false);
    }

    // ── commandes de contrôle (appelées par le routeur panel R3 ou l'API) ──
    public async Task PauseToggleAsync(CancellationToken ct)
    {
        if (!IsBusy) return;
        await _ra.PauseToggleAsync(ct).ConfigureAwait(false); // _paused sera relu par le monitor
    }

    public async Task SeekRelativeAsync(double seconds, CancellationToken ct)
    {
        if (!IsBusy) return;
        long cur, hi; long? loRun; double fps;
        lock (_gate) { cur = _frame; hi = _replayEnd ?? 0; loRun = _runStart; fps = _nominalFps <= 0 ? 60 : _nominalFps; }
        var target = cur + (long)Math.Round(seconds * fps);
        var lo = loRun ?? 0;
        if (target < lo) target = lo;
        if (hi > 0 && target > hi) target = hi;
        var resp = await _ra.SeekAsync(target, ct).ConfigureAwait(false);
        if (resp is not null && resp.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            lock (_gate) _frame = target; // approx ; le monitor recalera sur active_replay
        else
            _logger.LogDebug("Replay : SEEK {Target} refusé ({Resp})", target, resp);
    }

    public async Task NextCheckpointAsync(CancellationToken ct)
    {
        if (!IsBusy) return;
        await _ra.NextCheckpointAsync(ct).ConfigureAwait(false);
    }

    public async Task RestartRunAsync(CancellationToken ct)
    {
        if (!IsBusy) return;
        long target; lock (_gate) target = _runStart ?? 0;
        await _ra.SeekAsync(target, ct).ConfigureAwait(false);
        lock (_gate) _frame = target;
    }

    private void StartMonitor()
    {
        _monitorCts?.Cancel();
        _monitorCts = new CancellationTokenSource();
        var ct = _monitorCts.Token;
        _ = Task.Run(() => MonitorLoopAsync(ct), ct);
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        var idle = 0;
        while (!ct.IsCancellationRequested)
        {
            Process? proc; lock (_gate) proc = _process;
            // Fin PRIMAIRE = process fermé (--eof-exit à la vraie fin). L'inactivité n'est qu'un secours.
            if (proc is null || proc.HasExited) { Finish("process terminé"); return; }

            var active = await _ra.GetActiveReplayAsync(ct).ConfigureAwait(false);
            var status = await _ra.GetStatusAsync(ct).ConfigureAwait(false);
            var paused = status is { State: "PAUSED" };
            lock (_gate)
            {
                if (active is { Active: true }) { _frame = active.Frame; idle = 0; }
                else if (!paused) idle++;   // inactif EN PAUSE = normal, on ne compte pas (évite la fausse fin)
                _paused = paused;
            }
            // Secours : inactif hors pause pendant ~4 s (8×500 ms) = replay réellement terminé/bloqué.
            if (idle >= 8) { Finish("fin du replay (active_replay inactif)"); return; }
            try { await Task.Delay(500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    private void Finish(string reason)
    {
        Process? proc;
        lock (_gate)
        {
            if (_state is ReplayPlaybackState.Idle) return;
            proc = _process; _process = null; _state = ReplayPlaybackState.Finished; _paused = false;
        }
        try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
        _logger.LogInformation("Replay : lecture terminée ({Reason}).", reason);
        _ = Publish("replay.finished", new { reason });
        lock (_gate) { if (_state is ReplayPlaybackState.Finished) { _state = ReplayPlaybackState.Idle; _replayId = null; _frame = 0; _card = null; } }
    }

    private PlayResult Fail(ReplayErrorCode code)
    {
        lock (_gate) { _state = ReplayPlaybackState.Error; _error = code; }
        _logger.LogWarning("Replay : lecture refusée/échouée : {Code}", code);
        return new PlayResult(false, "error", code);
    }

    /// <summary>Le vrai core (cores_real/&lt;core&gt;_libretro.dll) pour éviter le wrapper de scoring. null si absent.</summary>
    private static string? ResolveRealCore(string core)
    {
        var real = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "cores_real", core + "_libretro.dll");
        return File.Exists(real) ? real : null;
    }

    private async Task Publish(string type, object payload)
    {
        try { await _bus.PublishAsync(new EventEnvelope { Type = type, Payload = payload }).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay : publication event {Type} échouée", type); }
    }

    // ── fiche performance (overlay) ──
    private static readonly CultureInfo Fr = new("fr-FR");
    private static readonly HashSet<string> RegionTokens = new(StringComparer.OrdinalIgnoreCase)
    { "usa", "europe", "japan", "world", "eu", "us", "jp", "en", "fr", "de", "es", "it",
      "rev", "proto", "beta", "demo", "sample", "unl", "pd" };

    private static ReplayCard BuildCard(ReplayManifest m, ReplayLocalMetadata? meta)
    {
        var game = PrettifyGame(m.Game);
        var system = PrettifyWords(m.Game.SystemId);
        var date = m.CreatedAt.ToLocalTime().ToString("dd MMM yyyy", Fr);
        // Non capturés en R1 : le joueur (à saisir au record / compte NelfePlay) et le rang
        // (classement). Le score existe dans ScoreLink mais reste null tant que la corrélation
        // scoring n'est pas branchée. Certifié = replay publié (état de publication).
        const string player = "JOUEUR";
        var score = m.ScoreLink?.ScoreValueSnapshot;
        int? rank = null;
        var certified = string.Equals(meta?.PublicationState, "published", StringComparison.OrdinalIgnoreCase);
        return new ReplayCard(game, system, date, player, score, rank, certified);
    }

    private static string PrettifyGame(ReplayGame g)
    {
        if (!string.IsNullOrWhiteSpace(g.RomGroup)) return PrettifyWords(g.RomGroup!);
        var seg = g.GameId.Contains('/') ? g.GameId[(g.GameId.LastIndexOf('/') + 1)..] : g.GameId;
        var words = seg.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .TakeWhile(w => !RegionTokens.Contains(w));
        return PrettifyWords(string.Join(' ', words));
    }

    private static string PrettifyWords(string raw)
    {
        var s = raw.Replace('-', ' ').Replace('_', ' ').Trim();
        if (s.Length == 0) return raw;
        return Fr.TextInfo.ToTitleCase(s.ToLowerInvariant());
    }
}
