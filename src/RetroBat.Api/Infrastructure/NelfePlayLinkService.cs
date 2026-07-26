using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Connecter cette machine a un compte, sans rien faire recopier.
///
/// L'appairage demandait de lire un code sur nelfeplay.com et de le taper ici.
/// C'est une friction qui fait renoncer, et elle n'apportait rien : cette
/// machine possede DEJA une identite solide — une cle privee dans le coffre du
/// systeme. Ce qui lui manque, c'est un lien vers une personne.
///
/// Elle DEMANDE donc, et une personne connectee ACCORDE. On ouvre une demande,
/// on ouvre l'adresse dans le navigateur du poste, et on attend. Un clic
/// la-bas, et le credential arrive ici.
///
/// Deux secrets circulent, et c'est essentiel : l'identifiant de la demande
/// voyage dans l'adresse — le joueur la voit, peut la recopier — tandis que le
/// SECRET DE RETRAIT ne quitte jamais cette machine. Sans cette separation,
/// quiconque apercevrait l'adresse pourrait reclamer le credential a notre
/// place.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NelfePlayLinkService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly NelfePlayDeviceStore _device;
    private readonly NelfePlayAgentService _agent;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<NelfePlayLinkService> _logger;

    private readonly object _sync = new();
    private LinkAttempt? _current;

    public NelfePlayLinkService(
        NelfePlayDeviceStore device,
        NelfePlayAgentService agent,
        IHttpClientFactory httpFactory,
        ILogger<NelfePlayLinkService> logger)
    {
        _device = device;
        _agent = agent;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>Etat courant, tel que la page /link l'affiche.</summary>
    public LinkState State
    {
        get
        {
            lock (_sync)
            {
                if (_device.IsPaired)
                {
                    return new LinkState("linked", null, _agent.MachineLabel);
                }

                return _current is null
                    ? new LinkState("idle", null, _agent.MachineLabel)
                    : new LinkState(_current.Status, _current.Url, _agent.MachineLabel);
            }
        }
    }

    /// <summary>
    /// Ouvre une demande et rend l'adresse a ouvrir.
    ///
    /// Une demande deja en cours est REUTILISEE : recliquer ne doit pas en
    /// empiler une seconde, sous peine de laisser le joueur devant deux
    /// adresses dont une seule marchera.
    /// </summary>
    public async Task<LinkState> StartAsync(CancellationToken cancellationToken)
    {
        if (_device.IsPaired)
        {
            return State;
        }

        lock (_sync)
        {
            if (_current is { Status: "pending" })
            {
                return new LinkState("pending", _current.Url, _agent.MachineLabel);
            }
        }

        try
        {
            using var client = CreateClient();
            using var response = await client.PostAsJsonAsync(
                "/api/v1/agent/link-request",
                new
                {
                    label = _agent.MachineLabel,
                    public_key = _device.ExportPublicKeyPem(),
                    key_algorithm = NelfePlayDeviceStore.KeyAlgorithm,
                },
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Nelfe Play : demande de liaison refusee ({Status}).", (int)response.StatusCode);
                return new LinkState("error", null, _agent.MachineLabel);
            }

            var opened = await response.Content
                .ReadFromJsonAsync<LinkRequestResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (opened is null || string.IsNullOrEmpty(opened.Id) || string.IsNullOrEmpty(opened.ClaimSecret))
            {
                return new LinkState("error", null, _agent.MachineLabel);
            }

            lock (_sync)
            {
                _current = new LinkAttempt(opened.Id, opened.ClaimSecret, opened.Url ?? string.Empty);
            }

            _logger.LogInformation("Nelfe Play : ouvrez {Url} pour connecter cette machine.", opened.Url);

            return new LinkState("pending", opened.Url, _agent.MachineLabel);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Nelfe Play : demande de liaison impossible.");
            return new LinkState("error", null, _agent.MachineLabel);
        }
    }

    /// <summary>
    /// Regarde si l'accord est venu, et prend le credential le cas echeant.
    ///
    /// C'est la PAGE qui interroge, pas une boucle de fond : tant que personne
    /// ne regarde, il n'y a aucune raison de solliciter le serveur.
    /// </summary>
    public async Task<LinkState> PollAsync(CancellationToken cancellationToken)
    {
        LinkAttempt? attempt;
        lock (_sync)
        {
            attempt = _current;
        }

        if (_device.IsPaired || attempt is null)
        {
            return State;
        }

        try
        {
            using var client = CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent/link-request/" + attempt.Id);
            request.Headers.Add("X-NelfePlay-Claim", attempt.Secret);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // 202 : personne n'a encore accorde. C'est l'etat normal tant que le
            // joueur n'a pas cliqué, pas une erreur.
            if ((int)response.StatusCode == 202)
            {
                return new LinkState("pending", attempt.Url, _agent.MachineLabel);
            }

            if (!response.IsSuccessStatusCode)
            {
                lock (_sync)
                {
                    // Expiree ou deja retiree : on repart de zero plutot que de
                    // laisser le joueur devant une adresse morte.
                    _current = null;
                }
                return new LinkState("expired", null, _agent.MachineLabel);
            }

            var claimed = await response.Content
                .ReadFromJsonAsync<LinkClaimResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (claimed is null || string.IsNullOrEmpty(claimed.Credential) || string.IsNullOrEmpty(claimed.DeviceId))
            {
                return new LinkState("error", attempt.Url, _agent.MachineLabel);
            }

            _device.SavePairing(claimed.DeviceId, claimed.Credential);
            lock (_sync)
            {
                _current = null;
            }

            // On va chercher tout de suite ce qui attend : le joueur vient
            // d'autoriser, il s'attend a voir ses jeux arriver, pas a patienter
            // jusqu'au prochain releve.
            await _agent.PollAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Nelfe Play : machine connectee ({Label}).", claimed.Label);

            return new LinkState("linked", null, _agent.MachineLabel);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Nelfe Play : verification de liaison impossible.");
            return new LinkState("pending", attempt.Url, _agent.MachineLabel);
        }
    }

    private HttpClient CreateClient()
    {
        var client = _httpFactory.CreateClient(nameof(NelfePlayLinkService));
        client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
        client.Timeout = TimeSpan.FromSeconds(15);

        return client;
    }

    private sealed record LinkAttempt(string Id, string Secret, string Url)
    {
        public string Status => "pending";
    }

    /// <param name="Status">idle | pending | linked | expired | error</param>
    public sealed record LinkState(string Status, string? Url, string Label);

    private sealed class LinkRequestResponse
    {
        public string Id { get; set; } = string.Empty;
        public string ClaimSecret { get; set; } = string.Empty;
        public string? Url { get; set; }
        public int ExpiresIn { get; set; }
    }

    private sealed class LinkClaimResponse
    {
        public string Status { get; set; } = string.Empty;
        public string Credential { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
    }
}
