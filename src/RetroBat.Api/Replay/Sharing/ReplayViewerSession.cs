namespace RetroBat.Api.Replay.Sharing;

/// <summary>
/// Qui regarde, en ce moment, sur cette borne (CDC DEV §101.6).
///
/// Un spectateur non authentifié ne réagit pas. Mais la borne ne manipule jamais une identité de
/// compte : elle reçoit un jeton OPAQUE et court, émis par la plateforme pour le visiteur
/// connecté qui a lancé la lecture, et le joint aux réactions. C'est la plateforme qui le résout.
///
/// Conséquence voulue : une machine compromise ne peut pas fabriquer des réactions au nom de
/// n'importe qui, puisqu'elle ne détient qu'un jeton lié à une séance et à une durée.
///
/// La séance se ferme avec la lecture. Un jeton qui traînerait laisserait le spectateur suivant
/// réagir sous l'identité du précédent, ce qui est exactement ce qu'on cherche à empêcher.
/// </summary>
public sealed class ReplayViewerSession
{
    private readonly object _gate = new();
    private string? _token;
    private DateTime _openedUtc;

    /// <summary>Au-delà, la séance est considérée close même si personne ne l'a fermée : une
    /// lecture interrompue par une coupure ne doit pas laisser une identité ouverte.</summary>
    private static readonly TimeSpan Peremption = TimeSpan.FromHours(4);

    private readonly ILogger<ReplayViewerSession> _logger;

    public ReplayViewerSession(ILogger<ReplayViewerSession> logger) => _logger = logger;

    public void Open(string? viewerToken)
    {
        var token = viewerToken?.Trim();
        lock (_gate)
        {
            _token = string.IsNullOrEmpty(token) ? null : token;
            _openedUtc = DateTime.UtcNow;
        }
        _logger.LogInformation("Replay : séance de visionnage {Etat}.",
            string.IsNullOrEmpty(token) ? "SANS spectateur identifié (aucune réaction ne sera retenue)" : "ouverte pour un spectateur identifié");
    }

    public void Close()
    {
        lock (_gate) { _token = null; }
    }

    /// <summary>Le jeton courant, ou null : dans ce cas, aucune réaction n'est retenue.</summary>
    public string? Current
    {
        get
        {
            lock (_gate)
            {
                if (_token is null) return null;
                if (DateTime.UtcNow - _openedUtc > Peremption) { _token = null; return null; }
                return _token;
            }
        }
    }
}
