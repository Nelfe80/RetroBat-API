using System.Runtime.Versioning;
using System.Text.Json;
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
    private readonly IHttpClientFactory _httpFactory;
    private readonly NelfePlayScoringSessionService _scoringSession;

    public NelfePlayController(
        NelfePlayDeviceStore device,
        NelfePlayAgentService agent,
        IHttpClientFactory httpFactory,
        NelfePlayScoringSessionService scoringSession)
    {
        _device = device;
        _agent = agent;
        _httpFactory = httpFactory;
        _scoringSession = scoringSession;
    }

    /// <summary>
    /// Joueur de SESSION sur la borne (Lot 6 / Station). HubManager l'appelle au
    /// check-in avec le code joueur RGPC + la fiche salle (nom/ville), et au checkout
    /// avec un code vide. Le record certifié joué pendant la session sera attribué à
    /// CE joueur et portera la provenance de l'enseigne.
    /// </summary>
    [HttpPost("scoring-session")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult SetScoringSession([FromBody] ScoringSessionRequest request)
    {
        _scoringSession.Set(
            request?.PlayerCode, request?.VenueName, request?.VenueCity,
            string.IsNullOrWhiteSpace(request?.World) ? "station" : request!.World!,
            request?.Channel, request?.ContestId);
        var current = _scoringSession.Get();
        return Ok(new { active = current is not null, world = current?.World, venue = current?.VenueName, channel = current?.Channel, contestId = current?.ContestId });
    }

    /// <summary>Retire le joueur de session (checkout).</summary>
    [HttpDelete("scoring-session")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult ClearScoringSession()
    {
        _scoringSession.Clear();
        return Ok(new { active = false });
    }

    /// <summary>Lit la session de scoring ARMÉE en direct (diagnostic) : monde
    /// (home si null / station / stream), joueur RGPC, salle, chaîne, contest.
    /// Sert à vérifier qu'un contest est bien armé avant de jouer.</summary>
    [HttpGet("scoring-session")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetScoringSession()
    {
        var s = _scoringSession.Get();
        return Ok(new
        {
            active = s is not null,
            world = s?.World ?? "home",
            playerCode = s?.PlayerCode,
            venue = s?.VenueName,
            channel = s?.Channel,
            contestId = s?.ContestId,
        });
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
            pseudo = status.Pseudo,
            entitlements = status.Entitlements,
            lastPollUtc = status.LastPollUtc,
            lastInstalled = status.LastInstalled,
            // Sans ce chiffre, « 0 installe » se lit comme un echec alors qu'il
            // veut presque toujours dire « rien a faire ». Zero installe ET zero
            // en attente est un systeme au repos, pas un systeme en panne.
            pendingIntents = status.PendingIntents,
            lastError = status.LastError,
        });
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

    /// <summary>
    /// Bloc « ta position » de la page /records : cette borne demande à Nelfe Play le
    /// rang de SON meilleur score (credential appairé ?? anonyme), pour que la page
    /// statique — chargée dans le navigateur de la borne — affiche « #N sur M » et le
    /// bouton d'identification. Appelable depuis nelfeplay.com (CORS) ; localhost seul
    /// (127.0.0.1) est joignable depuis une page HTTPS sans blocage « mixed content ».
    /// </summary>
    [HttpGet("records/me")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> RecordsMe(
        [FromQuery(Name = "rom_group")] string? romGroup,
        [FromQuery] string? ruleset,
        CancellationToken cancellationToken)
    {
        var identifyUrl = $"http://127.0.0.1:12345/link";
        var credential = ResolveCredential();
        if (string.IsNullOrEmpty(credential) || string.IsNullOrEmpty(romGroup))
        {
            // Pas de scoring configuré : on renvoie quand même de quoi afficher le CTA.
            return Ok(new { present = false, paired = _device.IsPaired, identify_url = identifyUrl });
        }

        try
        {
            var client = _httpFactory.CreateClient();
            client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
            client.DefaultRequestHeaders.Add("X-Nelfeplay-Device", credential);
            var url = $"/api/v1/agent/scores/my-rank?rom_group={Uri.EscapeDataString(romGroup)}"
                    + $"&ruleset={Uri.EscapeDataString(string.IsNullOrEmpty(ruleset) ? "1cc" : ruleset)}";
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Ok(new { present = false, paired = _device.IsPaired, identify_url = identifyUrl });
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = doc.RootElement;
            int? Rank(string k) => root.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : null;

            return Ok(new
            {
                present = true,
                paired = _device.IsPaired,
                anonymous = root.TryGetProperty("anonymous", out var a) && a.GetBoolean(),
                score = Rank("score"),
                rank = Rank("rank"),
                of = Rank("of") ?? 0,
                identify_url = identifyUrl,
            });
        }
        catch
        {
            return Ok(new { present = false, paired = _device.IsPaired, identify_url = identifyUrl });
        }
    }

    // Credential des appels sortants : appairé, sinon l'anonyme (anonymous.json).
    private string? ResolveCredential()
    {
        var paired = _device.GetCredential();
        if (!string.IsNullOrEmpty(paired))
        {
            return paired;
        }

        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "anonymous.json");
            if (System.IO.File.Exists(path))
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("credential", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    return c.GetString();
                }
            }
        }
        catch { /* pas d'anonyme : tant pis */ }

        return null;
    }

    public sealed record LabelRequest(string? Label);

    public sealed record ScoringSessionRequest(
        string? PlayerCode, string? VenueName, string? VenueCity,
        string? World = null, string? Channel = null, string? ContestId = null);
}
