using System.Text;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Accès partagé au <c>.env</c> du wrapper (<c>plugins\APIExpose\wrapper\.env</c>), lu une
/// seule fois par le wrapper à l'init du core. On préserve TOUJOURS les autres lignes
/// (DISCOVERY, PIPE, HZ, PERSO, commentaires) : jamais de réécriture complète du fichier —
/// c'est justement ce qui permet aux différents flags (découverte, perso) de coexister.
/// </summary>
public static class WrapperEnvFile
{
    public static string EnvPath => Path.Combine(RetroBatPaths.PluginRoot, "wrapper", ".env");

    /// <summary>Valeur d'une clé du .env (chaîne vide si absente ou fichier introuvable).</summary>
    public static string ReadFlag(string key)
    {
        try
        {
            if (!File.Exists(EnvPath)) return string.Empty;
            foreach (var line in File.ReadAllLines(EnvPath))
            {
                var t = line.Trim();
                if (t.StartsWith('#')) continue;
                var eq = t.IndexOf('=');
                if (eq > 0 && t[..eq].Trim() == key) return t[(eq + 1)..].Trim();
            }
        }
        catch { /* ignore */ }
        return string.Empty;
    }

    /// <summary>Écrit <c>key=value</c> en PRÉSERVANT les autres lignes (upsert).</summary>
    public static void UpsertFlag(string key, string value)
    {
        var lines = File.Exists(EnvPath)
            ? File.ReadAllLines(EnvPath).ToList()
            : new List<string>();
        var found = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith('#')) continue;
            var eq = lines[i].IndexOf('=');
            if (eq > 0 && lines[i][..eq].Trim() == key) { lines[i] = key + "=" + value; found = true; break; }
        }
        if (!found) lines.Add(key + "=" + value);
        Directory.CreateDirectory(Path.GetDirectoryName(EnvPath)!);
        File.WriteAllLines(EnvPath, lines);
    }
}
