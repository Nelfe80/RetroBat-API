using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using RetroBat.Api.Media;

namespace RetroBat.Api.Controllers;

/// <summary>
/// LOT 8 — serves a gamelist media asset (canonical store OR a user binding under roms/) from an
/// OPAQUE, allowlist-scoped reference produced by <see cref="GamelistMediaAssetResolver"/>. The
/// reference never carries a steerable path; the resolver re-validates the root and rejects any
/// traversal, so a client cannot read outside the allowlisted roots. A missing or invalid reference
/// is a plain 404.
/// </summary>
[ApiController]
[Route("api/v1/media-asset")]
public class MediaAssetController : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    private readonly GamelistMediaAssetResolver _resolver;

    public MediaAssetController(GamelistMediaAssetResolver resolver)
    {
        _resolver = resolver;
    }

    [HttpGet("{reference}")]
    [HttpHead("{reference}")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Get(string reference)
    {
        var file = _resolver.TryResolve(reference);
        if (file is null)
        {
            return NotFound();
        }

        if (!ContentTypeProvider.TryGetContentType(file, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        return PhysicalFile(file, contentType);
    }
}
