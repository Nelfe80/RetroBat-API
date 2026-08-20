using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Media;

/// <summary>
/// HP4 — the seam a media WRITER calls once it has committed a file, so the HP3 directory
/// cache never serves a listing that predates the write. It is deliberately small and free of
/// the cache's read details: a producer depends on "I changed this file", not on how the
/// discovery path is stored.
///
/// The <see cref="MediaDirectoryListingCache"/> already re-checks a directory's mtime on every
/// hit, so a file added to or removed from an EXISTING directory is caught without anyone
/// calling this. What it cannot see on its own is a directory that did not exist when it was
/// negatively cached: invalidating the path the instant APIExpose creates it closes that
/// window, instead of waiting out the short negative TTL.
/// </summary>
public interface IMediaDiscoveryInvalidator
{
    /// <summary>Drop the cached listing of the directory that holds <paramref name="fullPath"/>.</summary>
    void InvalidatePath(string fullPath);

    /// <summary>Drop the cached listing of <paramref name="directory"/>.</summary>
    void InvalidateDirectory(string directory);

    /// <summary>Drop every cached listing (a broad mutation, e.g. a media pack install).</summary>
    void Clear();
}

/// <summary>Forwards to the process-wide cache owned by the WebSocket projection service. The
/// cache is static because the enumeration helpers it backs are static; this DI wrapper lets
/// ordinary services invalidate without reaching for that static surface directly.</summary>
internal sealed class MediaDiscoveryInvalidator : IMediaDiscoveryInvalidator
{
    public void InvalidatePath(string fullPath)
        => PhysicalMediaWebSocketProjectionService.DirectoryCache.InvalidatePath(fullPath);

    public void InvalidateDirectory(string directory)
        => PhysicalMediaWebSocketProjectionService.DirectoryCache.InvalidateDirectory(directory);

    public void Clear()
        => PhysicalMediaWebSocketProjectionService.DirectoryCache.Clear();
}
