using Microsoft.AspNetCore.Mvc;

namespace RetroBat.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new { status = "healthy", version = "1.0.0" });
    }
}

[ApiController]
[Route("api/v1/[controller]")]
public class VersionController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new { version = "1.0.0", name = "RetroBat Local API" });
    }
}
