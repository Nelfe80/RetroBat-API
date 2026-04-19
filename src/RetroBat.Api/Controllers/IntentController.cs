using Microsoft.AspNetCore.Mvc;

namespace RetroBat.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class IntentController : ControllerBase
{
    [HttpPost("pushView")]
    public IActionResult PushView([FromBody] PushViewPayload payload)
    {
        // This simulates pushing an intent into the system
        // The IntentStore and Orchestrator would process it
        return Accepted(new {
            status = "accepted",
            intentId = Guid.NewGuid().ToString(),
            expiresAt = DateTime.UtcNow.AddSeconds(payload.TtlSeconds ?? 60)
        });
    }
}

public class PushViewPayload
{
    public string Target { get; set; } = string.Empty;
    public string ViewName { get; set; } = string.Empty;
    public int? TtlSeconds { get; set; }
    public string ResolvableContext { get; set; } = string.Empty;
}
