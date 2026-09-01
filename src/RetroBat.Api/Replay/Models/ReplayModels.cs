using System.Security.Cryptography;

namespace RetroBat.Api.Replay.Models;

// ─────────────────────────────────────────────────────────────────────────────
// Contrats Replay (CDC_DEV_NELFE_REPLAY v1.0). Le ReplayManifest est IMMUABLE
// après finalisation ; les données mutables vivent dans ReplayLocalMetadata.
// Sérialisation : System.Text.Json avec SnakeCaseLower (PascalCase -> snake_case).
// R1 : les empreintes runtime (rom/core/options) sont partielles (playback = R2).
// ─────────────────────────────────────────────────────────────────────────────

public sealed record ReplayGame(string GameId, string SystemId, string? RomGroup, string? Ruleset, string? Crc32);

public sealed record ReplayRuntime(
    string RuntimeId,
    string RetroarchVersion,
    string? RomSha256,
    string? CoreSha256,
    string? BiosSha256,
    string? CoreOptionsDigest,
    string ReplayFormat);

public sealed record ReplayObjectRef(string Sha256, long Size);

public sealed record ReplayFrames(long Start, long? RunStart, long? RunEnd, long ReplayEnd, double NominalFps);

public sealed record ReplayScoreLink(string? SubmissionHash, long? ScoreValueSnapshot);

public sealed record ReplayRecovery(bool RecoveredAfterCrash);

/// <summary>Manifeste technique immuable (schema nelfe.replay.v2).</summary>
public sealed record ReplayManifest(
    string Schema,
    string ReplayId,
    string SessionId,
    ReplayGame Game,
    DateTime CreatedAt,
    string Origin,
    ReplayRuntime Runtime,
    ReplayObjectRef Object,
    ReplayFrames Frames,
    ReplayScoreLink? ScoreLink,
    ReplayRecovery Recovery)
{
    public const string SchemaId = "nelfe.replay.v2";
}

/// <summary>
/// Indices de lancement LOCAUX (chemins ROM/core sur CETTE machine). Jamais dans le
/// manifeste (règle CDC : le manifeste ne contient aucun chemin local) — vivent dans la
/// meta locale. Capturés à l'enregistrement depuis saves/&lt;sys&gt;/libretro.&lt;core&gt;/&lt;jeu&gt;.replayN.
/// </summary>
public sealed record ReplayLaunchHint(string SystemFolder, string Core, string CoreDll, string RomPath);

/// <summary>Métadonnées locales MUTABLES (ne change jamais le manifeste technique).</summary>
public sealed record ReplayLocalMetadata(
    string Schema,
    string ReplayId,
    string Visibility,
    bool Pinned,
    string? ScoreRef,
    string? LeaderboardId,
    string PublicationState,
    DateTime LastAccessAt,
    bool CreatedByThisDevice,
    ReplayLaunchHint? Launch)
{
    public const string SchemaId = "nelfe.replay.local-meta.v1";

    public static ReplayLocalMetadata Fresh(string replayId, ReplayLaunchHint? launch = null) => new(
        SchemaId, replayId, "private", false, null, null, "local", DateTime.UtcNow, true, launch);
}

/// <summary>Entrée d'index (vue dérivée, reconstructible depuis les manifests).</summary>
public sealed record ReplayIndexEntry(string ReplayId, string GameId, DateTime CreatedAt, string ObjectSha256);

public enum ReplayRecordingState { Idle, Starting, Recording, Stopping, Finalizing, Ready, Error }

public enum ReplayPlaybackState { Idle, Resolving, Verifying, Preparing, Launching, Playing, Paused, Stopping, Finished, Error }

public enum ReplayErrorCode
{
    None,
    ReplayNotFound,
    ReplayObjectUnavailable,
    ReplayAlreadyRunning,
    GameAlreadyRunning,
    ReplayRecordStartFailed,
    ReplayFileNotStable,
    ReplayManifestInvalid,
    ReplayLaunchTimeout,
    RomNotFound,
    CoreNotFound,
    RuntimeIncompatible,
    RetroArchUnavailable,
    InternalError,
}

/// <summary>Générateur d'identifiant ULID (Crockford base32, 26 chars, triable par temps).</summary>
public static class Ulid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford (sans I L O U)

    public static string NewReplayId() => "rp_" + New();
    public static string NewSessionId() => "sess_" + New();

    public static string New()
    {
        Span<byte> b = stackalloc byte[16];
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int i = 5; i >= 0; i--) { b[i] = (byte)(ts & 0xFF); ts >>= 8; } // 48-bit temps (big-endian)
        RandomNumberGenerator.Fill(b[6..]);                                  // 80 bits d'aléa
        var chars = new char[26];
        for (int i = 0; i < 26; i++)
        {
            int val = 0;
            for (int j = 0; j < 5; j++)
            {
                int pos = i * 5 + j; // 0..129 (128 bits + 2 de padding)
                int bit = pos < 128 ? (b[pos / 8] >> (7 - (pos % 8))) & 1 : 0;
                val = (val << 1) | bit;
            }
            chars[i] = Alphabet[val];
        }
        return new string(chars);
    }
}
