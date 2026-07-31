using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroBat.Api.Scoring;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Scoring certifié — côté agent. Ce service :
///  1) enrôle, une fois, la clé de signature scoring de l'appareil (CNG/NCrypt) ;
///  2) demande un ticket de session au lancement d'un jeu ;
///  3) capte l'attestation du listener et le score final sur le bus.
///
/// La SOUMISSION du passeport signé (assemblage complet + POST /scores/submissions)
/// est l'étape suivante (3c) : elle exige que le listener homologué émette la
/// SESSION COMPLÈTE mesurée (checkpoints, timing, sensitive, empreintes des modules),
/// pas seulement l'attestation, et la résolution du profil ouvert côté serveur. Ce
/// service pose la moitié « identité/session » qui est prête et testable dès à présent.
/// </summary>
public sealed class NelfePlayScoringReporter : BackgroundService
{
    public const string ScoringKeyName = "Nelfe.Scoring.Device";

    private readonly IEventBus _eventBus;
    private readonly IHttpClientFactory _httpFactory;
    private readonly NelfePlayDeviceStore _devices;
    private readonly ILogger<NelfePlayScoringReporter>? _logger;

    private IDisposable? _subscription;
    private readonly object _sync = new();

    // État de session courante.
    private string? _enrolledKeyId;
    private string? _listenerSha256;
    private string? _coreSha256;
    private string? _memSha256;
    private string? _sessionNonce;
    private JsonElement? _ticket;
    private long? _lastScore;

    public static bool Enabled { get; set; } = true;

    public NelfePlayScoringReporter(
        IEventBus eventBus,
        IHttpClientFactory httpFactory,
        NelfePlayDeviceStore devices,
        ILogger<NelfePlayScoringReporter>? logger = null)
    {
        _eventBus = eventBus;
        _httpFactory = httpFactory;
        _devices = devices;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = _eventBus.Subscribe<EventEnvelope>(HandleEvent);
        try
        {
            await EnsureEnrolledAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Arrêt normal.
        }
        finally
        {
            _subscription?.Dispose();
        }
    }

    private void HandleEvent(EventEnvelope envelope)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            switch (envelope.Type?.ToLowerInvariant())
            {
                case "ui.game.started":
                    ResetSession();
                    _ = RequestTicketAsync(CancellationToken.None);
                    break;
                case "scoring.listener.attestation":
                    CaptureAttestation(envelope.Payload);
                    break;
                case "retroarch.score":
                    CaptureScore(envelope.Payload);
                    break;
                case "ui.game.ended":
                    LogSessionReady();
                    break;
            }
        }
        catch (Exception ex)
        {
            // Un incident de scoring ne doit jamais gêner la partie en cours.
            _logger?.LogDebug(ex, "Scoring: événement {Type} ignoré", envelope.Type);
        }
    }

    private void ResetSession()
    {
        lock (_sync)
        {
            _listenerSha256 = _coreSha256 = _memSha256 = _sessionNonce = null;
            _ticket = null;
            _lastScore = null;
        }
    }

    private void CaptureAttestation(object? payload)
    {
        var root = ToJson(payload);
        lock (_sync)
        {
            _listenerSha256 = GetString(root, "ListenerSha256");
            _coreSha256 = GetString(root, "CoreSha256");
            _memSha256 = GetString(root, "MemSha256");
            _sessionNonce = GetString(root, "SessionNonce");
        }
        _logger?.LogDebug("Scoring: attestation captée (listener {Listener})", _listenerSha256);
    }

    private void CaptureScore(object? payload)
    {
        var root = ToJson(payload);
        if (root.TryGetProperty("Value", out var v) && v.TryGetInt64(out var score))
        {
            lock (_sync)
            {
                // Le score FINAL est le dernier connu à la fin de partie (result_source=final).
                _lastScore = score;
            }
        }
    }

    /// <summary>
    /// À l'appairage / au démarrage : la clé de signature scoring (CNG/NCrypt) est
    /// créée si absente, puis sa clé publique est enrôlée côté serveur. Une fois.
    /// </summary>
    private async Task EnsureEnrolledAsync(CancellationToken cancellationToken)
    {
        var credential = _devices.GetCredential();
        if (string.IsNullOrEmpty(credential))
        {
            return; // scoring identifié = appareil appairé
        }

        try
        {
            using var deviceKey = CngDeviceKey.OpenOrCreate(ScoringKeyName);
            if (_enrolledKeyId == deviceKey.KeyId)
            {
                return;
            }

            using var client = CreateClient(credential);
            using var content = new StringContent(deviceKey.PublicKeyPem, Encoding.ASCII, "application/x-pem-file");
            using var response = await client
                .PostAsync("/api/v1/agent/scores/enroll-key", content, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _enrolledKeyId = deviceKey.KeyId;
                _logger?.LogInformation("Scoring: clé d'appareil enrôlée (key_id {KeyId}).", deviceKey.KeyId);
            }
            else
            {
                _logger?.LogDebug("Scoring: enrôlement refusé ({Status}).", (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Scoring: enrôlement de la clé impossible");
        }
    }

    private async Task RequestTicketAsync(CancellationToken cancellationToken)
    {
        var credential = _devices.GetCredential();
        if (string.IsNullOrEmpty(credential))
        {
            return;
        }

        await EnsureEnrolledAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var client = CreateClient(credential);
            using var response = await client
                .PostAsync("/api/v1/agent/scores/ticket", content: null, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogDebug("Scoring: ticket refusé ({Status}).", (int)response.StatusCode);
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("ticket", out var ticket))
            {
                lock (_sync)
                {
                    _ticket = ticket.Clone();
                }
                _logger?.LogDebug("Scoring: ticket de session obtenu.");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Scoring: demande de ticket impossible");
        }
    }

    /// <summary>
    /// Fin de partie : on journalise ce qui est capté. L'assemblage complet du
    /// passeport + POST /scores/submissions est l'étape 3c (dépend de la session
    /// complète émise par le listener et de la résolution du profil ouvert).
    /// </summary>
    private void LogSessionReady()
    {
        lock (_sync)
        {
            var ready = _listenerSha256 is not null && _ticket is not null && _lastScore is not null;
            _logger?.LogInformation(
                "Scoring: fin de partie — attestation={HasAtt} ticket={HasTicket} score={Score}. " +
                "Soumission du passeport = étape 3c (session complète + profil ouvert).",
                _listenerSha256 is not null, _ticket is not null, _lastScore);
        }
    }

    private HttpClient CreateClient(string credential)
    {
        var client = _httpFactory.CreateClient(nameof(NelfePlayScoringReporter));
        client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Add("X-NelfePlay-Device", credential);
        return client;
    }

    private static JsonElement ToJson(object? payload)
        => JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
