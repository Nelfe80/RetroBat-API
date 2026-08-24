namespace RetroBat.Api.Media;

/// <summary>LOT 9 — how the migration relocates one asset.</summary>
public enum MigrationTransferMode
{
    /// <summary>Non-destructive: the source stays in roms/, a verified copy lands in the store.</summary>
    Copy,

    /// <summary>Destructive: the source is deleted, but ONLY after a verified copy is in place.</summary>
    Move
}

/// <summary>Outcome of a transfer. <see cref="Success"/> false means the source was left untouched.</summary>
public readonly record struct MediaTransferResult(bool Success, string? Reason)
{
    public static readonly MediaTransferResult SourceMissing = new(false, "source-missing");
    public static readonly MediaTransferResult HashMismatch = new(false, "hash-mismatch");
    public static readonly MediaTransferResult Ok = new(true, null);
}

/// <summary>
/// LOT 9 — the one primitive that relocates a media file, safe by construction. It copies the source
/// to a temp beside the target, verifies the copy's SHA-256 matches the source, atomically swaps it
/// into place, and — for <see cref="MigrationTransferMode.Move"/> only, and only once that verified
/// copy is in place — deletes the source. A hash mismatch or any failure leaves the source intact and
/// removes the partial target: the source is NEVER deleted before its replacement is proven good.
/// </summary>
public static class MediaFileTransfer
{
    public static async Task<MediaTransferResult> TransferAsync(
        string sourcePath,
        string targetPath,
        MigrationTransferMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            return MediaTransferResult.SourceMissing;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var temp = targetPath + ".migrating.tmp";

        try
        {
            File.Copy(sourcePath, temp, overwrite: true);

            var sourceHash = await JsonMediaAliasStore.ComputeSha256Async(sourcePath, cancellationToken);
            var tempHash = await JsonMediaAliasStore.ComputeSha256Async(temp, cancellationToken);
            if (!string.Equals(sourceHash, tempHash, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(temp);
                return MediaTransferResult.HashMismatch; // source untouched
            }

            // The verified copy replaces the target atomically.
            if (File.Exists(targetPath))
            {
                File.Replace(temp, targetPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, targetPath);
            }

            if (mode == MigrationTransferMode.Move)
            {
                // Destructive step, reached only now: the target is verified and in place.
                File.Delete(sourcePath);
            }

            return MediaTransferResult.Ok;
        }
        catch
        {
            TryDelete(temp);
            throw; // caller records the failure; source is left intact
        }
    }

    public static MigrationTransferMode ParseMode(string? mode) => (mode ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "move" => MigrationTransferMode.Move,
        _ => MigrationTransferMode.Copy
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort cleanup of the temp copy
        }
    }
}
