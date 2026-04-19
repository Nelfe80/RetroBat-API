using Microsoft.AspNetCore.Mvc;

namespace RetroBat.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class CommandsController : ControllerBase
{
    [HttpPost("launch")]
    public IActionResult LaunchGame([FromBody] LaunchPayload payload)
    {
        return Accepted(new { status = "launching", gameId = payload.GameId });
    }
}

public class LaunchPayload
{
    public string GameId { get; set; } = string.Empty;
    public string Options { get; set; } = string.Empty;
}
