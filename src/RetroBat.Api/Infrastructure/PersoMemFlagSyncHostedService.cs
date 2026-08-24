using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Miroir de la préférence « Prioriser mes .MEM perso » (toggle ES
/// <see cref="PersoMemPreference.EsKey"/>) vers le flag <c>PERSO</c> du <c>.env</c> du
/// wrapper. Le wrapper lit ce flag à l'init du core : <c>PERSO=1</c> → arbitre
/// <c>contest &gt; perso &gt; officiel</c> ; <c>PERSO=0</c> → officiel/contest seulement.
///
/// - Au démarrage : écrit la baseline (aucun test perso n'est actif à ce moment).
/// - Sur changement es_settings : ne réécrit le .env QUE si la préférence a réellement
///   changé (delta) - pour ne pas écraser un <c>PERSO=1</c> transitoire posé par le
///   <c>MemPersoController</c> (bouton ▶ Tester du MEM Explorer) quand l'utilisateur
///   modifie un tout autre réglage du menu.
/// </summary>
public sealed class PersoMemFlagSyncHostedService : BackgroundService
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(600);
    private readonly IEsSettingsStore _settingsStore;
    private readonly IEsSettingsChangeBus _settingsChangeBus;
    private readonly ILogger<PersoMemFlagSyncHostedService>? _logger;
    private bool _lastPrefer;
    private IDisposable? _subscription;

    public PersoMemFlagSyncHostedService(
        IEsSettingsStore settingsStore,
        IEsSettingsChangeBus settingsChangeBus,
        ILogger<PersoMemFlagSyncHostedService>? logger = null)
    {
        _settingsStore = settingsStore;
        _settingsChangeBus = settingsChangeBus;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lastPrefer = PersoMemPreference.IsEnabled(_settingsStore);
        // Baseline au démarrage : pas de test perso en cours, on aligne le .env.
        WrapperEnvFile.UpsertFlag("PERSO", PersoMemPreference.EnvValue(_lastPrefer));
        _logger?.LogInformation("Préférence .MEM perso appliquée au démarrage : PERSO={Value}.", _lastPrefer ? "1" : "0");
        _subscription = _settingsChangeBus.Subscribe((_, token) => HandleSettingsChangedAsync(token));
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return base.StopAsync(cancellationToken);
    }

    private async Task HandleSettingsChangedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DebounceDelay, cancellationToken);
            await _settingsStore.WaitForStableFileAsync(cancellationToken);

            var prefer = PersoMemPreference.IsEnabled(_settingsStore);
            if (prefer == _lastPrefer)
            {
                return; // la préférence n'a pas bougé → on ne touche pas au .env (test en cours préservé).
            }

            _lastPrefer = prefer;
            WrapperEnvFile.UpsertFlag("PERSO", PersoMemPreference.EnvValue(prefer));
            _logger?.LogInformation("Préférence .MEM perso modifiée : PERSO={Value}.", prefer ? "1" : "0");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Debounced par une écriture plus récente, ou arrêt du service.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Miroir de la préférence .MEM perso échoué.");
        }
    }
}
