namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Joueur de SESSION sur la borne (Lot 6 / Station) : le code joueur RGPC posé au
/// check-in par le hub, retiré au checkout. Le <see cref="NelfePlayScoringReporter"/>
/// le lit à la fin de partie pour stamper le passeport certifié -
/// <c>identity.session_player_id</c> + <c>context{world:"station", venue}</c> - afin
/// que le record joué en salle soit attribué à CE joueur (pas à la machine anonyme)
/// et porte la provenance de l'enseigne (nom + ville).
///
/// En mémoire uniquement : une session ne survit pas à un redémarrage (le hub
/// repousse le check-in). Un seul joueur à la fois par borne.
/// </summary>
public sealed class NelfePlayScoringSessionService
{
    private readonly object _lock = new();
    private SessionPlayer? _current;

    /// <summary>
    /// Joueur/monde de session. <paramref name="World"/> ∈ {station, stream} :
    /// STATION porte la provenance salle (nom/ville) ; STREAM porte la chaîne du
    /// streamer et l'id du contest (participation à un contest, salle ou maison).
    /// HOME n'a jamais de session (borne libre / machine perso).
    /// </summary>
    /// <summary>
    /// Le joueur de session. <paramref name="Locale"/> est SA langue, pas celle
    /// de la borne : les annonces la suivent, l'interface de la borne non.
    /// </summary>
    public sealed record SessionPlayer(
        string PlayerCode, string World, string? VenueName, string? VenueCity,
        string? Channel, string? ContestId, string? Locale);

    /// <summary>
    /// Pose (ou remplace) le joueur/monde de session. Code vide = checkout.
    /// <paramref name="world"/> par défaut « station » (rétro-compat : le seul
    /// appelant historique est le check-in salle du hub). Pour un contest,
    /// l'appelant passe world="stream" + channel + contestId.
    /// </summary>
    public void Set(
        string? playerCode, string? venueName, string? venueCity,
        string world = "station", string? channel = null, string? contestId = null,
        string? locale = null)
    {
        var code = (playerCode ?? string.Empty).Trim();
        var w = world is "stream" or "station" ? world : "station";
        // Assainie a l'entree : ce qui est stocke est toujours l'une des six
        // langues servies, ou rien.
        var lang = CabinetAnnounceText.Normalize(locale);
        lock (_lock)
        {
            _current = code.Length == 0
                ? null
                : new SessionPlayer(
                    code,
                    w,
                    string.IsNullOrWhiteSpace(venueName) ? null : venueName.Trim(),
                    string.IsNullOrWhiteSpace(venueCity) ? null : venueCity.Trim(),
                    string.IsNullOrWhiteSpace(channel) ? null : channel.Trim(),
                    string.IsNullOrWhiteSpace(contestId) ? null : contestId.Trim(),
                    lang.Length == 0 ? null : lang);
        }
    }

    /// <summary>Retire le joueur de session (checkout / borne libre).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _current = null;
        }
    }

    /// <summary>Le joueur checké-in, ou null si la borne est libre (= partie « home »).</summary>
    public SessionPlayer? Get()
    {
        lock (_lock)
        {
            return _current;
        }
    }
}
