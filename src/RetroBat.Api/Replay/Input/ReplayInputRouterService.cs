using System.Text.Json;
using RetroBat.Api.Replay.Playback;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Replay.Input;

/// <summary>
/// Contrôles physiques Replay (R3). S'abonne à panel.input.pressed/released et, pendant une
/// LECTURE (playback.IsBusy), traduit les DIRECTIONS du panel en commandes RetroArch natives.
/// N'agit jamais hors lecture (les entrées vont au jeu/ES normalement).
///
/// Mapping (sans SELECT, pour éviter le télescopage avec la couche hotkey RetroArch que SELECT
/// active) :
///   ▲ haut          = lecture / pause
///   ▼ bas           = checkpoint suivant (pas à pas)
///   ◀ gauche (tenu) = recul rapide  (seek -5 s répété)
///   ▶ droite (tenu) = avance rapide (seek +5 s répété)
///   START (tenu)    = quitter la lecture
/// Les boutons de façade (A/B/X/Y) sont laissés LIBRES pour les réactions (étape 2).
///
/// Les directions arrivent via le canal additif du watcher (System=DPAD, identités
/// up/down/left/right) — cf. CabinetInputReader.SnapshotDirections.
/// </summary>
public sealed class ReplayInputRouterService : IHostedService
{
    private const int QuitHoldMs = 700;
    private const double FastSeekStepSeconds = 5;
    private const int FastSeekRepeatMs = 350;

    private readonly IEventBus _bus;
    private readonly ReplayPlaybackService _playback;
    private readonly ILogger<ReplayInputRouterService> _logger;

    private readonly object _repeatGate = new();
    private readonly Dictionary<string, CancellationTokenSource> _repeats = new(); // "left"/"right" tenus
    private IDisposable? _sub;
    private DateTime? _startDownAt;

    public ReplayInputRouterService(IEventBus bus, ReplayPlaybackService playback,
        ILogger<ReplayInputRouterService> logger)
    {
        _bus = bus; _playback = playback; _logger = logger;
    }

    public Task StartAsync(CancellationToken ct) { _sub = _bus.Subscribe<EventEnvelope>(OnEvent); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { _sub?.Dispose(); StopAllRepeats(); return Task.CompletedTask; }

    private void OnEvent(EventEnvelope e)
    {
        var pressed = string.Equals(e.Type, "panel.input.pressed", StringComparison.Ordinal);
        var released = string.Equals(e.Type, "panel.input.released", StringComparison.Ordinal);
        if (!pressed && !released) return;

        var (identity, system) = ReadButton(e.Payload);

        // Hors lecture : on ne pilote rien (et on coupe d'éventuelles répétitions résiduelles).
        if (!_playback.IsBusy) { _startDownAt = null; StopAllRepeats(); return; }

        // START tenu = quitter.
        if (string.Equals(system, "START", StringComparison.Ordinal))
        {
            if (pressed) _startDownAt = DateTime.UtcNow;
            else if (_startDownAt is DateTime t)
            {
                _startDownAt = null;
                if ((DateTime.UtcNow - t).TotalMilliseconds >= QuitHoldMs) Fire("quit", _playback.StopAsync);
            }
            return;
        }

        // Directions (canal DPAD).
        if (string.Equals(system, "DPAD", StringComparison.Ordinal) && !string.IsNullOrEmpty(identity))
        {
            var dir = identity.ToLowerInvariant();
            if (pressed) OnDirectionDown(dir); else OnDirectionUp(dir);
        }
    }

    private void OnDirectionDown(string dir)
    {
        switch (dir)
        {
            case "up": Fire("pause", _playback.PauseToggleAsync); break;
            case "down": Fire("next-checkpoint", _playback.NextCheckpointAsync); break;
            case "left": StartRepeat("left", -FastSeekStepSeconds); break;
            case "right": StartRepeat("right", +FastSeekStepSeconds); break;
        }
    }

    private void OnDirectionUp(string dir)
    {
        if (dir is "left" or "right") StopRepeat(dir);
    }

    // ── recul/avance rapide : re-seek tant que la direction est tenue ──
    private void StartRepeat(string dir, double stepSeconds)
    {
        CancellationTokenSource cts;
        lock (_repeatGate)
        {
            if (_repeats.ContainsKey(dir)) return; // déjà en cours
            cts = new CancellationTokenSource();
            _repeats[dir] = cts;
        }

        var ct = cts.Token;
        _logger.LogDebug("Replay : {Dir} maintenu → seek répété {Step}s", dir, stepSeconds);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested && _playback.IsBusy)
                {
                    await _playback.SeekRelativeAsync(stepSeconds, ct).ConfigureAwait(false);
                    await Task.Delay(FastSeekRepeatMs, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogDebug(ex, "Replay : répétition seek {Dir} interrompue", dir); }
        }, ct);
    }

    private void StopRepeat(string dir)
    {
        CancellationTokenSource? cts;
        lock (_repeatGate)
        {
            if (!_repeats.Remove(dir, out cts)) return;
        }
        cts.Cancel();
        cts.Dispose();
    }

    private void StopAllRepeats()
    {
        List<CancellationTokenSource> all;
        lock (_repeatGate)
        {
            if (_repeats.Count == 0) return;
            all = _repeats.Values.ToList();
            _repeats.Clear();
        }
        foreach (var cts in all) { cts.Cancel(); cts.Dispose(); }
    }

    private void Fire(string label, Func<CancellationToken, Task> action)
    {
        _logger.LogDebug("Replay contrôle panel : {Label}", label);
        _ = Task.Run(async () =>
        {
            try { await action(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Replay : commande panel {Label} échouée", label); }
        });
    }

    private static (string? identity, string? system) ReadButton(object? payload)
    {
        if (payload is null) return (null, null);
        try
        {
            var el = JsonSerializer.SerializeToElement(payload);
            var id = el.TryGetProperty("Identity", out var i) ? i.GetString() : null;
            var sys = el.TryGetProperty("System", out var s) ? s.GetString() : null;
            return (id, sys);
        }
        catch { return (null, null); }
    }
}
