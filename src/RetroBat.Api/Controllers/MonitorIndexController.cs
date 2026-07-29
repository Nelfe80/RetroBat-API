using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Syncs the standalone-emulator monitor to the RetroBat screen. Called by Marquee
/// Manager when the assigned game screen changes (it passes the current device name),
/// and reachable manually to force a re-sync. See <see cref="MonitorIndexSyncService"/>.
/// </summary>
[ApiController]
[Route("api/v1/system/monitor-index")]
public sealed class MonitorIndexController : ControllerBase
{
    private readonly MonitorIndexSyncService _sync;

    public MonitorIndexController(MonitorIndexSyncService sync) => _sync = sync;

    /// <summary>Recomputes and writes &lt;system&gt;.MonitorIndex to target the RetroBat
    /// screen. <paramref name="deviceName"/> (optional, e.g. <c>\\.\DISPLAY1</c>) is the
    /// currently assigned game-screen device name from Marquee Manager; omitted = auto
    /// (live EmulationStation window, else primary screen).</summary>
    [HttpPost("sync")]
    public IActionResult Sync([FromQuery] string? deviceName = null)
    {
        var index = _sync.Sync(deviceName);
        return Ok(new { applied = index.HasValue, monitorIndex = index });
    }
}
