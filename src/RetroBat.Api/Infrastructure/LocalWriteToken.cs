using System;
using System.IO;
using System.Security.Cryptography;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Le laissez-passer des pages LOCALES qui ont le droit d'ecrire.
///
/// Une page web ne peut pas prouver qu'elle est des notres par son en-tete
/// d'origine : un overlay maison charge dans un WebView2 et un site hostile
/// ouvert dans un onglet se presentent exactement pareil. Ce qui les separe
/// vraiment, c'est l'acces au DISQUE de la machine - le premier l'a, le second
/// jamais.
///
/// Ce jeton est donc ecrit dans un fichier local. Un overlay qui tourne sur la
/// machine le lit et le presente ; une page distante ne peut ni le lire ni le
/// deviner.
///
/// Il voyage dans un en-tete PERSONNALISE, et c'est deliberé : une requete
/// portant un en-tete non standard cesse d'etre « simple » pour le navigateur,
/// qui doit alors demander l'autorisation avant de l'envoyer. La politique
/// d'origine reprend donc la main - ce qu'elle n'a pas sur un formulaire ou une
/// image, envoyes sans rien demander a personne.
/// </summary>
public static class LocalWriteToken
{
    public const string HeaderName = "X-ApiExpose-Local";

    private static readonly object Sync = new();
    private static string? _value;

    private static string TokenPath =>
        Path.Combine(AppContext.BaseDirectory, "state", "apiexpose", "local-write-token.txt");

    /// <summary>
    /// Le jeton de cette installation, cree au premier besoin.
    ///
    /// Il survit aux redemarrages : le regenerer a chaque lancement obligerait
    /// tout overlay a le relire au bon moment, et un overlay qui garde une
    /// vieille valeur echouerait sans qu'on comprenne pourquoi.
    /// </summary>
    public static string Value
    {
        get
        {
            if (_value is { Length: > 0 })
            {
                return _value;
            }

            lock (Sync)
            {
                if (_value is { Length: > 0 })
                {
                    return _value;
                }

                _value = Read() ?? Create();

                return _value;
            }
        }
    }

    public static bool Matches(string? provided)
        => provided is { Length: > 0 }
            && CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(provided),
                System.Text.Encoding.UTF8.GetBytes(Value));

    private static string? Read()
    {
        try
        {
            if (!File.Exists(TokenPath))
            {
                return null;
            }

            var value = File.ReadAllText(TokenPath).Trim();

            return value.Length >= 32 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Create()
    {
        var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
            File.WriteAllText(TokenPath, value);
        }
        catch
        {
            // Sans fichier, le jeton ne vit que le temps du processus : les
            // ecritures depuis un navigateur cesseront au redemarrage, ce qui
            // est genant mais jamais dangereux.
        }

        return value;
    }
}
