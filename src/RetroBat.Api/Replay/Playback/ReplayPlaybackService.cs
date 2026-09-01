using System.Diagnostics;
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
    private bool _paused;
    private ReplayErrorCode _error = ReplayErrorCode.None;
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
        long? RunStartFrame, long? RunEndFrame, long? ReplayEndFrame, bool Paused, string? Error);

    public StateSnapshot GetState()
    {
        lock (_gate)
        {
            var mode = _state is ReplayPlaybackState.Idle ? "none" : "replay";
            return new StateSnapshot(mode, _state.ToString().ToLowerInvariant(), _replayId, _frame,
                _runStart, _runEnd, _replayEnd, _paused,
                _error == ReplayErrorCode.None ? null : _error.ToString());
        }
    }

    public async Task<PlayResult> PlayAsync(string replayId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (IsBusy) return new PlayResult(false, _state.ToString().ToLowerInvariant(), ReplayErrorCode.ReplayAlreadyRunning);
            _state = ReplayPlaybackState.Resolving; _replayId = replayId; _error = ReplayErrorCode.None;
            _frame = 0; _paused = false;
        }

        var manifest = _store.GetManifest(replayId);
        if (manifest is null) return Fail(ReplayErrorCode.ReplayNotFound);
        var meta = _store.GetMeta(replayId);
        var hint = meta?.Launch;
        var objectPath = _store.ObjectPath(manifest.Object.Sha256);
        if (!File.Exists(objectPath)) return Fail(ReplayErrorCode.ReplayObjectUnavailable);

        // ── vérification runtime (R2 MVP : présence ; empreintes strictes quand le manifeste les portera) ──
        lock (_gate) { _state = ReplayPlaybackState.Verifying; _replayEnd = manifest.Frames.ReplayEnd;
            _runStart = manifest.Frames.RunStart; _runEnd = manifest.Frames.RunEnd; }
        if (hint is null) return Fail(ReplayErrorCode.RuntimeIncompatible);
        var coreDll = ResolveRealCore(hint.Core) ?? hint.CoreDll;
        if (!File.Exists(coreDll)) return Fail(ReplayErrorCode.CoreNotFound);
        if (string.IsNullOrEmpty(hint.RomPath) || !File.Exists(hint.RomPath)) return Fail(ReplayErrorCode.RomNotFound);

        // ── pas de jeu déjà en cours (on lance notre propre RetroArch) ──
        var status = await _ra.GetStatusAsync(ct).ConfigureAwait(false);
        if (status is { ContentLoaded: true }) return Fail(ReplayErrorCode.GameAlreadyRunning);

        // ── lancement ──
        lock (_gate) _state = ReplayPlaybackState.Launching;
        var exe = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "retroarch.exe");
        var psi = new ProcessStartInfo { FileName = exe, WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false };
        psi.ArgumentList.Add("-L"); psi.ArgumentList.Add(coreDll);
        psi.ArgumentList.Add(hint.RomPath);
        psi.ArgumentList.Add("-P"); psi.ArgumentList.Add(objectPath);
        psi.ArgumentList.Add("--config"); psi.ArgumentList.Add(RetroBatPaths.RetroArchConfigPath);
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
        lock (_gate) { _process = null; _replayId = null; _state = ReplayPlaybackState.Idle; _frame = 0; }
        await Publish("replay.finished", new { reason = "user" }).ConfigureAwait(false);
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
            if (proc is null || proc.HasExited) { Finish("process terminé"); return; }

            var active = await _ra.GetActiveReplayAsync(ct).ConfigureAwait(false);
            var status = await _ra.GetStatusAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (active is { Active: true }) { _frame = active.Frame; idle = 0; } else idle++;
                _paused = status is { State: "PAUSED" };
            }
            if (idle >= 4) { Finish("fin du replay (active_replay inactif)"); return; }
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
        lock (_gate) { if (_state is ReplayPlaybackState.Finished) { _state = ReplayPlaybackState.Idle; _replayId = null; _frame = 0; } }
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
}
