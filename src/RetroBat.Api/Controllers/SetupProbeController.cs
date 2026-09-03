using System.Runtime.Versioning;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Sonde d'installation — servie par APIExpose lui-même, atteinte par NAVIGATION
/// depuis /setup (le navigateur bloque un fetch HTTPS→loopback ; une navigation,
/// non). /setup NAVIGUE ici, on teste côté serveur ce que le navigateur ne peut
/// pas voir, puis on REDIRIGE vers ?return= (le site) en portant le résultat dans
/// l'URL : rb (RetroBat/EmulationStation joignable), api (APIExpose = nous, donc
/// toujours 1), paired (machine liée) + pseudo. /setup lit ces paramètres et
/// affiche l'état + n'affiche « Lier » que si la machine n'est pas encore liée.
/// </summary>
[ApiController]
[Tags("NelfePlay")]
[SupportedOSPlatform("windows")]
public sealed class SetupProbeController : ControllerBase
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly NelfePlayDeviceStore _device;
    private readonly NelfePlayAgentService _agent;

    public SetupProbeController(IHttpClientFactory httpFactory, NelfePlayDeviceStore device, NelfePlayAgentService agent)
    {
        _httpFactory = httpFactory;
        _device = device;
        _agent = agent;
    }

    private static bool IsAllowedReturnHost(string host) =>
        host.Equals("nelfeplay.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".nelfeplay.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("nelfetech.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".nelfetech.com", StringComparison.OrdinalIgnoreCase);

    // Base du retour = site autorisé, SANS query (on repart propre : les paramètres
    // de résultat sont ajoutés ici, jamais accumulés).
    private static string SafeReturnBase(string? url)
    {
        const string fallback = "https://nelfeplay.com/";
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return fallback;
        if (u.Scheme != Uri.UriSchemeHttps || !IsAllowedReturnHost(u.Host)) return fallback;
        return u.GetLeftPart(UriPartial.Path);
    }

    [HttpGet("/setup-probe")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> Probe([FromQuery(Name = "return")] string? returnUrl, CancellationToken ct)
    {
        var ret = SafeReturnBase(returnUrl);
        var es = await EmulationStationReachableAsync(ct).ConfigureAwait(false);
        var paired = _device.IsPaired;
        var pseudo = paired ? _agent.Status.Pseudo : null;
        var deviceId = paired ? _device.DeviceId : null;   // identifie CETTE machine sur /account

        var q = "?rb=" + (es ? "1" : "0")
              + "&api=1"                                  // c'est nous : si on répond, APIExpose tourne
              + "&paired=" + (paired ? "1" : "0");
        if (!string.IsNullOrWhiteSpace(pseudo)) { q += "&pseudo=" + Uri.EscapeDataString(pseudo); }
        if (!string.IsNullOrWhiteSpace(deviceId)) { q += "&device_id=" + Uri.EscapeDataString(deviceId); }

        return Redirect(ret + q);
    }

    // EmulationStation expose une API HTTP sur :1234. On ne cherche pas un endpoint
    // précis : une RÉPONSE quelconque (même 404) prouve qu'ES écoute ; seuls un
    // refus de connexion ou un timeout signifient « absent ».
    private async Task<bool> EmulationStationReachableAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(1500));
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromMilliseconds(1500);
            using var resp = await client.GetAsync("http://127.0.0.1:1234/", cts.Token).ConfigureAwait(false);
            return true; // toute réponse HTTP = ES écoute
        }
        catch
        {
            return false; // connexion refusée / timeout = ES absent
        }
    }
}
