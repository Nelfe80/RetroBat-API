using Microsoft.AspNetCore.Mvc;

namespace RetroBat.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class HubController : ControllerBase
{
    [HttpPost("register")]
    public IActionResult RegisterNode([FromBody] RegisterPayload payload)
    {
        // Handle node registration for ArcadeHub mode
        return Ok(new { status = "registered", hubMode = "active" });
    }

    [HttpGet("nodes")]
    public IActionResult GetNodes()
    {
        return Ok(new[] {
            new { nodeId = "cab-01", status = "online", mode = "node" }
        });
    }
}

public class RegisterPayload
{
    public string NodeId { get; set; } = string.Empty;
    public string DiscoveryUrl { get; set; } = string.Empty;
}
