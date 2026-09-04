using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Playback;
using RetroBat.Api.Replay.Storage;

namespace RetroBat.Api.Replay.Controllers;

/// <summary>Lecture d'un replay (R2). Commandes logiques uniquement, jamais de chemin/commande libre.</summary>
[ApiController]
[Tags("Replay")]
[Route("api/v1/replay")]
public sealed class ReplayPlaybackController : ControllerBase
{
    private readonly ReplayPlaybackService _playback;
    private readonly ReplayStore _store;

    public ReplayPlaybackController(ReplayPlaybackService playback, ReplayStore store)
    {
        _playback = playback;
        _store = store;
    }

    // Clé canonique CDC / front : replay_id.
    public sealed record PlayRequest([property: JsonPropertyName("replay_id")] string ReplayId);

    [HttpPost("play")]
    public async Task<IActionResult> Play([FromBody] PlayRequest? req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.ReplayId))
            return BadRequest(new { ok = false, error = new { code = "REPLAY_MANIFEST_INVALID" } });

        var r = await _playback.PlayAsync(req.ReplayId, ct);
        if (!r.Accepted)
        {
            var status = r.Error is ReplayErrorCode.ReplayNotFound ? 404
                : r.Error is ReplayErrorCode.ReplayAlreadyRunning or ReplayErrorCode.GameAlreadyRunning ? 409
                : 422;
            return StatusCode(status, new { ok = false, error = new { code = ToCode(r.Error) } });
        }
        return Ok(new { accepted = true, replay_id = req.ReplayId, state = r.State });
    }

    [HttpPost("stop")]
    public async Task<IActionResult> Stop(CancellationToken ct)
    {
        await _playback.StopAsync(ct);
        return Ok(new { ok = true });
    }

    public sealed record ControlRequest(
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("seconds")] double? Seconds);

    /// <summary>Commande logique de lecture (aucune commande RetroArch libre).</summary>
    [HttpPost("control")]
    public async Task<IActionResult> Control([FromBody] ControlRequest? req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.Action))
            return BadRequest(new { ok = false, error = new { code = "REPLAY_MANIFEST_INVALID" } });

        switch (req.Action.ToLowerInvariant())
        {
            case "pause_toggle": await _playback.PauseToggleAsync(ct); break;
            case "next_checkpoint": await _playback.NextCheckpointAsync(ct); break;
            case "prev_checkpoint": await _playback.PreviousCheckpointAsync(ct); break;
            case "restart_run": await _playback.RestartRunAsync(ct); break;
            case "stop": await _playback.StopAsync(ct); break;
            // Seeks NOMMÉS (R3.11) : l'appelant exprime une INTENTION, le serveur décide la durée
            // (court/long) — l'UX peut évoluer sans casser le SDK.
            case "seek_back_short": await _playback.SeekShortBackwardAsync(ct); break;
            case "seek_forward_short": await _playback.SeekShortForwardAsync(ct); break;
            case "seek_back_long": await _playback.SeekLongBackwardAsync(ct); break;
            case "seek_forward_long": await _playback.SeekLongForwardAsync(ct); break;
            // Primitive bas niveau, gardée pour les outils techniques / le debug.
            case "seek_relative": await _playback.SeekRelativeAsync(req.Seconds ?? 0, ct); break;
            default: return BadRequest(new { ok = false, error = new { code = "REPLAY_COMMAND_UNSUPPORTED" } });
        }
        var s = _playback.GetState();
        return Ok(new { accepted = true, state = s.State, frame = s.Frame });
    }

    [HttpGet("state")]
    public IActionResult State()
    {
        var s = _playback.GetState();
        return Ok(new
        {
            mode = s.Mode,
            state = s.State,
            replay_id = s.ReplayId,
            frame = s.Frame,
            run_start_frame = s.RunStartFrame,
            run_end_frame = s.RunEndFrame,
            replay_end_frame = s.ReplayEndFrame,
            paused = s.Paused,
            error = s.Error,
            nominal_fps = s.NominalFps,
            card = s.Card is null ? null : new
            {
                game = s.Card.Game,
                system = s.Card.System,
                date = s.Card.DateText,
                player = s.Card.Player,
                score = s.Card.Score,
                rank = s.Card.Rank,
                certified = s.Card.Certified,
            },
        });
    }

    /// <summary>Réactions horodatées d'un replay (JSONL rejouable). Sert l'affichage (étape suivante).</summary>
    [HttpGet("reactions")]
    public IActionResult Reactions([FromQuery(Name = "replay_id")] string? replayId)
    {
        if (string.IsNullOrWhiteSpace(replayId))
            return BadRequest(new { ok = false, error = new { code = "REPLAY_MANIFEST_INVALID" } });
        var items = _store.ReadReactions(replayId).Select(r => new
        {
            reaction = r.Reaction, level = r.Level, frame = r.Frame, ts_ms = r.TsMs, lang = r.Lang, chord = r.Chord,
        });
        return Ok(new { replay_id = replayId, items });
    }

    // ReplayErrorCode (PascalCase) -> code API stable UPPER_SNAKE_CASE.
    private static string ToCode(ReplayErrorCode e)
    {
        var s = e.ToString();
        var sb = new StringBuilder(s.Length + 6);
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i])) sb.Append('_');
            sb.Append(char.ToUpperInvariant(s[i]));
        }
        return sb.ToString();
    }
}
