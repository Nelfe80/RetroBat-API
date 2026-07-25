using System.Runtime.Versioning;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Appairage de cette machine avec un compte Nelfe Play, et etat de l'agent.
/// Le joueur genere un code sur nelfeplay.com et le saisit ici : la borne
/// echange ce code contre son credential durable, puis va chercher elle-meme
/// les jeux acquis. Rien n'est jamais pousse depuis l'exterieur.
/// </summary>
[ApiController]
[Tags("NelfePlay")]
[Route("api/v1/nelfeplay")]
[SupportedOSPlatform("windows")]
public sealed class NelfePlayController : ControllerBase
{
    private readonly NelfePlayDeviceStore _device;
    private readonly NelfePlayAgentService _agent;

    public NelfePlayController(NelfePlayDeviceStore device, NelfePlayAgentService agent)
    {
        _device = device;
        _agent = agent;
    }

    /// <summary>Etat de l'appairage et du dernier releve.</summary>
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetStatus()
    {
        var status = _agent.Status;
        return Ok(new
        {
            paired = _device.IsPaired,
            deviceId = _device.DeviceId,
            label = _agent.MachineLabel,
            playerCode = status.PlayerCode,
            entitlements = status.Entitlements,
            lastPollUtc = status.LastPollUtc,
            lastInstalled = status.LastInstalled,
            lastError = status.LastError,
        });
    }

    /// <summary>
    /// Appaire la machine avec le code affiche sur nelfeplay.com. La cle
    /// publique de l'appareil part avec la demande ; sa cle privee reste dans
    /// le coffre de Windows.
    /// </summary>
    /// <response code="200">Appairage reussi.</response>
    /// <response code="400">Code absent ou mal forme.</response>
    /// <response code="409">Code refuse ou expire.</response>
    [HttpPost("pair")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Pair([FromBody] PairRequest request, CancellationToken cancellationToken)
    {
        var code = (request?.Code ?? string.Empty).Trim();
        if (code.Length != 8)
        {
            return BadRequest(new { message = "Code d'appairage attendu (8 caracteres)." });
        }

        // Nom par defaut : celui que cette machine porte deja (borne nommee via
        // HubManager en salle, nom de la machine Windows chez un particulier).
        if (!string.IsNullOrWhiteSpace(request?.Label))
        {
            _agent.SetMachineLabel(request!.Label);
        }

        var label = _agent.MachineLabel;
        var paired = await _agent.PairAsync(code, label, cancellationToken);
        if (!paired)
        {
            return Conflict(new { message = "Code refuse ou expire. Generez-en un nouveau sur nelfeplay.com." });
        }

        return Ok(new { paired = true, deviceId = _device.DeviceId });
    }

    /// <summary>Force un releve immediat des jeux a installer.</summary>
    [HttpPost("sync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Sync(CancellationToken cancellationToken)
    {
        if (!_device.IsPaired)
        {
            return Conflict(new { message = "Machine non appairee." });
        }

        await _agent.PollAsync(cancellationToken);
        return Ok(new { synchronised = true, lastInstalled = _agent.Status.LastInstalled });
    }

    /// <summary>
    /// Nom de cette machine dans le compte Nelfe Play. UNE seule regle pour les
    /// deux mondes : le poste local possede son nom. En salle, HubManager
    /// appelle cette route quand l'exploitant renomme la borne ; un particulier
    /// — qui n'a pas de hub — le fixe ici, ou laisse le nom de sa machine
    /// Windows. Le nom part au prochain releve.
    /// </summary>
    /// <response code="200">Nom applique.</response>
    [HttpPost("label")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult SetLabel([FromBody] LabelRequest request)
    {
        _agent.SetMachineLabel(request?.Label);
        return Ok(new { label = _agent.MachineLabel });
    }

    /// <summary>Delie la machine du compte (l'appairage est oublie).</summary>
    [HttpPost("forget")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Forget()
    {
        _device.Forget();
        return Ok(new { paired = false });
    }

    public sealed record PairRequest(string? Code, string? Label);

    public sealed record LabelRequest(string? Label);
}
