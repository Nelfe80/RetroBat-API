using System.Text.Json;
using RetroBat.Api.Replay.Playback;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Replay.Input;

/// <summary>
/// Contrôles physiques Replay (R3). S'abonne à panel.input.pressed/released, suit le
/// modificateur SELECT, et pendant une LECTURE (playback.IsBusy) traduit SELECT+bouton en
/// commande RetroArch. N'agit jamais hors lecture (les presses vont au jeu/ES normalement).
///
/// NB matériel : le lecteur de panel n'expose PAS les directions (dpad/axes ignorés, seul le
/// map FaceSwap produit des identités : a/b/x/y/l/r/l2/r2/select/start). Le seek du CDC
/// (SELECT+directions) est donc remappé sur les GÂCHETTES (l/r = ±10 s, l2/r2 = ±60 s).
/// </summary>
public sealed class ReplayInputRouterService : IHostedService
{
    private const int QuitHoldMs = 700;

    private readonly IEventBus _bus;
    private readonly ReplayPlaybackService _playback;
    private readonly ILogger<ReplayInputRouterService> _logger;

    private IDisposable? _sub;
    private volatile bool _selectDown;
    private DateTime? _bDownAt;

    public ReplayInputRouterService(IEventBus bus, ReplayPlaybackService playback,
        ILogger<ReplayInputRouterService> logger)
    {
        _bus = bus; _playback = playback; _logger = logger;
    }

    public Task StartAsync(CancellationToken ct) { _sub = _bus.Subscribe<EventEnvelope>(OnEvent); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { _sub?.Dispose(); return Task.CompletedTask; }

    private void OnEvent(EventEnvelope e)
    {
        var pressed = string.Equals(e.Type, "panel.input.pressed", StringComparison.Ordinal);
        var released = string.Equals(e.Type, "panel.input.released", StringComparison.Ordinal);
        if (!pressed && !released) return;

        var (identity, system) = ReadButton(e.Payload);

        if (string.Equals(system, "SELECT", StringComparison.Ordinal))
        {
            _selectDown = pressed;
            if (!pressed) _bDownAt = null;
            _logger.LogDebug("Replay panel : SELECT {State}", pressed ? "down" : "up");
            return;
        }

        if (!_selectDown || string.IsNullOrEmpty(identity)) return;
        var id = identity.ToLowerInvariant();

        // Découverte des identités (Debug) : toute combinaison SELECT+bouton, même hors lecture.
        if (pressed) _logger.LogDebug("Replay panel : SELECT+{Id} (playback={Busy})", id, _playback.IsBusy);

        // Action seulement pendant une lecture.
        if (!_playback.IsBusy) return;
        if (pressed) OnControlPress(id); else OnControlRelease(id);
    }

    private void OnControlPress(string id)
    {
        // NB : RetroBat swappe A<->B (bouton A physique = identité "b", B physique = "a").
        // On mappe donc par LABEL physique attendu : A=pause, B=quit.
        switch (id)
        {
            case "b": Fire("pause", _playback.PauseToggleAsync); break;               // bouton A physique
            case "l": Fire("-10s", c => _playback.SeekRelativeAsync(-10, c)); break;
            case "r": Fire("+10s", c => _playback.SeekRelativeAsync(+10, c)); break;
            case "l2": Fire("-60s", c => _playback.SeekRelativeAsync(-60, c)); break;
            case "r2": Fire("+60s", c => _playback.SeekRelativeAsync(+60, c)); break;
            case "x": Fire("restart-run", _playback.RestartRunAsync); break;
            case "y": Fire("next-checkpoint", _playback.NextCheckpointAsync); break;
            case "a": _bDownAt = DateTime.UtcNow; break; // bouton B physique = quit sur maintien
        }
    }

    private void OnControlRelease(string id)
    {
        if (id == "a" && _bDownAt is DateTime t) // bouton B physique
        {
            _bDownAt = null;
            if ((DateTime.UtcNow - t).TotalMilliseconds >= QuitHoldMs) Fire("quit", _playback.StopAsync);
        }
    }

    private void Fire(string label, Func<CancellationToken, Task> action)
    {
        _logger.LogDebug("Replay contrôle panel : SELECT+{Label}", label);
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
