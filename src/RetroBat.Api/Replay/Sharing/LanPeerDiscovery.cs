using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RetroBat.Api.Replay.Sharing;

/// <summary>
/// Découverte sur le réseau LOCAL, par diffusion UDP. C'est la seule porte d'entrée qui ne
/// demande aucune infrastructure : ni hub, ni annuaire en ligne, ni fichier à remplir. Dans un
/// foyer avec deux bornes, ou lors d'une soirée où l'on branche plusieurs machines, c'est elle
/// qui fait tout le travail.
///
/// Le protocole tient en deux messages, volontairement minuscules et sans secret :
///   requête  : NELFENET/1 DISCOVER
///   réponse  : NELFENET/1 PEER {"name":"...","base_url":"http://192.168.1.20:12345","device_id":"..."}
///
/// Une borne ne répond QUE si elle partage quelque chose : une machine qui ne partage rien reste
/// muette et n'apparaît nulle part. La réponse ne contient aucun secret ; découvrir une borne ne
/// donne aucun droit sur elle.
/// </summary>
public static class LanPeerProtocol
{
    public const int Port = 55360;
    public const string Discover = "NELFENET/1 DISCOVER";
    public const string PeerPrefix = "NELFENET/1 PEER ";

    public sealed record Announce(string Name, string BaseUrl, string? DeviceId, string? InstanceId);

    /// <summary>Identifiant de CE processus. La diffusion revient sur la machine qui l'a émise :
    /// sans ce repère, une borne se découvrirait elle-même et tenterait de se demander ses
    /// propres objets. Il ne survit pas au redémarrage, ce qui suffit à cet usage.</summary>
    public static readonly string InstanceId = Guid.NewGuid().ToString("N");
}

/// <summary>
/// Le RÉPONDEUR : écoute les requêtes de découverte et se présente, si et seulement si cette
/// borne partage. Il ne sert aucun contenu ; il dit seulement « je suis là, voici où me parler ».
/// </summary>
public sealed class LanPeerResponderService : BackgroundService
{
    private readonly ReplaySharePolicy _policy;
    private readonly RetroBat.Api.Infrastructure.NelfePlayDeviceStore _devices;
    private readonly IConfiguration _config;
    private readonly ILogger<LanPeerResponderService> _logger;

    public LanPeerResponderService(ReplaySharePolicy policy,
        RetroBat.Api.Infrastructure.NelfePlayDeviceStore devices, IConfiguration config,
        ILogger<LanPeerResponderService> logger)
    {
        _policy = policy; _devices = devices; _config = config; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, LanPeerProtocol.Port));
            _logger.LogInformation("Replay : répondeur de découverte LAN en écoute (UDP {Port}).", LanPeerProtocol.Port);
        }
        catch (Exception ex)
        {
            // Port occupé, pare-feu, pas de réseau : la découverte LAN est un CONFORT, jamais un
            // prérequis. On abandonne en silence plutôt que d'empêcher l'API de démarrer.
            _logger.LogInformation(ex, "Replay : découverte LAN indisponible (le reste fonctionne).");
            udp?.Dispose();
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try { received = await udp.ReceiveAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogDebug(ex, "Replay : réception UDP en erreur."); continue; }

                var text = Encoding.UTF8.GetString(received.Buffer).Trim();
                if (!string.Equals(text, LanPeerProtocol.Discover, StringComparison.Ordinal)) continue;

                // Une borne qui ne partage rien ne se signale pas.
                if (!_policy.SharingEnabled) continue;

                var reply = BuildAnnounce(received.RemoteEndPoint.Address);
                if (reply is null) continue;
                var bytes = Encoding.UTF8.GetBytes(LanPeerProtocol.PeerPrefix + reply);
                try { await udp.SendAsync(bytes, received.RemoteEndPoint, stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogDebug(ex, "Replay : réponse de découverte non envoyée."); }
            }
        }
        finally { udp.Dispose(); }
    }

    /// <summary>L'adresse à annoncer est celle par laquelle CE demandeur peut nous joindre.</summary>
    private string? BuildAnnounce(IPAddress asker)
    {
        var local = LocalAddressTowards(asker);
        if (local is null) return null;
        var port = PortFromUrls(_config["Urls"]) ?? 12345;
        var name = Environment.MachineName;
        var announce = new LanPeerProtocol.Announce(name, $"http://{local}:{port}", _devices.DeviceId, LanPeerProtocol.InstanceId);
        return JsonSerializer.Serialize(announce, PeerJson.Options);
    }

    private static IPAddress? LocalAddressTowards(IPAddress target)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(target, 9); // UDP : ne transmet rien, choisit juste l'interface sortante
            return (probe.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch { return null; }
    }

    internal static int? PortFromUrls(string? urls)
    {
        if (string.IsNullOrWhiteSpace(urls)) return null;
        foreach (var part in urls.Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (Uri.TryCreate(part.Trim(), UriKind.Absolute, out var u) && u.Port > 0) return u.Port;
        return null;
    }
}

/// <summary>Le SONDEUR : diffuse une requête et récolte les bornes qui se présentent.</summary>
public sealed class LanPeerSource : IReplayPeerSource
{
    private static readonly TimeSpan Listen = TimeSpan.FromMilliseconds(900);

    private readonly ILogger<LanPeerSource> _logger;
    public LanPeerSource(ILogger<LanPeerSource> logger) => _logger = logger;

    public string Name => "lan";

    public async Task<IReadOnlyList<ReplayPeer>> DiscoverAsync(CancellationToken ct)
    {
        var found = new Dictionary<string, ReplayPeer>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0)); // port éphémère : on est le demandeur
            var payload = Encoding.UTF8.GetBytes(LanPeerProtocol.Discover);
            await udp.SendAsync(payload, new IPEndPoint(IPAddress.Broadcast, LanPeerProtocol.Port), ct).ConfigureAwait(false);

            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(Listen);
            while (!window.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync(window.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                var text = Encoding.UTF8.GetString(r.Buffer).Trim();
                if (!text.StartsWith(LanPeerProtocol.PeerPrefix, StringComparison.Ordinal)) continue;
                var json = text[LanPeerProtocol.PeerPrefix.Length..];
                LanPeerProtocol.Announce? a;
                try { a = JsonSerializer.Deserialize<LanPeerProtocol.Announce>(json, PeerJson.Options); }
                catch { continue; }
                if (a is null || string.IsNullOrWhiteSpace(a.BaseUrl)) continue;
                if (string.Equals(a.InstanceId, LanPeerProtocol.InstanceId, StringComparison.Ordinal)) continue; // c'est nous

                // Une borne découverte est un CANDIDAT : aucune clé, donc aucun droit acquis.
                found[a.BaseUrl] = new ReplayPeer(a.Name ?? "borne", a.BaseUrl, ApiKey: null, Source: "lan", DeviceId: a.DeviceId);
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay : découverte LAN sans résultat."); }

        if (found.Count > 0) _logger.LogInformation("Replay : {Count} borne(s) trouvée(s) sur le réseau local.", found.Count);
        return found.Values.ToList();
    }
}
