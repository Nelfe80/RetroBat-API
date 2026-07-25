using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Agent local Nelfe Play : la borne va CHERCHER ce que le joueur a acquis.
///
/// Rien n'est pousse depuis l'exterieur — c'est APIExpose qui interroge
/// nelfeplay.com en HTTPS sortant, avec le credential de l'appareil appaire.
/// Pour chaque demande d'installation, l'agent recupere le paquet CHIFFRE et
/// une licence scellee POUR CETTE MACHINE, verifie l'empreinte du paquet, puis
/// installe un PLACEHOLDER dans roms/&lt;systeme&gt;/ : le jeu apparait dans
/// EmulationStation, mais la ROM en clair n'existe nulle part sur le disque.
///
/// Le dechiffrement n'a lieu qu'au lancement (chaine on-the-fly du pack
/// manager), et le clair est efface juste apres — voir docs/10.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NelfePlayAgentService : BackgroundService
{
    private const string PlaceholderMarker = "NELFEPLAY_ENCRYPTED_ROM_PLACEHOLDER_v1";
    private const string ContainerMagic = "NPLAY1";

    /// <summary>
    /// Le serveur nomme ses champs en snake_case : system_id, target_file_name,
    /// content_key. « Insensible a la casse » ne suffit PAS a les reconnaitre —
    /// cette option rapproche systemId de SystemId, jamais system_id. Les champs
    /// restaient donc vides, et l'installation s'arretait faute de systeme et de
    /// nom de fichier, sans que rien ne dise pourquoi.
    ///
    /// La politique snake_case les relie ; l'insensibilite a la casse reste,
    /// pour que les champs deja nommes autrement continuent d'etre lus.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly NelfePlayDeviceStore _device;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<NelfePlayAgentService> _logger;
    private readonly string _packageRoot =
        Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "packages");

    public NelfePlayAgentService(
        NelfePlayDeviceStore device,
        IHttpClientFactory httpFactory,
        ILogger<NelfePlayAgentService> logger)
    {
        _device = device;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>Adresse du service Nelfe Play (surchargeable pour les tests).</summary>
    public static string BaseUrl { get; set; } = "https://nelfeplay.com";

    /// <summary>Intervalle entre deux relevés, en secondes.</summary>
    public static int PollSeconds { get; set; } = 300;

    /// <summary>Dernier resultat connu, expose par le controleur.</summary>
    public AgentStatus Status { get; private set; } = new();

    private string LabelPath => Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "label.txt");

    /// <summary>
    /// Nom de CETTE machine, tel qu'il apparaitra dans le compte Nelfe Play.
    ///
    /// Une seule regle pour les deux mondes : le poste local est proprietaire
    /// du nom. En salle, HubManager le pousse quand l'exploitant renomme la
    /// borne ; chez un particulier — qui n'a pas HubManager — le nom de la
    /// machine Windows sert de defaut, et le joueur peut le remplacer depuis
    /// APIExpose. Nelfe Play ne fait que l'afficher.
    /// </summary>
    public string MachineLabel
    {
        get
        {
            try
            {
                if (File.Exists(LabelPath))
                {
                    var stored = File.ReadAllText(LabelPath).Trim();
                    if (stored.Length > 0)
                    {
                        return stored.Length > 120 ? stored[..120] : stored;
                    }
                }
            }
            catch (IOException)
            {
            }

            return Environment.MachineName;
        }
    }

    /// <summary>
    /// Fixe le nom local de la machine. Appele par HubManager au renommage
    /// d'une borne, ou par le joueur lui-meme s'il n'a pas de hub. Un nom vide
    /// remet le nom de la machine Windows.
    /// </summary>
    public void SetMachineLabel(string? label)
    {
        var trimmed = (label ?? string.Empty).Trim();
        Directory.CreateDirectory(Path.GetDirectoryName(LabelPath)!);

        if (trimmed.Length == 0)
        {
            if (File.Exists(LabelPath))
            {
                File.Delete(LabelPath);
            }

            return;
        }

        File.WriteAllText(LabelPath, trimmed.Length > 120 ? trimmed[..120] : trimmed);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_device.IsPaired)
                {
                    await PollAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Nelfe Play : releve impossible.");
                Status = Status with { LastError = exception.Message };
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(30, PollSeconds)), stoppingToken);
        }
    }

    /// <summary>Releve immediat (appele aussi juste apres un appairage).</summary>
    public async Task PollAsync(CancellationToken cancellationToken)
    {
        var credential = _device.GetCredential();
        if (credential is null)
        {
            return;
        }

        using var client = CreateClient(credential);
        // Le NOM de cette machine part a chaque releve : c'est le poste local
        // qui en est proprietaire (borne nommee dans HubManager pour une salle,
        // nom de la machine Windows pour un joueur). Nelfe Play l'affiche mais
        // ne le possede pas — un renommage cote salle se propage ici tout seul.
        client.DefaultRequestHeaders.Add("X-Nelfeplay-Label", MachineLabel);
        using var response = await client.GetAsync("/api/v1/agent/work", cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Le compte a revoque cette machine (perdue, revendue, ou dont il
            // doute), ou il est suspendu : le credential ne vaut plus rien. On
            // l'efface au lieu de reinterroger indefiniment, et l'ecran dit
            // clairement qu'il faut re-appairer.
            _logger.LogWarning("Nelfe Play : cette machine n'est plus autorisee, appairage efface.");
            _device.Forget();
            Status = new AgentStatus
            {
                Paired = false,
                LastPollUtc = DateTime.UtcNow,
                LastError = "revoked",
            };
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            Status = Status with { LastError = "work: HTTP " + (int)response.StatusCode };
            return;
        }

        var payload = await response.Content.ReadFromJsonAsync<WorkResponse>(JsonOptions, cancellationToken);
        if (payload is null || !payload.Ok)
        {
            return;
        }

        var installed = 0;
        foreach (var intent in payload.Intents ?? [])
        {
            if (await TryInstallAsync(client, intent, cancellationToken))
            {
                installed++;
            }
        }

        Status = new AgentStatus
        {
            Paired = true,
            PlayerCode = payload.PlayerCode,
            Entitlements = payload.Entitlements?.Count ?? 0,
            LastPollUtc = DateTime.UtcNow,
            LastInstalled = installed,
            LastError = null,
        };

        if (installed > 0)
        {
            _logger.LogInformation("Nelfe Play : {Count} jeu(x) installe(s) depuis la bibliotheque du joueur.", installed);
        }
    }

    /// <summary>
    /// Echange le code affiche sur nelfeplay.com contre le credential durable
    /// de l'appareil. La cle publique part avec la demande ; la cle privee ne
    /// quitte jamais la machine.
    /// </summary>
    public async Task<bool> PairAsync(string code, string label, CancellationToken cancellationToken)
    {
        using var client = CreateClient(credential: null);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code.Trim().ToUpperInvariant(),
            ["label"] = label,
            ["public_key"] = _device.ExportPublicKeyPem(),
            ["key_algorithm"] = NelfePlayDeviceStore.KeyAlgorithm,
        });

        using var response = await client.PostAsync("/api/v1/agent/pair", form, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Nelfe Play : appairage refuse (HTTP {Status}).", (int)response.StatusCode);
            return false;
        }

        var paired = await response.Content.ReadFromJsonAsync<PairResponse>(JsonOptions, cancellationToken);
        if (paired?.Credential is not { Length: > 0 } credential)
        {
            return false;
        }

        _device.SavePairing(paired.DeviceId ?? string.Empty, credential);

        // On epingle la cle publique de Nelfe Play : c'est elle qui permettra
        // de refuser une licence expiree, destinee a une autre machine, ou
        // retouchee. Sans elle, une licence ne serait qu'une cle.
        try
        {
            using var keyResponse = await client.GetAsync("/api/v1/agent/license-key", cancellationToken);
            if (keyResponse.IsSuccessStatusCode)
            {
                var key = await keyResponse.Content.ReadFromJsonAsync<LicenseKeyResponse>(JsonOptions, cancellationToken);
                if (key?.PublicKey is { Length: > 0 } pem)
                {
                    _device.SaveLicensePublicKey(pem);
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(exception, "Nelfe Play : cle publique de licence non recuperee.");
        }

        await PollAsync(cancellationToken);
        return true;
    }

    private async Task<bool> TryInstallAsync(HttpClient client, WorkIntent intent, CancellationToken cancellationToken)
    {
        if (intent.Package is not { } package || intent.License is not { } license)
        {
            // Sans paquet ou sans licence pour cette machine, on n'installe
            // rien : le jeu reste visible dans la bibliotheque en ligne.
            _logger.LogInformation("Nelfe Play : {Slug} sans paquet livrable pour cette machine.", intent.Slug);
            return false;
        }

        var systemId = intent.SystemId;
        if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(package.TargetFileName))
        {
            return false;
        }

        var packageDirectory = Path.Combine(_packageRoot, intent.Slug);
        Directory.CreateDirectory(packageDirectory);
        var packagePath = Path.Combine(packageDirectory, package.Version + ".nplay");

        using (var download = await client.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            if (!download.IsSuccessStatusCode)
            {
                _logger.LogWarning("Nelfe Play : telechargement de {Slug} refuse (HTTP {Status}).", intent.Slug, (int)download.StatusCode);
                return false;
            }

            await using var source = await download.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(packagePath);
            await source.CopyToAsync(target, cancellationToken);
        }

        // Empreinte du paquet : ce qui a ete telecharge est bien ce que le
        // serveur a fabrique.
        if (!string.IsNullOrWhiteSpace(package.Sha256))
        {
            var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(packagePath, cancellationToken))).ToLowerInvariant();
            if (!string.Equals(actual, package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(packagePath);
                _logger.LogWarning("Nelfe Play : empreinte du paquet {Slug} invalide, installation abandonnee.", intent.Slug);
                return false;
            }
        }

        var header = ReadContainerHeader(packagePath);
        if (header is null)
        {
            File.Delete(packagePath);
            _logger.LogWarning("Nelfe Play : conteneur {Slug} illisible.", intent.Slug);
            return false;
        }

        // La licence n'est utile que si elle ouvre reellement ce paquet : on
        // descelle la cle de contenu ici, et on la re-scelle aussitot sous la
        // cle de la machine pour le lancement.
        byte[] contentKey;
        try
        {
            contentKey = _device.UnwrapContentKey(license.ContentKey);
        }
        catch (CryptographicException exception)
        {
            File.Delete(packagePath);
            _logger.LogWarning(exception, "Nelfe Play : licence de {Slug} inutilisable sur cette machine.", intent.Slug);
            return false;
        }

        try
        {
            WriteLocalLicense(packageDirectory, package, header, contentKey, license);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contentKey);
        }

        WritePlaceholder(systemId, package.TargetFileName, intent, header);
        _logger.LogInformation(
            "Nelfe Play : {Slug} installe (systeme {System}, profil {Profile}) — ROM chiffree, dechiffrement au lancement.",
            intent.Slug,
            systemId,
            header.Profile);

        return true;
    }

    /// <summary>
    /// Licence locale : la cle de contenu re-scellee sous la cle de CETTE
    /// machine, avec ce qu'il faut pour dechiffrer au lancement. Le fichier est
    /// inutilisable ailleurs.
    /// </summary>
    private void WriteLocalLicense(string directory, WorkPackage package, ContainerHeader header, byte[] contentKey, WorkLicense issued)
    {
        var license = new LocalLicense
        {
            Profile = header.Profile,
            Cipher = header.Cipher,
            Iv = header.Iv,
            Tag = header.Tag,
            TargetFileName = header.TargetFileName,
            MasterFile = header.MasterFile,
            RomMd5 = header.RomMd5,
            CanonicalGroup = header.CanonicalGroup,
            SystemId = header.SystemId,
            ClearSize = header.ClearSize,
            PackageFile = package.Version + ".nplay",
            SealedContentKey = SealForThisMachine(contentKey),
            ExpiresAt = issued.ExpiresAt,
            DeviceId = issued.DeviceId,
            SessionId = issued.SessionId,
            SignedPayload = issued.Payload,
            Signature = issued.Signature,
        };

        File.WriteAllText(
            Path.Combine(directory, "license.json"),
            JsonSerializer.Serialize(license, new JsonSerializerOptions { WriteIndented = true }));
    }

    private string SealForThisMachine(byte[] contentKey)
        => _device.ProtectBytes(contentKey);

    /// <summary>
    /// Placeholder : un fichier leger a l'extension native, pour qu'
    /// EmulationStation liste le jeu, le scrape et le lance normalement. La
    /// ROM reelle n'existe qu'en clair, brievement, au lancement.
    /// </summary>
    private void WritePlaceholder(string systemId, string targetFileName, WorkIntent intent, ContainerHeader header)
    {
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, systemId);
        Directory.CreateDirectory(systemRoot);
        var destination = Path.Combine(systemRoot, targetFileName);

        var payload = string.Join(
            Environment.NewLine,
            PlaceholderMarker,
            "This lightweight file keeps EmulationStation listing a Nelfe Play title.",
            "The real ROM stays encrypted until launch, then is wiped.",
            "system=" + systemId,
            "slug=" + intent.Slug,
            "md5=" + header.RomMd5,
            "group=" + header.CanonicalGroup,
            string.Empty);

        File.WriteAllText(destination, payload, Encoding.UTF8);
    }

    private ContainerHeader? ReadContainerHeader(string packagePath)
    {
        using var stream = File.OpenRead(packagePath);
        Span<byte> prologue = stackalloc byte[8];
        if (stream.Read(prologue) != prologue.Length
            || Encoding.ASCII.GetString(prologue[..6]) != ContainerMagic)
        {
            return null;
        }

        var headerLength = BitConverter.ToUInt16(prologue[6..]);
        var headerBytes = new byte[headerLength];
        if (stream.Read(headerBytes) != headerLength)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ContainerHeader>(headerBytes, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private HttpClient CreateClient(string? credential)
    {
        var client = _httpFactory.CreateClient();
        client.BaseAddress = new Uri(BaseUrl.TrimEnd('/'));
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (credential is not null)
        {
            client.DefaultRequestHeaders.Add("X-Nelfeplay-Device", credential);
        }

        return client;
    }

    public sealed record AgentStatus
    {
        public bool Paired { get; init; }
        public string? PlayerCode { get; init; }
        public int Entitlements { get; init; }
        public DateTime? LastPollUtc { get; init; }
        public int LastInstalled { get; init; }
        public string? LastError { get; init; }
    }

    private sealed class LicenseKeyResponse
    {
        public bool Ok { get; set; }
        public string? Algorithm { get; set; }
        public string? PublicKey { get; set; }
    }

    private sealed class PairResponse
    {
        public bool Ok { get; set; }
        public string? Credential { get; set; }
        public string? DeviceId { get; set; }
    }

    private sealed class WorkResponse
    {
        public bool Ok { get; set; }
        public string? PlayerCode { get; set; }
        public List<JsonElement>? Entitlements { get; set; }
        public List<WorkIntent>? Intents { get; set; }
    }

    private sealed class WorkIntent
    {
        public string Id { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string SystemId { get; set; } = string.Empty;
        public string InstallMode { get; set; } = string.Empty;
        public WorkPackage? Package { get; set; }
        public WorkLicense? License { get; set; }
    }

    private sealed class WorkPackage
    {
        public string Version { get; set; } = "1";
        public string TargetFileName { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string? Sha256 { get; set; }
        public long? SizeBytes { get; set; }
        public string EncryptionProfile { get; set; } = "gcm_full";
        public string? MasterFile { get; set; }
    }

    private sealed class WorkLicense
    {
        public string Algorithm { get; set; } = string.Empty;
        public string ContentKey { get; set; } = string.Empty;
        public string? IssuedAt { get; set; }
        public string? ExpiresAt { get; set; }
        public string? DeviceId { get; set; }
        public string? SessionId { get; set; }
        // Octets exactement signes par Nelfe Play (base64) et leur signature :
        // l'agent verifie ce qui a ete signe, sans re-serialiser du JSON.
        public string? Payload { get; set; }
        public string? Signature { get; set; }
    }

    private sealed class ContainerHeader
    {
        public int V { get; set; }
        public string Profile { get; set; } = "gcm_full";
        public string Cipher { get; set; } = "aes-256-gcm";
        public string Iv { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public string TargetFileName { get; set; } = string.Empty;
        public string? MasterFile { get; set; }
        public string RomMd5 { get; set; } = string.Empty;
        public string CanonicalGroup { get; set; } = string.Empty;
        public string SystemId { get; set; } = string.Empty;
        public long ClearSize { get; set; }
    }

    private sealed class LocalLicense
    {
        public string Profile { get; set; } = string.Empty;
        public string Cipher { get; set; } = string.Empty;
        public string Iv { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public string TargetFileName { get; set; } = string.Empty;
        public string? MasterFile { get; set; }
        public string RomMd5 { get; set; } = string.Empty;
        public string CanonicalGroup { get; set; } = string.Empty;
        public string SystemId { get; set; } = string.Empty;
        public long ClearSize { get; set; }
        public string PackageFile { get; set; } = string.Empty;
        public string SealedContentKey { get; set; } = string.Empty;
        // Ce qui rend la licence OPPOSABLE au lancement.
        public string? ExpiresAt { get; set; }
        public string? DeviceId { get; set; }
        public string? SessionId { get; set; }
        public string? SignedPayload { get; set; }
        public string? Signature { get; set; }
    }
}
