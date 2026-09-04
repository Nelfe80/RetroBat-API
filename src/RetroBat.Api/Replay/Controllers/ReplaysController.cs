using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Storage;

namespace RetroBat.Api.Replay.Controllers;

/// <summary>
/// API locale Replay (R1) : lecture seule. Renvoie des identités logiques, jamais un
/// chemin local. L'index est une vue dérivée reconstructible depuis les manifests.
/// </summary>
[ApiController]
[Tags("Replay")]
[Route("api/v1/[controller]")]
public sealed class ReplaysController : ControllerBase
{
    private readonly ReplayStore _store;

    public ReplaysController(ReplayStore store) => _store = store;

    /// <summary>Liste les replays connus (depuis l'index, reconstruit s'il est absent).</summary>
    [HttpGet]
    public IActionResult List([FromQuery] string? game_id = null)
    {
        var index = _store.ReadIndex();
        if (index.Count == 0) index = _store.RebuildIndex();

        var items = index
            .Where(e => game_id is null || string.Equals(e.GameId, game_id, StringComparison.Ordinal))
            .Select(e =>
            {
                var meta = _store.GetMeta(e.ReplayId);
                return new
                {
                    replay_id = e.ReplayId,
                    game_id = e.GameId,
                    created_at = e.CreatedAt,
                    object_sha256 = e.ObjectSha256,
                    visibility = meta?.Visibility ?? "private",
                    local_available = System.IO.File.Exists(_store.ObjectPath(e.ObjectSha256)),
                    score_ref = meta?.ScoreRef,
                };
            })
            .ToList();

        return Ok(new { items, total = items.Count });
    }

    /// <summary>Vue combinée manifeste + métadonnées locales + disponibilité (sans chemin).</summary>
    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        var m = _store.GetManifest(id);
        if (m is null) return NotFound(new { ok = false, error = new { code = "REPLAY_NOT_FOUND" } });
        return Ok(new
        {
            manifest = m,
            metadata = _store.GetMeta(id),
            local_available = System.IO.File.Exists(_store.ObjectPath(m.Object.Sha256)),
        });
    }

    /// <summary>Le manifeste immuable, tel qu'écrit sur disque (snake_case).</summary>
    [HttpGet("{id}/manifest")]
    public IActionResult GetManifest(string id)
    {
        var path = _store.ManifestPath(id);
        if (!System.IO.File.Exists(path))
            return NotFound(new { ok = false, error = new { code = "REPLAY_NOT_FOUND" } });
        return new FileContentResult(System.IO.File.ReadAllBytes(path), "application/json");
    }

    public sealed record VisibilityRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("visibility")] string? Visibility);

    /// <summary>
    /// Rend un replay partageable, ou le retire du partage. C'est le SEUL geste qui autorise une
    /// autre borne à récupérer l'objet. Il modifie les métadonnées locales, jamais le manifeste
    /// immuable (CDC §66) : la visibilité est une décision de cette borne, pas une propriété du
    /// replay. `followers` est refusé tant que le transport contrôlé n'existe pas.
    /// </summary>
    [HttpPost("{id}/visibility")]
    public IActionResult SetVisibility(string id, [FromBody] VisibilityRequest? req)
    {
        if (_store.GetManifest(id) is null)
            return NotFound(new { ok = false, error = new { code = "REPLAY_NOT_FOUND" } });

        var v = req?.Visibility?.Trim().ToLowerInvariant();
        if (v is not ("public" or "private"))
            return BadRequest(new { ok = false, error = new { code = "VISIBILITY_INVALID" }, accepted = new[] { "public", "private" } });

        var meta = _store.GetMeta(id) ?? ReplayLocalMetadata.Fresh(id);
        _store.SaveMeta(meta with { Visibility = v });
        return Ok(new { ok = true, replay_id = id, visibility = v });
    }

    /// <summary>Reconstruit l'index depuis les manifests (maintenance).</summary>
    [HttpPost("rebuild-index")]
    public IActionResult RebuildIndex() => Ok(new { rebuilt = _store.RebuildIndex().Count });
}
