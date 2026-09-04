using System.Security.Cryptography;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Sharing;

/// <summary>
/// Clé de PARTAGE, distincte de la clé d'API de la borne.
///
/// La clé de borne ouvre toute l'API : lancer une lecture, déployer une configuration, tout lire.
/// C'est légitime pour le hub de flotte, qui administre la machine. Ce n'est pas ce qu'on veut
/// donner à une borne voisine qui veut simplement récupérer un replay public : ce serait confier
/// les clés de la maison pour prêter un livre.
///
/// Cette clé-ci n'ouvre QUE la surface de partage (les objets et les manifestes des replays
/// publics). Elle est propre à chaque borne, générée à la première demande, et rangée sous
/// <c>state/</c>, qui n'est pas versionné. C'est elle qu'on confie à un pair, jamais l'autre.
/// </summary>
public static class ReplayShareKeyStore
{
    private static string KeyPath => Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfenet", "share-key.txt");

    /// <summary>La clé persistée, créée à la première demande. Vide en cas d'échec, ce que le
    /// pipeline traite comme « aucun partage possible hors boucle locale ».</summary>
    public static string GetOrCreate()
    {
        var path = KeyPath;
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length >= 16) return existing;
            }

            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(20)); // 40 caractères hex
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, key);
            return key;
        }
        catch
        {
            return string.Empty;
        }
    }
}
