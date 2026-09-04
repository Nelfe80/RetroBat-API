using System.Text.Json;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Playback;
using RetroBat.Api.Replay.Storage;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Replay.Input;

/// <summary>
/// Moteur d'émission des RÉACTIONS d'audience (R4, étape 2a). Pendant une LECTURE, les 8 boutons
/// de façade (libérés par le transport-directions) déclenchent une réaction :
///   • appui d'UN bouton = sa famille ; le MAINTIEN monte l'intensité (niveau 1→2→3) ;
///   • ≥3 boutons pressés ENSEMBLE = accord « CÉLÉBRATION » (intensité = nb de boutons),
///     qui SUPPRIME les réactions individuelles des boutons concernés et ne compte qu'une fois.
/// Anti-spam par BUDGET (≈10/min proportionnel à la durée, borné [3,40]) recalculé par replay.
/// Chaque réaction est horodatée (frame + ms), publiée (event replay.reaction) et STOCKÉE en JSONL
/// pour être rejouée (affichage = étape suivante). Les MOTS/emojis (6 langues) sont une table à part.
///
/// NB swap RetroBat A↔B : bouton A physique = identité "b", B = "a" (X/Y non swappés).
/// </summary>
public sealed class ReplayReactionService : IHostedService
{
    private const int ChordThreshold = 3;   // ≥3 boutons simultanés = célébration
    private const int HoldLevel2Ms = 400;
    private const int HoldLevel3Ms = 900;
    private const int CooldownMs = 600;     // délai anti-spam entre deux réactions

    // Identité RetroPad (swap pris en compte) → famille de réaction.
    private static readonly IReadOnlyDictionary<string, string> Family = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["b"] = "hype",     // bouton A physique
        ["a"] = "wow",      // bouton B physique
        ["x"] = "respect",
        ["y"] = "laugh",
        ["l"] = "tension",
        ["r"] = "ouch",
        ["l2"] = "love",
        ["r2"] = "rage",
    };

    private readonly IEventBus _bus;
    private readonly ReplayPlaybackService _playback;
    private readonly ReplayStore _store;
    private readonly RetroBat.Api.Infrastructure.NelfePlayAgentService _agent;   // pseudo appairé = auteur du react
    private readonly ILogger<ReplayReactionService> _logger;

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _down = new();  // boutons de réaction tenus
    private readonly HashSet<string> _gesture = new();            // boutons consommés par une célébration
    private bool _celebrating;
    private int _celebMaxCount;
    private string? _budgetReplayId;
    private int _budget;
    private int _budgetMax;
    private DateTime _cooldownUntil;

    private IDisposable? _sub;

    public ReplayReactionService(IEventBus bus, ReplayPlaybackService playback, ReplayStore store,
        RetroBat.Api.Infrastructure.NelfePlayAgentService agent, ILogger<ReplayReactionService> logger)
    {
        _bus = bus; _playback = playback; _store = store; _agent = agent; _logger = logger;
    }

    public Task StartAsync(CancellationToken ct) { _sub = _bus.Subscribe<EventEnvelope>(OnEvent); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { _sub?.Dispose(); return Task.CompletedTask; }

    /// <summary>Charge EN COURS (pour la jauge de l'overlay) : le bouton le plus longtemps tenu,
    /// ou l'accord célébration. Progress 0-1 sur la durée de maintien.</summary>
    public sealed record ChargeSnapshot(bool Active, string Family, int Level, double Progress, bool Chord, int ChordCount);

    /// <summary>Disponibilité des réactions (pour l'indicateur de la légende) : budget restant + cooldown.</summary>
    public sealed record Availability(int Budget, int BudgetMax, bool CanReact, double CooldownProgress);

    public Availability GetAvailability()
    {
        lock (_gate)
        {
            var cdLeft = (_cooldownUntil - DateTime.UtcNow).TotalMilliseconds;
            var can = _budget > 0 && cdLeft <= 0;
            var prog = cdLeft > 0 ? Math.Clamp(1 - cdLeft / CooldownMs, 0, 1) : 1;
            return new Availability(_budget, _budgetMax, can, prog);
        }
    }

    public ChargeSnapshot GetCharge()
    {
        lock (_gate)
        {
            if (_celebrating)
            {
                var lvl = CelebLevel(_celebMaxCount);
                var prog = Math.Clamp(_celebMaxCount / 8.0, 0.15, 1.0);
                return new ChargeSnapshot(true, "celebrate", lvl, prog, true, _celebMaxCount);
            }
            if (_down.Count == 0) return new ChargeSnapshot(false, "", 0, 0, false, 0);
            var earliest = _down.Values.Min();
            var id = _down.First(kv => kv.Value == earliest).Key;
            var elapsed = (DateTime.UtcNow - earliest).TotalMilliseconds;
            var level = elapsed < HoldLevel2Ms ? 1 : elapsed < HoldLevel3Ms ? 2 : 3;
            var progress = Math.Clamp(elapsed / (double)HoldLevel3Ms, 0, 1);
            return new ChargeSnapshot(true, Family[id], level, progress, false, 0);
        }
    }

    private void OnEvent(EventEnvelope e)
    {
        // Chaque LECTURE repart avec un budget neuf (sinon rejouer le même replay resterait à 0).
        if (string.Equals(e.Type, "replay.started", StringComparison.Ordinal))
        {
            lock (_gate) { _budgetReplayId = null; _down.Clear(); ResetGesture(); }
            return;
        }
        if (string.Equals(e.Type, "replay.finished", StringComparison.Ordinal))
        {
            lock (_gate) { _down.Clear(); ResetGesture(); }
            return;
        }

        var pressed = string.Equals(e.Type, "panel.input.pressed", StringComparison.Ordinal);
        var released = string.Equals(e.Type, "panel.input.released", StringComparison.Ordinal);
        if (!pressed && !released) return;

        var (identity, system) = ReadButton(e.Payload);
        // Les entrées « système » (START/SELECT/DPAD/L3/R3) portent un System non nul → pas des réactions.
        if (identity is null || system is not null) return;
        var id = identity.ToLowerInvariant();
        if (!Family.ContainsKey(id)) return; // uniquement les 8 boutons de réaction

        var st = _playback.GetState();
        if (!string.Equals(st.Mode, "replay", StringComparison.Ordinal))
        {
            lock (_gate) { _down.Clear(); ResetGesture(); }
            return;
        }

        if (pressed) OnPress(id, st); else OnRelease(id, st);
    }

    private void OnPress(string id, ReplayPlaybackService.StateSnapshot st)
    {
        lock (_gate)
        {
            EnsureBudget(st);
            _down[id] = DateTime.UtcNow;

            if (_celebrating)
            {
                _gesture.Add(id);
                _celebMaxCount = Math.Max(_celebMaxCount, _down.Count);
            }
            else if (_down.Count >= ChordThreshold)
            {
                // L'accord se forme : bascule en célébration, les singles en cours sont abandonnés.
                _celebrating = true;
                _celebMaxCount = _down.Count;
                _gesture.Clear();
                foreach (var k in _down.Keys) _gesture.Add(k);
            }
            // sinon : réaction simple en cours, le niveau sera calculé au relâché.
        }
    }

    private void OnRelease(string id, ReplayPlaybackService.StateSnapshot st)
    {
        string? family = null; var level = 0; var chord = false;
        lock (_gate)
        {
            if (!_down.TryGetValue(id, out var pressedAt)) return;
            _down.Remove(id);

            if (_celebrating || _gesture.Contains(id))
            {
                _gesture.Add(id);
                if (_celebrating && _down.Count == 0)
                {
                    family = "celebrate";
                    level = CelebLevel(_celebMaxCount);
                    chord = true;
                    ResetGesture();
                }
                // sinon : d'autres boutons de l'accord sont encore tenus, on attend.
            }
            else
            {
                var heldMs = (DateTime.UtcNow - pressedAt).TotalMilliseconds;
                family = Family[id];
                level = heldMs < HoldLevel2Ms ? 1 : heldMs < HoldLevel3Ms ? 2 : 3;
            }
        }

        if (family is not null) Commit(st, family, level, chord);
    }

    private void Commit(ReplayPlaybackService.StateSnapshot st, string family, int level, bool chord)
    {
        lock (_gate)
        {
            if (_budget <= 0) { _logger.LogDebug("Replay réaction ignorée (budget épuisé) : {F} n{L}", family, level); return; }
            if (DateTime.UtcNow < _cooldownUntil) { _logger.LogDebug("Replay réaction ignorée (cooldown) : {F} n{L}", family, level); return; }
            _budget--;
            _cooldownUntil = DateTime.UtcNow.AddMilliseconds(CooldownMs);
        }

        var pseudo = _agent.Status.Pseudo;
        var r = new ReplayReaction(st.ReplayId ?? "", family, level, st.Frame,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Lang(), chord,
            string.IsNullOrWhiteSpace(pseudo) ? null : pseudo);
        try { _store.AppendReaction(r); }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay : append réaction échoué"); }
        _ = Publish(r);
        _logger.LogInformation("Replay réaction : {F} niveau {L}{Chord} @frame {Fr} (budget restant {B})",
            family, level, chord ? " [accord]" : "", st.Frame, _budget);
    }

    // Budget recalculé quand on change de replay (≈10/min, borné [3,40]).
    private void EnsureBudget(ReplayPlaybackService.StateSnapshot st)
    {
        if (string.Equals(_budgetReplayId, st.ReplayId, StringComparison.Ordinal)) return;
        _budgetReplayId = st.ReplayId;
        var fps = st.NominalFps <= 0 ? 60 : st.NominalFps;
        var minutes = (st.ReplayEndFrame ?? 0) / fps / 60.0;
        _budget = Math.Clamp((int)Math.Round(minutes * 10), 3, 40);
        _budgetMax = _budget;
        _cooldownUntil = DateTime.MinValue;
        _logger.LogDebug("Replay réactions : budget {B} pour {Id} ({Min:F1} min)", _budget, st.ReplayId, minutes);
    }

    private static int CelebLevel(int count) => count >= 7 ? 3 : count >= 5 ? 2 : 1;

    private void ResetGesture() { _celebrating = false; _celebMaxCount = 0; _gesture.Clear(); }

    // Langue de l'auteur (affichage). TODO : brancher sur la langue ES/UI ; défaut FR pour l'instant.
    private static string Lang() => "fr";

    private async Task Publish(ReplayReaction r)
    {
        try { await _bus.PublishAsync(new EventEnvelope { Type = "replay.reaction", Payload = r }).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay : publication réaction échouée"); }
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
