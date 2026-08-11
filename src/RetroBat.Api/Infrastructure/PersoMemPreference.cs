using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Préférence « Prioriser mes .MEM perso » (toggle du menu EmulationStation
/// <c>global.apiexpose.community_ram.prefer_perso</c>, preset <c>switchauto</c>).
///
/// Sémantique (décidée avec l'utilisateur) : <b>opt-in explicite</b>.
/// <list type="bullet">
///   <item>AUTO (valeur vide, défaut ES du switch) → <c>false</c> = officiel/contest joué normalement ;</item>
///   <item>ON (<c>1/true/on</c>) → <c>true</c> = le wrapper préfère les .MEM perso en jeu normal ;</item>
///   <item>OFF (<c>0/false/off</c>) → <c>false</c>.</item>
/// </list>
/// C'est volontaire : un .MEM perso resté sur une borne ne doit JAMAIS être joué en
/// silence (le scoring certifiable resterait par défaut sur l'officiel). L'arbitre du
/// wrapper garde <c>contest &gt; perso &gt; officiel</c> : même à <c>true</c>, un contest live
/// prime toujours sur le perso.
/// </summary>
public static class PersoMemPreference
{
    /// <summary>Clé du réglage dans es_settings.cfg.</summary>
    public const string EsKey = "global.apiexpose.community_ram.prefer_perso";

    /// <summary>Valeur du flag <c>PERSO</c> à écrire dans le .env du wrapper (« 1 » / « 0 »).</summary>
    public static string EnvValue(bool enabled) => enabled ? "1" : "0";

    /// <summary>Lit la préférence depuis le store es_settings (vide/absent = off).</summary>
    public static bool IsEnabled(IEsSettingsStore store)
    {
        try
        {
            return store.ReadAllSettings().TryGetValue(EsKey, out var raw) && Parse(raw);
        }
        catch { return false; }
    }

    /// <summary>Interprète une valeur brute de switchauto : vide/inconnu = off (défaut sûr).</summary>
    public static bool Parse(string? raw)
    {
        switch ((raw ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "on":
            case "yes":
                return true;
            default:
                // vide (= AUTO), "0", "false", "off", ou toute autre valeur → non prioritaire.
                return false;
        }
    }
}
