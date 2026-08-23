using System.Text;

namespace RetroBat.Api.Media;

/// <summary>
/// LOT 8 — serves a game's media over HTTP whether it lives in the canonical store OR is a user
/// binding under roms/, without ever exposing the filesystem or allowing escape. A media path is
/// turned into an OPAQUE reference ("&lt;rootId&gt;|&lt;relPath&gt;", base64url) scoped to an
/// ALLOWLIST of roots; resolving a reference re-validates the root and canonicalizes the path,
/// rejecting anything that would land outside its root (path traversal, absolute escape, unknown
/// root). The reference never carries a raw path the caller can steer, and every resolution is
/// bounded by <see cref="IsUnderRoot"/>.
/// </summary>
public sealed class GamelistMediaAssetResolver
{
    private const string RomsRootId = "roms";

    // Windows filesystems are case-insensitive; compare paths accordingly.
    private static readonly StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

    private readonly IReadOnlyDictionary<string, string> _roots;

    public GamelistMediaAssetResolver(IReadOnlyDictionary<string, string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        // Canonicalize every allowlisted root once so containment checks are exact.
        _roots = roots.ToDictionary(
            kv => kv.Key,
            kv => Path.GetFullPath(kv.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Build an opaque reference for a gamelist media value (relative to roms/&lt;systemId&gt;/
    /// or absolute), or null when it does not fall inside any allowlisted root. Existence is NOT
    /// checked here — the endpoint returns 404 for a reference whose file is gone.</summary>
    public string? BuildReference(string systemId, string? gamelistMediaPath)
    {
        if (string.IsNullOrWhiteSpace(gamelistMediaPath))
        {
            return null;
        }

        var raw = gamelistMediaPath.Trim().Replace('\\', '/');

        string candidate;
        if (Path.IsPathRooted(raw))
        {
            candidate = Path.GetFullPath(raw);
        }
        else if (_roots.TryGetValue(RomsRootId, out var romsRoot) && !string.IsNullOrWhiteSpace(systemId))
        {
            // Gamelist media paths are written relative to the game's system folder.
            candidate = Path.GetFullPath(Path.Combine(romsRoot, systemId, raw));
        }
        else
        {
            return null;
        }

        foreach (var (rootId, root) in _roots)
        {
            if (IsUnderRoot(candidate, root))
            {
                var relative = Path.GetRelativePath(root, candidate).Replace('\\', '/');
                return Encode($"{rootId}|{relative}");
            }
        }

        return null;
    }

    /// <summary>Resolve an opaque reference to a safe absolute file path under an allowlisted root,
    /// or null when the reference is malformed, escapes its root, or the file does not exist.</summary>
    public string? TryResolve(string? reference)
    {
        var decoded = Decode(reference);
        if (decoded is null)
        {
            return null;
        }

        var separator = decoded.IndexOf('|');
        if (separator <= 0 || separator == decoded.Length - 1)
        {
            return null;
        }

        var rootId = decoded[..separator];
        var relative = decoded[(separator + 1)..];

        if (!_roots.TryGetValue(rootId, out var root) || Path.IsPathRooted(relative))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsUnderRoot(full, root) || !File.Exists(full))
        {
            return null;
        }

        return full;
    }

    /// <summary>True only when <paramref name="candidate"/> (already canonical) sits at or under
    /// <paramref name="root"/>. The trailing separator stops "C:\a-evil" from matching "C:\a".</summary>
    private static bool IsUnderRoot(string candidate, string root)
    {
        if (string.Equals(candidate, root, PathComparison))
        {
            return true;
        }

        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return candidate.StartsWith(rootWithSep, PathComparison);
    }

    private static string Encode(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string? Decode(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var s = reference.Trim().Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: return null; // never a valid base64 length
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
