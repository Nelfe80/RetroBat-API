using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Models;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace RetroBat.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class HiscoresController : ControllerBase
{
    private readonly ILogger<HiscoresController> _logger;
    private readonly ApiContext _context;
    // Tools folder
    private readonly string _toolsDir = @"C:\RetroBat\plugins\APIExpose\tools";

    public HiscoresController(ILogger<HiscoresController> logger, ApiContext context)
    {
        _logger = logger;
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> GetHiscore([FromQuery] string? ids, [FromQuery] string? md5)
    {
        // Si aucun argument, on se replie sur le jeu en cours
        GameReference? targetGame = null;
        if (string.IsNullOrEmpty(ids) && string.IsNullOrEmpty(md5))
        {
            targetGame = _context.Ui.Running ?? _context.Ui.Selected;
            if (targetGame == null)
            {
                return BadRequest(new { message = "You must provide an 'ids' or 'md5' parameter, or have a running/selected game." });
            }
        }
        else
        {
            // Vérifier si la requête id/md5 correspond au jeu courant
            var current = _context.Ui.Running ?? _context.Ui.Selected;
            if (current != null && (current.GameId == ids || current.GameId == md5 || current.Details?.Md5 == md5 || current.Details?.Md5 == ids))
            {
                targetGame = current;
            }
            
            if (targetGame == null)
            {
                return NotFound(new { message = "Fetching an arbitrary hiscore not matching the current game is currently pending external search functionality." });
            }
        }

        _logger.LogInformation($"[Hiscores] Requested hiscore for target game {targetGame.GameName}");

        var executable = Path.Combine(_toolsDir, "hi2txt", "hi2txt.exe");
        if (!System.IO.File.Exists(executable))
        {
            executable = Path.Combine(_toolsDir, "hi2txt.exe"); // fallback local
        }

        if (!System.IO.File.Exists(executable))
        {
            return StatusCode(500, new { message = $"hi2txt.exe is missing. Expected at {executable}" });
        }

        // Logic de récupération du fichier de sauvegarde
        var romPath = targetGame.GamePath;
        var romName = Path.GetFileNameWithoutExtension(romPath);
        var systemId = targetGame.SystemId;

        string[] types = { "nvram", "saveram", "eeprom" };
        string hsFile = "";
        string hsType = "";

        string[] searchSystems = new[] { systemId, "mame" }.Distinct().ToArray();
        
        foreach (var sys in searchSystems)
        {
            var nvramDir = Path.Combine(@"C:\RetroBat\saves", sys, "nvram", romName);
            if (Directory.Exists(nvramDir))
            {
                foreach (var t in types)
                {
                    var file = Path.Combine(nvramDir, t);
                    if (System.IO.File.Exists(file))
                    {
                        hsFile = file;
                        hsType = t;
                        break;
                    }
                }
            }
            if (!string.IsNullOrEmpty(hsFile)) break;
        }
        
        // Tester aussi le dossier 'hi' s'il n'y a pas de nvram
        if (string.IsNullOrEmpty(hsFile))
        {
            foreach (var sys in searchSystems)
            {
                var hiFile = Path.Combine(@"C:\RetroBat\saves", sys, "hi", romName + ".hi");
                if (System.IO.File.Exists(hiFile))
                {
                    hsFile = hiFile;
                    hsType = "hi";
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(hsFile))
        {
             return Ok(new {
                queryId = ids,
                queryMd5 = md5,
                game = targetGame.GameName,
                status = "not_found",
                message = "No hiscore file (nvram, saveram, eeprom, .hi) found for this game.",
                ts = DateTime.UtcNow
            });
        }

        // Exécution du binaire
        try
        {
            var pInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"-r \"{hsFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(pInfo);
            if (process == null) return StatusCode(500, new { message = "Failed to start hi2txt.exe" });

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                 return StatusCode(500, new { 
                     message = "hi2txt.exe execution failed", 
                     exitCode = process.ExitCode,
                     error = error 
                 });
            }

            // Parsing du tableau résultat
            var scores = new List<object>();
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            
            bool isHeader = true;
            foreach (var line in lines)
            {
                var parts = line.Split('|');
                if (parts.Length >= 3)
                {
                    if (isHeader) 
                    {
                         if (parts[0].Trim().ToLower() == "rank")
                         {
                             // on saute l'entête
                             isHeader = false;
                             continue;
                         }
                         isHeader = false;
                    }
                    
                    scores.Add(new {
                        rank = parts[0].Trim(),
                        score = parts[1].Trim(),
                        name = parts[2].Trim()
                    });
                }
            }

            return Ok(new {
                romName = romName,
                romPath = targetGame.GamePath,
                system = systemId,
                status = "ok",
                sourceType = hsType,
                sourceFile = hsFile,
                scores = scores,
                updatedAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Hiscores] Error executing hi2txt");
            return StatusCode(500, new { message = "Error executing hi2txt", exception = ex.Message });
        }
    }
}
