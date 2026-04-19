using Microsoft.AspNetCore.Mvc;

namespace RetroBat.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class RulesController : ControllerBase
{
    [HttpGet("active")]
    public IActionResult GetActiveRules()
    {
        // Mock returning active rule sets
        return Ok(new object[] {
            new {
                ruleSetId = "lip:Nintendo64:Arcade-Shark-8B",
                scope = new { systemId = "n64", layout = "8-Button" }
            },
            new {
                ruleSetId = "lay:chasehq:Marquee_Only",
                scope = new { systemId = "mame", machine = "chasehq" }
            }
        });
    }

    [HttpPost("compile")]
    public IActionResult Compile([FromBody] object source)
    {
        // This is a placeholder for LIP/LAY compilation API
        return Accepted(new { status = "compiled", ir = "..." });
    }
}
