using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RetroBat.Api.Media;

/// <summary>
/// Compares two media files by CONTENT, not by path. A regional alias that resolves to
/// byte-identical bytes (screentitle.png → screentitle-wor.png) is not a real change and
/// must never trigger a /addgames refresh; a genuinely different image (front.png →
/// front-eu.png) is. Fingerprints are cached by absolute path + size + last-write time so
/// a card revisited many times does not re-hash the same files.
/// </summary>
public static class MediaFileContentComparer
{
    private static readonly ConcurrentDictionary<string, string> HashCache = new(StringComparer.Ordinal);

    /// <summary>True when both disk paths point to the same file or to byte-identical
    /// content. Two empty paths are equivalent (nothing either way); one empty and one
    /// present are different (a media was added or removed).</summary>
    public static bool AreEquivalent(string? firstPath, string? secondPath)
    {
        var firstEmpty = string.IsNullOrWhiteSpace(firstPath);
        var secondEmpty = string.IsNullOrWhiteSpace(secondPath);
        if (firstEmpty || secondEmpty)
        {
            return firstEmpty && secondEmpty;
        }

        string firstFull, secondFull;
        try
        {
            firstFull = Path.GetFullPath(firstPath!);
            secondFull = Path.GetFullPath(secondPath!);
        }
        catch
        {
            return false;
        }

        // same normalized path: identical
        if (string.Equals(firstFull, secondFull, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var firstInfo = new FileInfo(firstFull);
            var secondInfo = new FileInfo(secondFull);
            // absent, or different sizes: different
            if (!firstInfo.Exists || !secondInfo.Exists || firstInfo.Length != secondInfo.Length)
            {
                return false;
            }

            // same size: compare the SHA-256 fingerprints
            var firstHash = ComputeHash(firstInfo);
            var secondHash = ComputeHash(secondInfo);
            return firstHash != null && string.Equals(firstHash, secondHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string? ComputeHash(FileInfo info)
    {
        var cacheKey = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        if (HashCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        try
        {
            using var stream = File.Open(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            HashCache[cacheKey] = hash;
            return hash;
        }
        catch
        {
            return null;
        }
    }
}
