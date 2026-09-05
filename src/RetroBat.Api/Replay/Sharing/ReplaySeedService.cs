using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Replay.Sharing;

/// <summary>
/// Vide la file de semis (CDC DEV §101.5) : porte chaque replay inscrit jusqu'à l'amorce, et
/// recommence tant que ce n'est pas fait.
///
/// Trois propriétés voulues.
///
/// Le test d'achèvement est ADRESSÉ PAR CONTENU : on demande à l'amorce si elle détient déjà ce
/// hash. Comme le nom de l'objet EST son hash, la réponse est sans ambiguïté et il n'y a aucun
/// état local à conserver ni à croire. Une reprise revérifie la réalité au lieu de se fier à sa
/// mémoire, donc rien ne peut être poussé deux fois ni perdu en silence.
///
/// Le semis se TAIT pendant qu'une partie ou une lecture tourne. Envoyer un objet de plusieurs
/// méga-octets pendant que le joueur joue lui volerait de la bande passante, et c'est exactement
/// le genre de nuisance invisible qu'on ne rattrape jamais. Le record attend quelques minutes.
///
/// Enfin, un délai de garde après une poussée réussie : la plateforme relaie vers l'amorce par une
/// tâche périodique, donc l'objet n'y apparaît pas immédiatement. Sans ce délai, on repousserait
/// le même objet à chaque tour pendant toute la fenêtre de relais.
/// </summary>
public sealed class ReplaySeedService : BackgroundService
{
    private static readonly TimeSpan Cadence = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PremierEssai = TimeSpan.FromSeconds(45);
    /// <summary>Après une poussée acceptée, on laisse à la plateforme le temps de relayer.</summary>
    private static readonly TimeSpan DelaiDeGarde = TimeSpan.FromMinutes(30);

    private readonly ReplaySeedQueue _queue;
    private readonly ReplayTransitPublisher _publisher;
    private readonly IEventBus _bus;
    private readonly ILogger<ReplaySeedService> _logger;

    private volatile bool _gameActive;
    private volatile bool _replayActive;

    public ReplaySeedService(ReplaySeedQueue queue, ReplayTransitPublisher publisher,
        IEventBus bus, ILogger<ReplaySeedService> logger)
    {
        _queue = queue; _publisher = publisher; _bus = bus; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { _bus.Subscribe<EventEnvelope>(OnBusEvent); } catch (Exception ex) { _logger.LogDebug(ex, "Replay : abonnement au bus impossible."); }

        // Un premier passage peu après le démarrage : c'est lui qui rattrape une machine éteinte
        // en plein envoi. Pas immédiat, pour ne pas concurrencer le démarrage d'EmulationStation.
        try { await Task.Delay(PremierEssai, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DrainAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "Replay : passage de semis en erreur."); }

            try { await Task.Delay(Cadence, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Tentative immédiate, déclenchée par le geste de publication.</summary>
    public Task NudgeAsync(CancellationToken ct) => DrainAsync(ct);

    private async Task DrainAsync(CancellationToken ct)
    {
        var intents = _queue.Read();
        if (intents.Count == 0) return;

        if (_gameActive || _replayActive)
        {
            _logger.LogDebug("Replay : semis reporté, une partie ou une lecture est en cours ({Count} en file).", intents.Count);
            return;
        }

        foreach (var intent in intents)
        {
            if (ct.IsCancellationRequested) return;

            // 1. L'amorce l'a-t-elle déjà ? Question posée par le HASH, donc sans ambiguïté.
            if (await _publisher.IsOnSeedAsync(intent.ObjectSha256, ct).ConfigureAwait(false))
            {
                _queue.Complete(intent.ReplayId);
                continue;
            }

            // 2. Poussée récente : la plateforme n'a peut-être pas encore relayé. On patiente
            //    plutôt que de renvoyer plusieurs méga-octets pour rien.
            if (intent.LastPushUtc is { } pushed && DateTime.UtcNow - pushed < DelaiDeGarde) continue;

            // 3. On pousse. L'intention reste en file jusqu'à ce que l'amorce confirme.
            var result = await _publisher.PublishAsync(intent.ReplayId, ct).ConfigureAwait(false);
            _queue.Note(intent.ReplayId, result.Ok, result.Error);
            if (!result.Ok)
                _logger.LogInformation("Replay : semis de {ReplayId} non abouti ({Error}), nouvelle tentative plus tard.",
                    intent.ReplayId, result.Error);
        }
    }

    private void OnBusEvent(EventEnvelope e)
    {
        switch (e.Type)
        {
            case "ui.game.started": _gameActive = true; break;
            case "ui.game.ended": _gameActive = false; break;
            case "replay.launching":
            case "replay.started": _replayActive = true; break;
            case "replay.finished": _replayActive = false; break;
        }
    }
}
