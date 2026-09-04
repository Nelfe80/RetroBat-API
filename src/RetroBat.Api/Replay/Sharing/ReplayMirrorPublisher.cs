using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Storage;

namespace RetroBat.Api.Replay.Sharing;

/// <summary>
/// Envoie un replay au miroir de la plateforme, sur un geste EXPLICITE.
///
/// Rien ne part tout seul. La visibilité par défaut d'un replay est privée, et publier est une
/// décision du propriétaire de la borne, jamais un effet de bord de l'enregistrement ou du
/// scellement d'un score.
///
/// On monte l'objet ET son manifeste. Sans le manifeste, une borne qui n'a jamais vu ce replay
/// ne saurait pas quel core ni quelle ROM employer, et l'objet seul serait illisible. Le
/// manifeste est conçu pour ça : identifiants canoniques et empreintes, aucun chemin local,
/// aucune donnée de machine (CDC §1.6).
/// </summary>
public sealed class ReplayMirrorPublisher
{
    private const string PublishPath = "/api/v1/agent/nelfenet/publish";
    private const string UnpublishPath = "/api/v1/agent/nelfenet/unpublish";

    private readonly IReplayManifestStore _manifests;
    private readonly IReplayObjectStore _objects;
    private readonly RetroBat.Api.Infrastructure.NelfePlayDeviceStore _devices;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ReplayMirrorPublisher> _logger;

    public ReplayMirrorPublisher(IReplayManifestStore manifests, IReplayObjectStore objects,
        RetroBat.Api.Infrastructure.NelfePlayDeviceStore devices, IConfiguration config,
        IHttpClientFactory httpFactory, ILogger<ReplayMirrorPublisher> logger)
    {
        _manifests = manifests; _objects = objects; _devices = devices;
        _config = config; _httpFactory = httpFactory; _logger = logger;
    }

    public sealed record PublishResult(bool Ok, string? Error = null);

    private string MirrorBase()
    {
        var url = _config["Replay:Share:MirrorUrl"];
        if (string.IsNullOrWhiteSpace(url)) url = RetroBat.Api.Infrastructure.NelfePlayAgentService.BaseUrl;
        return (url ?? string.Empty).TrimEnd('/');
    }

    public async Task<PublishResult> PublishAsync(string replayId, CancellationToken ct)
    {
        var manifest = _manifests.GetManifest(replayId);
        if (manifest is null) return new PublishResult(false, "REPLAY_NOT_FOUND");

        var objectPath = _objects.ObjectPath(manifest.Object.Sha256);
        if (!File.Exists(objectPath)) return new PublishResult(false, "REPLAY_OBJECT_UNAVAILABLE");

        var credential = _devices.GetCredential();
        if (string.IsNullOrWhiteSpace(credential)) return new PublishResult(false, "DEVICE_NOT_PAIRED");

        var manifestPath = _manifests.ManifestPath(replayId);
        if (!File.Exists(manifestPath)) return new PublishResult(false, "REPLAY_MANIFEST_INVALID");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5)); // un objet peut peser quelques Mo sur une liaison montante modeste

            // Le manifeste part TEL QU'ÉCRIT sur le disque : le re-sérialiser risquerait de
            // changer un octet et de casser l'identité que ce document porte.
            var manifestJson = await File.ReadAllTextAsync(manifestPath, cts.Token).ConfigureAwait(false);

            using var content = new MultipartFormDataContent
            {
                { new StringContent(manifestJson), "manifest" },
                { new StringContent(replayId), "replay_id" },
                { new StringContent(manifest.Object.Sha256), "object_sha256" },
            };
            await using var stream = File.OpenRead(objectPath);
            var file = new StreamContent(stream);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            content.Add(file, "object", manifest.Object.Sha256 + ".replay");

            var client = _httpFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var request = new HttpRequestMessage(HttpMethod.Post, MirrorBase() + PublishPath) { Content = content };
            request.Headers.Add("X-NelfePlay-Device", credential);

            using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                _logger.LogWarning("Replay : publication refusée par le miroir ({Code}) : {Body}", (int)response.StatusCode, Trim(body));
                return new PublishResult(false, "MIRROR_REFUSED");
            }

            _logger.LogInformation("Replay : {ReplayId} publié sur le miroir ({Size} octets).", replayId, manifest.Object.Size);
            return new PublishResult(true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PublishResult(false, "MIRROR_TIMEOUT");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Replay : publication vers le miroir impossible.");
            return new PublishResult(false, "MIRROR_UNAVAILABLE");
        }
    }

    public async Task<PublishResult> UnpublishAsync(string replayId, CancellationToken ct)
    {
        var manifest = _manifests.GetManifest(replayId);
        if (manifest is null) return new PublishResult(false, "REPLAY_NOT_FOUND");

        var credential = _devices.GetCredential();
        if (string.IsNullOrWhiteSpace(credential)) return new PublishResult(false, "DEVICE_NOT_PAIRED");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var client = _httpFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var request = new HttpRequestMessage(HttpMethod.Post, MirrorBase() + UnpublishPath)
            {
                Content = new StringContent($"{{\"object_sha256\":\"{manifest.Object.Sha256}\"}}",
                    System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-NelfePlay-Device", credential);
            using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? new PublishResult(true) : new PublishResult(false, "MIRROR_REFUSED");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Replay : retrait du miroir impossible.");
            return new PublishResult(false, "MIRROR_UNAVAILABLE");
        }
    }

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200];
}
