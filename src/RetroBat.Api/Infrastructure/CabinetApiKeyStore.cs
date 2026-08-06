using System.Security.Cryptography;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Per-cabinet API key. The shipped appsettings ships NO key (never commit a secret);
/// when <c>Security:ApiKey</c> is empty this generates a unique key on first run and
/// persists it under <c>state/</c>, so non-loopback callers (the Fleet Hub over the LAN)
/// stay gated with a key that differs on every machine. Loopback callers are never gated,
/// so a generation failure degrades safely to loopback-only.
/// </summary>
public static class CabinetApiKeyStore
{
    private static string KeyPath => Path.Combine(RetroBatPaths.PluginRoot, "state", "cabinet-api-key.txt");

    /// <summary>Returns the persisted key, creating one the first time. Empty on failure
    /// (treated by the pipeline as "no key" = loopback-only, never a hard stop).</summary>
    public static string GetOrCreate()
    {
        var path = KeyPath;
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length >= 16)
                {
                    return existing;
                }
            }

            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(20)); // 40 hex chars
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, key);
            return key;
        }
        catch
        {
            return string.Empty;
        }
    }
}
