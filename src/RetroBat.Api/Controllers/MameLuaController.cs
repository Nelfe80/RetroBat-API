using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Diagnostic du pont MAME Lua ingame : sessions et compteurs de
/// declenchement par adresse. Consomme par le banc de validation des .MEM
/// arcade (tools/mem-curator/mame_batch_validate.py).
/// </summary>
[ApiController]
[Tags("Game Events")]
[Route("api/v1/mamelua")]
public class MameLuaController : ControllerBase
{
    private readonly MameLuaIngameProvider? _provider;

    public MameLuaController(IEnumerable<IProvider> providers)
    {
        _provider = providers.OfType<MameLuaIngameProvider>().FirstOrDefault();
    }

    /// <summary>Diagnostic snapshot of the MAME Lua ingame bridge sessions (501 when the provider is off).</summary>
    [HttpGet("sessions")]
    public IActionResult Sessions()
        => _provider is null
            ? StatusCode(StatusCodes.Status501NotImplemented)
            : Ok(_provider.SessionsSnapshot());

    /// <summary>Arme la découverte MAME (MEM Explorer) : au prochain HELLO d'un plugin Lua, APIExpose
    /// lui répond <c>DISCOVER|host|port|hz</c> → il streame la RAM principale vers le listener TCP de
    /// l'Explorer (au lieu d'écouter des adresses connues).</summary>
    [HttpPost("discovery/arm")]
    public IActionResult ArmDiscovery([FromBody] DiscoveryArmRequest? req)
    {
        if (_provider is null) return StatusCode(StatusCodes.Status501NotImplemented);
        if (req is null || req.Port is <= 0 or >= 65536)
            return BadRequest(new { error = "port requis (1..65535)." });

        var host = string.IsNullOrWhiteSpace(req.Host) ? "127.0.0.1" : req.Host!.Trim();
        var hz = req.Hz is > 0 and <= 60 ? req.Hz : 8;
        _provider.ArmDiscovery(host, req.Port, hz);
        return Ok(new { ok = true, armed = true, host, port = req.Port, hz });
    }

    /// <summary>Désarme la découverte MAME (retour au mode WATCH runtime).</summary>
    [HttpDelete("discovery/arm")]
    public IActionResult DisarmDiscovery()
    {
        if (_provider is null) return StatusCode(StatusCodes.Status501NotImplemented);
        _provider.DisarmDiscovery();
        return Ok(new { ok = true, armed = false });
    }

    public sealed class DiscoveryArmRequest
    {
        public string? Host { get; set; }
        public int Port { get; set; }
        public int Hz { get; set; }
    }
}
