using Microsoft.AspNetCore.Mvc;
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

    /// <summary>Reconstruit l'index depuis les manifests (maintenance).</summary>
    [HttpPost("rebuild-index")]
    public IActionResult RebuildIndex() => Ok(new { rebuilt = _store.RebuildIndex().Count });
}
