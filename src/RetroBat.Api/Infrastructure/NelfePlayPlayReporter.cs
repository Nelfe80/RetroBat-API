using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Events;
using RetroBat.Domain.Models;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Releve ce qui est joue, et rien d'autre.
///
/// Le but est de mesurer une audience, pas de suivre quelqu'un : ce qui part
/// d'ici est le titre lance, le systeme, et la duree. Aucun identifiant de
/// joueur ne quitte la machine — le serveur reduit sur-le-champ ce qu'il
/// recoit a un pseudonyme dont le sel est detruit chaque mois.
///
/// Deux natures de machine :
///
///  - APPAIREE : le credential de l'appareil sert deja pour le reste de
///    l'API, on s'en sert aussi ici ;
///  - NON APPAIREE : la plupart des installations n'ouvriront jamais de
///    compte, et ce sont justement elles qui font le volume. Elles
///    s'inscrivent donc une fois, sans compte ni adresse, et gardent un
///    secret local. Pourquoi authentifier ce qui est anonyme ? Parce qu'un
///    relais ouvert se remplit tout seul, et que des chiffres gonfles valent
///    moins que pas de chiffres.
///
/// Le relevé s'éteint d'un réglage : une mesure sans interrupteur n'est pas
/// une mesure, c'est une surveillance.
/// </summary>
public sealed class NelfePlayPlayReporter : BackgroundService
{
    /// <summary>
    /// Au-dela de cette taille, on ne calcule pas d'empreinte.
    ///
    /// Une image de CD ou de DVD prend plusieurs secondes a lire entierement,
    /// et ce serait au moment ou le joueur attend que son jeu demarre. On
    /// retombe alors sur le couple systeme + nom, moins sur mais gratuit.
    /// </summary>
    private const long MaxHashBytes = 96L * 1024 * 1024;

    /// <summary>Une partie plus courte n'en est pas une : c'est un jeu lance
    /// puis quitte aussitot, souvent par erreur. La compter fausserait la
    /// duree moyenne, qui est justement ce qui distingue un titre qu'on essaie
    /// d'un titre qu'on joue.</summary>
    private const int MinimumSeconds = 20;

    /// <summary>La file d'attente hors ligne ne grandit pas indefiniment : une
    /// machine coupee du reseau pendant des mois ne doit pas remplir son disque
    /// avec des parties que personne n'attend plus.</summary>
    private const int MaxPending = 200;

    private static readonly string StatePath =
        Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "anonymous.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IEventBus _eventBus;
    private readonly ApiContext _context;
    private readonly IHttpClientFactory _httpFactory;
    private readonly NelfePlayDeviceStore _devices;
    private readonly ILogger<NelfePlayPlayReporter>? _logger;

    private readonly object _sync = new();
    private readonly Queue<PlayRecord> _pending = new();
    private IDisposable? _subscription;
    private Play? _current;
    private string? _anonymousCredential;

    /// <summary>Interrupteur du releve. Coupe, plus rien ne part.</summary>
    public static bool Enabled { get; set; } = true;

    public NelfePlayPlayReporter(
        IEventBus eventBus,
        ApiContext context,
        IHttpClientFactory httpFactory,
        NelfePlayDeviceStore devices,
        ILogger<NelfePlayPlayReporter>? logger = null)
    {
        _eventBus = eventBus;
        _context = context;
        _httpFactory = httpFactory;
        _devices = devices;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = _eventBus.Subscribe<EventEnvelope>(HandleEvent);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await FlushAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Arret normal.
        }
        finally
        {
            _subscription?.Dispose();
        }
    }

    private void HandleEvent(EventEnvelope envelope)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            if (string.Equals(envelope.Type, "ui.game.started", StringComparison.OrdinalIgnoreCase))
            {
                BeginPlay(envelope);
                return;
            }

            if (string.Equals(envelope.Type, "ui.game.ended", StringComparison.OrdinalIgnoreCase))
            {
                EndPlay();
            }
        }
        catch (Exception ex)
        {
            // Un relevé raté ne doit jamais gêner la partie en cours.
            _logger?.LogDebug(ex, "Play reporting skipped for {EventType}", envelope.Type);
        }
    }

    private void BeginPlay(EventEnvelope envelope)
    {
        // Le jeu se lit dans le CONTEXTE, pas dans la charge de l'evenement.
        //
        // J'avais suppose une charge JSON portant SystemId, GameName et
        // GamePath. Elle n'existe pas : le bus publie un objet anonyme
        // { EventName, RawArgs, Context }, et le jeu vit dans Context.Running.
        // Mon extraction exigeait un JsonElement, recevait un objet .NET, et
        // rendait null a chaque fois — aucune partie n'etait donc enregistree,
        // sans qu'aucune erreur ne le signale.
        //
        // On lit maintenant le contexte typé, ou l'objet ne peut pas mentir sur
        // sa forme : une faute de nom deviendrait une erreur de compilation.
        var running = ResolveRunning(envelope.Payload) ?? _context.Ui.Running;
        if (running is null)
        {
            return;
        }

        var systemId = running.SystemId;
        var gameName = running.GameName;
        var gamePath = running.GamePath;

        if (string.IsNullOrWhiteSpace(systemId) && string.IsNullOrWhiteSpace(gameName))
        {
            return;
        }

        lock (_sync)
        {
            // Une nouvelle partie ferme la precedente : Emulationstation ne
            // signale pas toujours la fin quand l'emulateur meurt seul.
            EndPlayLocked();
            _current = new Play(systemId, gameName, gamePath, Stopwatch.StartNew());
        }
    }

    private void EndPlay()
    {
        lock (_sync)
        {
            EndPlayLocked();
        }
    }

    private void EndPlayLocked()
    {
        var play = _current;
        _current = null;
        if (play is null)
        {
            return;
        }

        var seconds = (int)play.Clock.Elapsed.TotalSeconds;
        if (seconds < MinimumSeconds)
        {
            return;
        }

        // L'empreinte est calculee A LA FIN : au demarrage, elle retarderait le
        // lancement du jeu pour un chiffre dont personne n'a besoin tout de
        // suite.
        var md5 = HashOf(play.GamePath);

        if (_pending.Count >= MaxPending)
        {
            _pending.Dequeue();
        }

        _pending.Enqueue(new PlayRecord(play.SystemId, play.GameName, md5, seconds));
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return;
        }

        PlayRecord[] batch;
        lock (_sync)
        {
            if (_pending.Count == 0)
            {
                return;
            }
            batch = _pending.ToArray();
        }

        var credential = await ResolveCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(credential))
        {
            return;
        }

        var sent = 0;
        foreach (var record in batch)
        {
            if (!await PostAsync(credential, record, cancellationToken).ConfigureAwait(false))
            {
                // Le serveur ne repond pas : on garde le reste pour plus tard
                // plutot que de s'acharner.
                break;
            }
            sent++;
        }

        if (sent == 0)
        {
            return;
        }

        lock (_sync)
        {
            for (var i = 0; i < sent && _pending.Count > 0; i++)
            {
                _pending.Dequeue();
            }
        }

        _logger?.LogDebug("Reported {Count} play(s) to NelfePlay", sent);
    }

    private async Task<bool> PostAsync(string credential, PlayRecord record, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-NelfePlay-Device", credential);

            var fields = new List<KeyValuePair<string, string>>
            {
                new("system_id", record.SystemId ?? string.Empty),
                new("name", record.GameName ?? string.Empty),
                new("seconds", record.Seconds.ToString())
            };
            if (!string.IsNullOrEmpty(record.Md5))
            {
                fields.Add(new KeyValuePair<string, string>("md5", record.Md5));
            }

            using var form = new FormUrlEncodedContent(fields);
            using var response = await client.PostAsync("/api/v1/agent/play", form, cancellationToken)
                .ConfigureAwait(false);

            // 401 : le secret anonyme ne vaut plus rien (base remise a zero,
            // par exemple). On l'oublie pour en redemander un au prochain tour.
            if ((int)response.StatusCode == 401 && credential == _anonymousCredential)
            {
                _anonymousCredential = null;
                TryDeleteState();
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Play report failed");
            return false;
        }
    }

    /// <summary>
    /// Le secret a presenter : celui de l'appareil appaire, sinon celui de
    /// l'installation anonyme — inscrite au premier besoin.
    /// </summary>
    private async Task<string?> ResolveCredentialAsync(CancellationToken cancellationToken)
    {
        var paired = _devices.GetCredential();
        if (!string.IsNullOrEmpty(paired))
        {
            return paired;
        }

        if (!string.IsNullOrEmpty(_anonymousCredential))
        {
            return _anonymousCredential;
        }

        _anonymousCredential = ReadState();
        if (!string.IsNullOrEmpty(_anonymousCredential))
        {
            return _anonymousCredential;
        }

        try
        {
            using var client = CreateClient();
            using var response = await client
                .PostAsync("/api/v1/agent/register-anonymous", content: null, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("credential", out var element))
            {
                return null;
            }

            _anonymousCredential = element.GetString();
            WriteState(_anonymousCredential);

            return _anonymousCredential;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Anonymous registration failed");
            return null;
        }
    }

    private HttpClient CreateClient()
    {
        var client = _httpFactory.CreateClient(nameof(NelfePlayPlayReporter));
        client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
        client.Timeout = TimeSpan.FromSeconds(10);

        return client;
    }

    /// <summary>Empreinte MD5, quand le fichier n'est pas trop gros.</summary>
    private static string? HashOf(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            var info = new FileInfo(path);
            if (info.Length == 0 || info.Length > MaxHashBytes)
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            using var md5 = MD5.Create();

            return Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadState()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(StatePath));

            return document.RootElement.TryGetProperty("credential", out var element)
                ? element.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteState(string? credential)
    {
        try
        {
            if (string.IsNullOrEmpty(credential))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new { credential }, JsonOptions));
        }
        catch
        {
            // Sans fichier, l'installation se reinscrira au prochain demarrage :
            // elle comptera comme nouvelle, ce qui est un defaut acceptable pour
            // une mesure d'audience.
        }
    }

    private static void TryDeleteState()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                File.Delete(StatePath);
            }
        }
        catch
        {
            // Sans consequence : le secret sera simplement redemande.
        }
    }

    /// <summary>
    /// Le jeu en cours, tel que la charge de l'evenement le transporte.
    ///
    /// La charge est un objet ANONYME — { EventName, RawArgs, Context } — donc
    /// impossible a typer depuis ici : on va chercher sa propriete Context par
    /// reflexion, une fois, et on retombe sur le contexte partage si sa forme
    /// change un jour. Le repli n'est pas de la prudence decorative : c'est ce
    /// qui evite qu'un renommage cote frontend refasse taire le releve sans
    /// prevenir.
    /// </summary>
    private static GameReference? ResolveRunning(object? payload)
    {
        if (payload is null)
        {
            return null;
        }

        var context = payload.GetType().GetProperty("Context")?.GetValue(payload);

        return context is GameState state ? state.Running : null;
    }

    private sealed record Play(string? SystemId, string? GameName, string? GamePath, Stopwatch Clock);

    private sealed record PlayRecord(string? SystemId, string? GameName, string? Md5, int Seconds);
}
