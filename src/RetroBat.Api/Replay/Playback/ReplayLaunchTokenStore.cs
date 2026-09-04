using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RetroBat.Api.Replay.Playback;

/// <summary>
/// Jetons de lancement à USAGE UNIQUE pour /replay/watch (sécurité du funnel web).
///
/// Le vrai risque n'est pas /replay/play (déjà loopback-only : un site tiers ne peut pas
/// l'atteindre en fetch — Local Network Access), mais une NAVIGATION drive-by vers
/// /replay/watch qui, elle, est autorisée et auto-lançait la lecture. APIExpose émet donc un
/// jeton au HANDSHAKE de détection (/nelfeplay/detect → 302 /apiexpose-ok) : seule une page
/// nelfeplay.com peut le récupérer (le signal BroadcastChannel/postMessage est scellé à son
/// origine). /replay/watch n'AUTO-lance que sur jeton valide ; sans jeton (navigation directe,
/// ou jeton expiré), il demande un clic de confirmation. En mémoire, usage unique, TTL court.
/// </summary>
public sealed class ReplayLaunchTokenStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(90);
    private readonly ConcurrentDictionary<string, DateTime> _tokens = new(StringComparer.Ordinal);

    /// <summary>Émet un jeton opaque (128 bits) valable une seule fois, TTL 90 s.</summary>
    public string Issue()
    {
        Prune();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _tokens[token] = DateTime.UtcNow + Ttl;
        return token;
    }

    /// <summary>Vrai si le jeton existe, n'est pas expiré et n'a jamais été consommé. Le
    /// consomme (retrait atomique) : un même jeton ne peut lancer qu'UNE lecture.</summary>
    public bool Consume(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (!_tokens.TryRemove(token, out var expiry)) return false; // retrait atomique = usage unique
        return DateTime.UtcNow <= expiry;                            // expiré → déjà retiré, refusé
    }

    // Nettoyage paresseux : on ne balaie que si la table gonfle (jetons non consommés qui expirent).
    private void Prune()
    {
        if (_tokens.Count < 64) return;
        var now = DateTime.UtcNow;
        foreach (var kv in _tokens)
        {
            if (kv.Value < now) { _tokens.TryRemove(kv.Key, out _); }
        }
    }
}
