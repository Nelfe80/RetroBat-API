namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Réglages du canal RAM communautaire (voir <see cref="CommunityRamSyncService"/>).
/// Section de configuration : <c>ApiExpose:CommunityRam</c>.
/// </summary>
public class CommunityRamOptions
{
    /// <summary>Active le pull périodique des .MEM communautaires.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Dépôt public GitHub (owner/name) des contributions .MEM.</summary>
    public string Repository { get; set; } = "Nelfe80/RetroBat-RAM-Community";

    /// <summary>Branche suivie.</summary>
    public string Branch { get; set; } = "main";

    /// <summary>Intervalle entre deux synchronisations, en heures.</summary>
    public int IntervalHours { get; set; } = 24;

    /// <summary>
    /// Écraser un fichier .MEM déjà présent en local (dossier officiel de l'installer) ?
    /// Par défaut NON : on n'ajoute que les jeux absents, le dossier officiel prime.
    /// </summary>
    public bool OverwriteExisting { get; set; }
}
