using System.Security.Cryptography;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Playback;
using RetroBat.Api.Replay.Storage;

namespace RetroBat.Api.Replay.Sharing;

/// <summary>
/// L'implémentation NelfeNet de la seam posée en R7 : « rends-moi cet objet disponible ».
///
/// Ordre : d'abord ici (aucun réseau si l'objet est déjà là), puis les pairs connus, dans l'ordre
/// du fichier. Sans pair configuré, le comportement est exactement celui d'avant, en local pur.
///
/// Un pair n'est JAMAIS cru sur parole. Ce qu'il envoie est écrit dans un fichier temporaire,
/// pesé et haché, et il n'entre dans le magasin que si taille ET SHA-256 correspondent au
/// manifeste. Un contenu qui ne correspond pas est supprimé et le pair suivant est essayé : une
/// borne hostile ne peut donc ni polluer le magasin, ni faire lire autre chose que ce qui a été
/// demandé. Le téléchargement est en outre coupé dès qu'il dépasse la taille annoncée, pour
/// qu'un pair ne puisse pas remplir le disque.
/// </summary>
public sealed class NelfeNetSourceResolver : IReplaySourceResolver
{
    /// <summary>Délai pour SAVOIR si un pair répond. Court : au-delà, il ne répond pas.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Délai de TRANSFERT, proportionné à la taille. Confondre les deux reviendrait à
    /// choisir entre écarter trop vite les liaisons lentes et attendre trop longtemps les mortes.</summary>
    private static readonly TimeSpan TransferMinimum = TimeSpan.FromSeconds(30);
    private const long OctetsParSecondePessimiste = 50 * 1024;

    /// <summary>Un pair qui vient d'échouer est écarté un moment. Sans cette mémoire, on repaie le
    /// coût de la sonde à chaque tentative pour une machine éteinte depuis des semaines.</summary>
    private static readonly TimeSpan Quarantaine = TimeSpan.FromMinutes(10);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> Echecs =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IReplayObjectStore _objects;
    private readonly ReplayPeerDirectory _peers;
    private readonly ReplayNetworkStateService _network;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<NelfeNetSourceResolver> _logger;

    public NelfeNetSourceResolver(IReplayObjectStore objects, ReplayPeerDirectory peers,
        ReplayNetworkStateService network, IHttpClientFactory httpFactory, ILogger<NelfeNetSourceResolver> logger)
    {
        _objects = objects; _peers = peers; _network = network; _httpFactory = httpFactory; _logger = logger;
    }

    public async Task<bool> EnsureObjectAvailableAsync(ReplayManifest manifest, CancellationToken ct)
    {
        var sha = manifest.Object.Sha256;
        if (File.Exists(_objects.ObjectPath(sha))) return true;

        var peers = await _peers.PeersAsync(ct).ConfigureAwait(false);
        if (peers.Count == 0)
        {
            _logger.LogInformation("Replay : objet {Sha} absent et aucun pair configuré.", Short(sha));
            return false;
        }

        // Le temps de la récupération, l'objet est « replicating » pour qui interroge l'API.
        using (_network.BeginFetch(sha))
        {
            foreach (var peer in peers)
            {
                if (ct.IsCancellationRequested) return false;
                if (EnQuarantaine(peer))
                {
                    _logger.LogDebug("Replay : pair {Peer} écarté, échec récent.", peer.Name);
                    continue;
                }
                if (await TryFetchAsync(peer, manifest, ct).ConfigureAwait(false))
                {
                    _peers.RememberWorking(peer); // pair récent : retrouvable même annuaire coupé
                    return true;
                }
            }
        }

        _logger.LogWarning("Replay : objet {Sha} introuvable auprès des {Count} pair(s) connus.", Short(sha), peers.Count);
        return false;
    }

    private async Task<bool> TryFetchAsync(ReplayPeer peer, ReplayManifest manifest, CancellationToken ct)
    {
        var sha = manifest.Object.Sha256;
        var temp = Path.Combine(_objects.TempRoot, $"fetch-{sha}.part");
        try
        {
            // Une borne expose une route d'API ; une amorce statique expose une URL de fichier.
            // Le gabarit permet aux deux de passer par le MEME client de transfert.
            var url = string.IsNullOrWhiteSpace(peer.UrlTemplate)
                ? peer.BaseUrl.TrimEnd('/') + "/api/v1/object/" + sha
                : peer.UrlTemplate.Replace("{sha}", sha, StringComparison.Ordinal);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(peer.ApiKey)) request.Headers.Add("X-Api-Key", peer.ApiKey);

            var client = _httpFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan; // c'est le CTS qui borne, pour couvrir aussi la lecture du corps

            // 1) Répond-il ? Délai COURT : quatre secondes suffisent à le savoir.
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Replay : pair {Peer} n'a pas l'objet {Sha} (HTTP {Code}).", peer.Name, Short(sha), (int)response.StatusCode);
                return false;
            }

            // 2) Transfert : délai PROPORTIONNÉ, pour ne pas couper une liaison lente mais valide.
            var budget = TimeSpan.FromSeconds(Math.Max(
                TransferMinimum.TotalSeconds,
                (double)manifest.Object.Size / OctetsParSecondePessimiste));
            using var transferCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            transferCts.CancelAfter(budget);

            var written = await WriteCappedAsync(response, temp, manifest.Object.Size, transferCts.Token).ConfigureAwait(false);
            if (written is null)
            {
                _logger.LogWarning("Replay : pair {Peer} a envoyé plus que la taille annoncée pour {Sha} — abandonné.", peer.Name, Short(sha));
                return false;
            }

            if (written != manifest.Object.Size)
            {
                _logger.LogWarning("Replay : pair {Peer}, taille {Got} ≠ {Want} pour {Sha} — rejeté.", peer.Name, written, manifest.Object.Size, Short(sha));
                return false;
            }

            var actual = await HashAsync(temp, transferCts.Token).ConfigureAwait(false);
            if (!string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Replay : pair {Peer} a envoyé un contenu qui ne correspond PAS au hash demandé ({Sha}) — rejeté.", peer.Name, Short(sha));
                return false;
            }

            await _objects.ImportObjectAsync(temp, transferCts.Token).ConfigureAwait(false);
            _logger.LogInformation("Replay : objet {Sha} récupéré auprès de {Peer} ({Size} octets) et vérifié.", Short(sha), peer.Name, written);
            return File.Exists(_objects.ObjectPath(sha));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            NoterEchec(peer);
            _logger.LogWarning("Replay : délai dépassé auprès du pair {Peer} pour {Sha}.", peer.Name, Short(sha));
            return false;
        }
        catch (Exception ex)
        {
            NoterEchec(peer);
            _logger.LogWarning(ex, "Replay : échec de récupération auprès du pair {Peer}.", peer.Name);
            return false;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    /// <summary>Écrit le corps sur disque en s'arrêtant net au-delà de la taille annoncée.
    /// Renvoie le nombre d'octets écrits, ou null si le pair a dépassé.</summary>
    private static async Task<long?> WriteCappedAsync(HttpResponseMessage response, string destination, long maxBytes, CancellationToken ct)
    {
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes) return null;
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return total;
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var fs = File.OpenRead(path);
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool EnQuarantaine(ReplayPeer peer)
        => Echecs.TryGetValue(peer.BaseUrl, out var quand) && DateTime.UtcNow - quand < Quarantaine;

    /// <summary>Un échec de TRANSPORT met le pair de côté. Un simple « il ne l'a pas » (404) n'en
    /// est pas un : le pair fonctionne, il n'a juste pas cet objet.</summary>
    private static void NoterEchec(ReplayPeer peer) => Echecs[peer.BaseUrl] = DateTime.UtcNow;

    private static string Short(string sha) => sha.Length <= 8 ? sha : sha[..8];
}
