using System.Runtime.Versioning;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Relais LOCAL MEM Explorer -> NelfePlay pour la contribution communautaire de .MEM.
/// MEM Explorer ne détient jamais de credential : APIExpose relaie avec son appairage machine↔compte
/// (l'attribution du proposeur = le compte appairé de la borne).
/// </summary>
[ApiController]
[Tags("MemExplorer")]
[Route("api/v1/mem")]
[SupportedOSPlatform("windows")]
public sealed class MemContribController : ControllerBase
{
    private readonly NelfePlayAgentService _agent;

    public MemContribController(NelfePlayAgentService agent) => _agent = agent;

    /// <summary>Découvertes communauté (candidats en test) d'un jeu.</summary>
    [HttpGet("candidates")]
    public async Task<IActionResult> Candidates([FromQuery] string? system, [FromQuery] string? grp, CancellationToken ct)
    {
        var (status, body) = await _agent.MemCandidatesAsync(system ?? "", grp ?? "", ct);
        return new ContentResult { StatusCode = status, Content = body, ContentType = "application/json" };
    }

    /// <summary>Soumettre une contribution .MEM (corps JSON {system,grp,game_title,note,content}).</summary>
    [HttpPost("contribute")]
    public async Task<IActionResult> Contribute(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync(ct);
        var (status, body) = await _agent.MemContributeAsync(json, ct);
        return new ContentResult { StatusCode = status, Content = body, ContentType = "application/json" };
    }

    /// <summary>Voter le test d'un candidat (corps JSON {candidate_id,result,note}).</summary>
    [HttpPost("candidate-test")]
    public async Task<IActionResult> CandidateTest(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync(ct);
        var (status, body) = await _agent.MemCandidateTestAsync(json, ct);
        return new ContentResult { StatusCode = status, Content = body, ContentType = "application/json" };
    }
}
