using Microsoft.AspNetCore.Mvc;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class MediaController : ControllerBase
{
    private readonly IMediaStore _mediaStore;

    public MediaController(IMediaStore mediaStore)
    {
        _mediaStore = mediaStore;
    }

    [HttpGet("{*path}")]
    public async Task<IActionResult> GetMedia(string path)
    {
        // TODO: Resolve game reference and asset kind
        var fileToServe = await _mediaStore.ResolveAsync(path, "marquee");
        if (fileToServe == null || !System.IO.File.Exists(fileToServe))
        {
            return NotFound();
        }

        return PhysicalFile(fileToServe, "image/png");
    }
}
