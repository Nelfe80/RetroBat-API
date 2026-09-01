using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Playback;

namespace RetroBat.Api.Replay.Controllers;

/// <summary>Lecture d'un replay (R2). Commandes logiques uniquement, jamais de chemin/commande libre.</summary>
[ApiController]
[Tags("Replay")]
[Route("api/v1/replay")]
public sealed class ReplayPlaybackController : ControllerBase
{
    private readonly ReplayPlaybackService _playback;

    public ReplayPlaybackController(ReplayPlaybackService playback) => _playback = playback;

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
            case "seek_relative": await _playback.SeekRelativeAsync(req.Seconds ?? 0, ct); break;
            case "next_checkpoint": await _playback.NextCheckpointAsync(ct); break;
            case "restart_run": await _playback.RestartRunAsync(ct); break;
            case "stop": await _playback.StopAsync(ct); break;
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
        });
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
