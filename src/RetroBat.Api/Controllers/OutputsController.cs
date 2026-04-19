using Microsoft.AspNetCore.Mvc;

namespace RetroBat.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class OutputsController : ControllerBase
{
    [HttpGet("mame")]
    public IActionResult GetMameOutputs()
    {
        // Mock returning the last known output state for MAME
        return Ok(new
        {
            source = "mame.network",
            port = 8000,
            machineName = "chasehq",
            signals = new[]
            {
                new { key = "genout5", value = 1, ts = DateTime.UtcNow },
                new { key = "genout6", value = 0, ts = DateTime.UtcNow }
            }
        });
    }
}
