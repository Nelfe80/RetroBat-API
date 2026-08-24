using System.Runtime.Versioning;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Lot 2 « perso-live ». Dépose un .MEM PERSO dans <c>resources\ram\.user\&lt;système&gt;\&lt;slug&gt;.MEM</c>
/// et bascule le flag <c>PERSO</c> du wrapper (<c>wrapper\.env</c>). Le wrapper arbitre alors
/// <c>contest &gt; perso &gt; officiel</c> : un contributeur teste son .MEM EN JEU avant publication.
///
/// Une session perso reste NON certifiable : le wrapper émet <c>mem_source="perso"</c> dans son
/// attestation (et le <c>mem_sha256</c> diffère de l'officiel). Endpoints locaux (client natif :
/// le MEM Explorer), jamais destinés au web.
/// </summary>
[ApiController]
[Tags("NelfePlay")]
[Route("api/v1/mem/perso")]
[SupportedOSPlatform("windows")]
public sealed class MemPersoController : ControllerBase
{
    private static string RamRoot => Path.Combine(RetroBatPaths.PluginRoot, "resources", "ram");

    private readonly IEsSettingsStore _settingsStore;

    public MemPersoController(IEsSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    // Composant de chemin SÛR : pas de séparateur ni de traversée. Le wrapper lit exactement
    // le dossier <système> et le fichier <slug>.MEM - on refuse tout ce qui sortirait de là.
    private static bool Safe(string? s) =>
        !string.IsNullOrWhiteSpace(s) && s.Length <= 190
        && s.IndexOfAny(new[] { '/', '\\', ':' }) < 0 && s != "." && s != ".." && !s.Contains("..");

    /// <summary>Le mode perso est-il actif, quelle est la baseline (toggle ES), et quels .user\ sont déposés.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetStatus()
    {
        var files = new List<object>();
        var userRoot = Path.Combine(RamRoot, ".user");
        if (Directory.Exists(userRoot))
        {
            foreach (var sysDir in Directory.EnumerateDirectories(userRoot))
                foreach (var f in Directory.EnumerateFiles(sysDir, "*.MEM"))
                    files.Add(new { system = Path.GetFileName(sysDir), slug = Path.GetFileNameWithoutExtension(f) });
        }
        return Ok(new
        {
            enabled = WrapperEnvFile.ReadFlag("PERSO") == "1",
            baseline = PersoMemPreference.IsEnabled(_settingsStore),
            files
        });
    }

    /// <summary>Dépose un .MEM perso et active le mode transitoire (PERSO=1) pour le test EN JEU.</summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Deposit([FromBody] PersoMemRequest? req)
    {
        if (req is null || !Safe(req.System) || !Safe(req.Slug) || string.IsNullOrEmpty(req.Content))
            return BadRequest(new { error = "system/slug/content requis (composants de chemin sûrs)." });

        var userDir = Path.Combine(RamRoot, ".user", req.System!);
        Directory.CreateDirectory(userDir);
        var path = Path.Combine(userDir, req.Slug! + ".MEM");
        System.IO.File.WriteAllText(path, req.Content, new UTF8Encoding(false));

        // PERSO=1 pour charger le .MEM perso ; DISCOVERY=0 pour que le wrapper l'ÉVALUE normalement
        // (une session de découverte laissée armée mettrait le wrapper en RAM brute et ignorerait le .MEM).
        WrapperEnvFile.UpsertFlag("PERSO", "1");
        WrapperEnvFile.UpsertFlag("DISCOVERY", "0");
        return Ok(new { ok = true, enabled = true, path = Path.Combine(".user", req.System!, req.Slug! + ".MEM") });
    }

    /// <summary>Arrête le test perso : le flag <c>PERSO</c> revient à la BASELINE de la borne
    /// (toggle ES « Prioriser mes .MEM perso ») - donc <c>0</c> par défaut, mais <c>1</c> si
    /// l'utilisateur a explicitement activé la priorité perso. <c>?clear=1</c> supprime aussi
    /// les .user\ déposés.</summary>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Stop([FromQuery] bool clear = false)
    {
        var baseline = PersoMemPreference.IsEnabled(_settingsStore);
        WrapperEnvFile.UpsertFlag("PERSO", PersoMemPreference.EnvValue(baseline));
        var removed = 0;
        var userRoot = Path.Combine(RamRoot, ".user");
        if (clear && Directory.Exists(userRoot))
        {
            foreach (var f in Directory.EnumerateFiles(userRoot, "*.MEM", SearchOption.AllDirectories))
            {
                try { System.IO.File.Delete(f); removed++; } catch { /* best effort */ }
            }
        }
        return Ok(new { ok = true, enabled = baseline, removed });
    }

    public sealed class PersoMemRequest
    {
        public string? System { get; set; }
        public string? Slug { get; set; }
        public string? Content { get; set; }
    }
}
