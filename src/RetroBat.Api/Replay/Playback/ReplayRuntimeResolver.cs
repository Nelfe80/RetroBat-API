using System.IO.Compression;
using System.Security.Cryptography;
using RetroBat.Api.Replay.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Playback;

/// <summary>Core + ROM résolus sur CETTE machine pour lancer un replay.</summary>
public sealed record ResolvedRuntime(string CoreDll, string RomPath, bool ExactCore);

/// <summary>
/// Résout, sur CETTE machine, le core et la ROM d'un replay À PARTIR DU MANIFESTE (R5). But :
/// que le <see cref="ReplayLaunchHint"/> local ne soit qu'un ACCÉLÉRATEUR (chemin rapide) et
/// jamais une CONDITION de lecture — un replay reçu d'un peer (NelfeNet) n'a pas de hint.
///
/// Politique SOUPLE (cf. compat .bsv, décision produit) : le CONTENU de la ROM (crc32, tel que
/// RetroArch le calcule) est le seul repère DUR ; le core est résolu par EMPREINTE (core_sha256
/// du manifeste, R4) quand elle est disponible, sinon best-effort via le hint. On ne bloque
/// JAMAIS a priori sur la version — la vérité finale de compatibilité reste la lecture elle-même
/// (les checkpoints du .bsv signalent un désync). Renvoie null seulement si on ne trouve
/// physiquement ni core ni ROM utilisables ici.
/// </summary>
public sealed class ReplayRuntimeResolver
{
    private static readonly uint[] Crc32Table = BuildCrc32Table();
    private readonly ILogger<ReplayRuntimeResolver> _logger;

    public ReplayRuntimeResolver(ILogger<ReplayRuntimeResolver> logger) => _logger = logger;

    public ResolvedRuntime? Resolve(ReplayManifest manifest, ReplayLaunchHint? hint)
    {
        var core = ResolveCore(manifest, hint, out var exact);
        if (core is null)
        {
            _logger.LogWarning("Replay resolver : aucun core utilisable pour {Id} (runtime {Rt}, core_sha256 {Sha})",
                manifest.ReplayId, manifest.Runtime.RuntimeId, Short(manifest.Runtime.CoreSha256));
            return null;
        }
        var rom = ResolveRom(manifest, hint);
        if (rom is null)
        {
            _logger.LogWarning("Replay resolver : ROM introuvable pour {Id} (crc32 {Crc})", manifest.ReplayId, manifest.Game.Crc32);
            return null;
        }
        if (!exact) _logger.LogInformation("Replay resolver : core NON identique à l'enregistrement pour {Id} — lecture best-effort (désync détecté par checkpoints).", manifest.ReplayId);
        return new ResolvedRuntime(core, rom, exact);
    }

    // Core : hint local (rapide, en préférant cores_real sans wrapper scoring) → empreinte
    // core_sha256 (scan cores_real puis cores) → null. Jamais de refus sur la version.
    private string? ResolveCore(ReplayManifest manifest, ReplayLaunchHint? hint, out bool exact)
    {
        exact = false;
        if (hint is not null && !string.IsNullOrEmpty(hint.Core))
        {
            var real = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "cores_real", hint.Core + "_libretro.dll");
            if (File.Exists(real)) { exact = true; return real; }
            if (!string.IsNullOrEmpty(hint.CoreDll) && File.Exists(hint.CoreDll)) { exact = true; return hint.CoreDll; }
        }

        var wanted = manifest.Runtime.CoreSha256;
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            foreach (var sub in new[] { "cores_real", "cores" })
            {
                var root = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", sub);
                if (!Directory.Exists(root)) continue;
                foreach (var dll in Directory.EnumerateFiles(root, "*_libretro.dll"))
                {
                    if (string.Equals(HashFileQuiet(dll), wanted, StringComparison.OrdinalIgnoreCase)) { exact = true; return dll; }
                }
            }
        }
        return null;
    }

    // ROM : hint local (rapide) → scan roms/<système> par crc32 de CONTENU (== celui de RetroArch,
    // décompressé pour un .zip). Sans dossier système connu (peer), on ne scanne pas globalement :
    // le mapping systemId→dossier reste à brancher (es_systems) avant le transfert d'objet NelfeNet.
    private string? ResolveRom(ReplayManifest manifest, ReplayLaunchHint? hint)
    {
        if (hint is not null && !string.IsNullOrEmpty(hint.RomPath) && File.Exists(hint.RomPath)) return hint.RomPath;

        var crc = manifest.Game.Crc32;
        var systemFolder = hint?.SystemFolder;
        if (string.IsNullOrWhiteSpace(crc) || string.IsNullOrWhiteSpace(systemFolder)) return null;

        var romDir = Path.Combine(RetroBatPaths.RomsRoot, systemFolder);
        if (!Directory.Exists(romDir)) return null;

        foreach (var f in Directory.EnumerateFiles(romDir))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".txt" or ".xml" or ".dat" or ".jpg" or ".png" or ".srm" or ".state" or ".cfg") continue;
            if (string.Equals(ContentCrc32(f), crc, StringComparison.OrdinalIgnoreCase)) return f;
        }
        return null;
    }

    // crc32 du CONTENU : pour un .zip, l'entrée ROM décompressée (comme RetroArch) ; sinon le fichier.
    private static string? ContentCrc32(string path)
    {
        try
        {
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var archive = ZipFile.OpenRead(path);
                ZipArchiveEntry? entry = null;
                foreach (var e in archive.Entries)
                {
                    if (e.Length > 0 && !e.FullName.EndsWith('/')) { entry = e; break; }
                }
                if (entry is null) return null;
                using var s = entry.Open();
                return Crc32Stream(s);
            }
            using var fs = File.OpenRead(path);
            return Crc32Stream(fs);
        }
        catch { return null; }
    }

    private static string Crc32Stream(Stream s)
    {
        var crc = 0xFFFFFFFFu;
        var buf = new byte[81920];
        int r;
        while ((r = s.Read(buf, 0, buf.Length)) > 0)
        {
            for (var i = 0; i < r; i++) { crc = (crc >> 8) ^ Crc32Table[(crc ^ buf[i]) & 0xFF]; }
        }
        return (crc ^ 0xFFFFFFFFu).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? HashFileQuiet(string path)
    {
        try { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }
        catch { return null; }
    }

    private static string Short(string? sha) => string.IsNullOrEmpty(sha) ? "-" : sha[..Math.Min(8, sha.Length)];

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++) { crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1; }
            table[i] = crc;
        }
        return table;
    }
}
