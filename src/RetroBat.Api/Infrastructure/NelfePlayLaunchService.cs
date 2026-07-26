using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Dechiffrement au LANCEMENT d'un jeu Nelfe Play, puis effacement.
///
/// Sur le disque, un titre Nelfe Play n'est qu'un placeholder : le paquet reste
/// chiffre, et la cle de contenu est scellee sous la cle de CETTE machine. Au
/// demarrage du jeu, ce service dechiffre le paquet a l'emplacement attendu par
/// l'emulateur ; a la fin de la partie, la ROM en clair est ECRASEE puis
/// supprimee, et le placeholder revient.
///
/// Rien de clair ne survit a la partie — ni apres un arret brutal : un balayage
/// au demarrage nettoie ce qu'un plantage aurait laisse.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NelfePlayLaunchService : BackgroundService
{
    private const string PlaceholderMarker = "NELFEPLAY_ENCRYPTED_ROM_PLACEHOLDER_v1";
    private const string ContainerMagic = "NPLAY1";
    private const int GcmTagBytes = 16;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// La charge SIGNEE vient du serveur, qui nomme en snake_case (device_id,
    /// session_id, expires_at). Le fichier de licence local, lui, est ecrit par
    /// l'agent en PascalCase. Deux conventions, deux jeux d'options : les
    /// melanger ferait lire des champs vides d'un cote ou de l'autre — et ici,
    /// un champ vide c'est une contrainte qui ne s'applique plus.
    /// </summary>
    private static readonly JsonSerializerOptions SignedPayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IEventBus _eventBus;
    private readonly NelfePlayDeviceStore _device;
    private readonly ILogger<NelfePlayLaunchService> _logger;
    private readonly string _packageRoot =
        Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "packages");
    private readonly object _gate = new();

    private IDisposable? _subscription;

    public NelfePlayLaunchService(
        IEventBus eventBus,
        NelfePlayDeviceStore device,
        ILogger<NelfePlayLaunchService> logger)
    {
        _eventBus = eventBus;
        _device = device;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Un plantage a pu laisser une ROM en clair : on nettoie avant tout.
        SweepClearRoms();
        _subscription = _eventBus.Subscribe<EventEnvelope>(OnEvent);
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }

    private void OnEvent(EventEnvelope envelope)
    {
        try
        {
            if (string.Equals(envelope.Type, "ui.game.started", StringComparison.OrdinalIgnoreCase))
            {
                var (systemId, gamePath) = ExtractGame(envelope.Payload);
                if (gamePath is { Length: > 0 })
                {
                    PrepareForLaunch(systemId, gamePath);
                }

                return;
            }

            if (string.Equals(envelope.Type, "ui.game.ended", StringComparison.OrdinalIgnoreCase))
            {
                var (systemId, gamePath) = ExtractGame(envelope.Payload);
                if (gamePath is { Length: > 0 })
                {
                    WipeAfterPlay(systemId, gamePath);
                    return;
                }

                // Fin de partie sans chemin exploitable : on nettoie largement.
                SweepClearRoms();
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Nelfe Play : traitement du cycle de jeu impossible.");
        }
    }

    /// <summary>
    /// Dechiffre le paquet a l'emplacement du placeholder, juste avant que
    /// l'emulateur ne lise le fichier.
    /// </summary>
    private void PrepareForLaunch(string systemId, string gamePath)
    {
        lock (_gate)
        {
            var target = ResolveTargetPath(systemId, gamePath);
            if (target is null || !IsPlaceholder(target))
            {
                return; // jeu ordinaire, ou deja dechiffre
            }

            var entry = FindLicenseFor(target);
            if (entry is null)
            {
                return;
            }

            var (directory, license) = entry.Value;

            // La licence est OPPOSABLE : on refuse d'ouvrir le paquet si elle a
            // expire, si elle vise une autre machine, ou si sa signature ne
            // tient pas. C'est ce qui donne un effet reel a la fin d'une
            // session en salle : la cle deja remise cesse de servir.
            if (!IsLicenseUsable(license, out var refusal))
            {
                _logger.LogInformation(
                    "Nelfe Play : lancement refuse pour {File} — {Reason}.",
                    license.TargetFileName,
                    refusal);
                return;
            }

            var packagePath = Path.Combine(directory, license.PackageFile);
            if (!File.Exists(packagePath))
            {
                _logger.LogWarning("Nelfe Play : paquet absent pour {File}.", license.TargetFileName);
                return;
            }

            byte[] contentKey;
            try
            {
                contentKey = _device.UnprotectBytes(license.SealedContentKey);
            }
            catch (CryptographicException exception)
            {
                _logger.LogWarning(exception, "Nelfe Play : cle de contenu illisible sur cette machine.");
                return;
            }

            try
            {
                var clear = DecryptPackage(packagePath, contentKey, license);
                if (clear is null)
                {
                    return;
                }

                File.WriteAllBytes(target, clear);
                CryptographicOperations.ZeroMemory(clear);
                _logger.LogInformation(
                    "Nelfe Play : {File} dechiffre pour la partie (efface a la fin).",
                    license.TargetFileName);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(contentKey);
            }
        }
    }

    /// <summary>Efface la ROM en clair et remet le placeholder.</summary>
    private void WipeAfterPlay(string systemId, string gamePath)
    {
        lock (_gate)
        {
            var target = ResolveTargetPath(systemId, gamePath);
            if (target is null || !File.Exists(target) || IsPlaceholder(target))
            {
                return;
            }

            var entry = FindLicenseFor(target);
            if (entry is null)
            {
                return;
            }

            WipeAndRestore(target, entry.Value.License);
        }
    }

    /// <summary>
    /// Balaye les titres Nelfe Play connus et remet en place tout ce qui serait
    /// reste en clair (plantage, coupure de courant).
    /// </summary>
    private void SweepClearRoms()
    {
        lock (_gate)
        {
            foreach (var (_, license) in ReadLicenses())
            {
                var target = BuildRomPath(license.SystemId, license.TargetFileName);
                if (File.Exists(target) && !IsPlaceholder(target))
                {
                    WipeAndRestore(target, license);
                    _logger.LogInformation(
                        "Nelfe Play : ROM en clair laissee par une partie interrompue effacee ({File}).",
                        license.TargetFileName);
                }
            }
        }
    }

    private void WipeAndRestore(string target, LocalLicense license)
    {
        try
        {
            // Ecrasement avant suppression : le contenu ne reste pas lisible
            // dans les secteurs liberes.
            var length = new FileInfo(target).Length;
            if (length > 0)
            {
                using (var stream = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    RandomNumberGenerator.Fill(buffer);
                    long written = 0;
                    while (written < length)
                    {
                        var chunk = (int)Math.Min(buffer.Length, length - written);
                        stream.Write(buffer, 0, chunk);
                        written += chunk;
                    }

                    stream.Flush(true);
                }
            }

            File.Delete(target);
            WritePlaceholder(target, license);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Nelfe Play : effacement de {Path} impossible pour l'instant.", target);
        }
    }

    /// <summary>
    /// Une licence n'est utilisable que si elle est authentique, encore
    /// valable, et destinee a CETTE machine.
    ///
    /// Deux failles se cachaient ici, et elles se completaient.
    ///
    /// LA PREMIERE : une licence sans signature etait acceptee, au motif qu'une
    /// installation durable n'en porte pas. Il suffisait donc d'effacer deux
    /// champs d'un fichier JSON pour transformer une licence de session — qui
    /// expire — en licence perpetuelle.
    ///
    /// LA SECONDE, plus discrete : la signature couvre la charge signee, mais
    /// c'etaient les champs EN CLAIR qu'on faisait respecter. On pouvait donc
    /// garder une signature parfaitement valable et reecrire l'expiration a
    /// cote. Verifier une signature sans lire ce qu'elle couvre ne verifie
    /// rien.
    ///
    /// Desormais : la charge signee fait foi, et une signature est EXIGEE des
    /// lors que la machine detient une cle publique de verification — c'est-a-
    /// dire des qu'elle a ete appairee. Une machine qui n'en a pas ne peut rien
    /// verifier, et l'installation sans signature y reste possible : c'est le
    /// cas d'un serveur qui ne signe pas encore.
    /// </summary>
    private bool IsLicenseUsable(LocalLicense license, out string refusal)
    {
        refusal = string.Empty;

        var publicKey = _device.LicensePublicKey;
        var hasSignature = license.Signature is { Length: > 0 }
            && license.SignedPayload is { Length: > 0 };

        if (publicKey is { Length: > 0 })
        {
            if (!hasSignature)
            {
                // C'est ici que l'effacement des deux champs ne paie plus.
                refusal = "licence non signee";
                return false;
            }
        }
        else if (!hasSignature)
        {
            // Rien a verifier, et rien avec quoi verifier : on ne peut pas
            // exiger mieux sans empecher toute installation.
            return IsWithinPlainConstraints(license, out refusal);
        }

        if (publicKey is not { Length: > 0 })
        {
            refusal = "cle publique de verification absente";
            return false;
        }

        SignedLicense? signed;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKey);
            var payloadBytes = Convert.FromBase64String(license.SignedPayload!);
            var verified = rsa.VerifyData(
                payloadBytes,
                Convert.FromBase64String(license.Signature!),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            if (!verified)
            {
                refusal = "signature invalide";
                return false;
            }

            // On lit CE QUI A ETE SIGNE, jamais ce qui l'accompagne.
            signed = JsonSerializer.Deserialize<SignedLicense>(payloadBytes, SignedPayloadJsonOptions);
        }
        catch (Exception exception)
            when (exception is CryptographicException or FormatException or ArgumentException or JsonException)
        {
            refusal = "signature invérifiable";
            return false;
        }

        if (signed is null)
        {
            refusal = "charge signee illisible";
            return false;
        }

        if (signed.ExpiresAt is not { Length: > 0 } expiry
            || !DateTimeOffset.TryParse(expiry, out var expiresAt))
        {
            // Une licence signee SANS expiration n'existe pas : le serveur en
            // pose toujours une. Son absence signale une charge fabriquee.
            refusal = "expiration absente de la charge signee";
            return false;
        }

        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            refusal = "licence expiree";
            return false;
        }

        if (_device.DeviceId is { Length: > 0 } thisDevice
            && !string.Equals(signed.DeviceId ?? string.Empty, thisDevice, StringComparison.OrdinalIgnoreCase))
        {
            refusal = "licence destinee a une autre machine";
            return false;
        }

        // Les champs en clair servent au fonctionnement (nom de fichier, IV,
        // taille) ; ceux qui ENGAGENT doivent correspondre a la charge signee,
        // sinon le fichier ment sur ce qu'il autorise.
        if (license.ExpiresAt is { Length: > 0 } plainExpiry
            && !string.Equals(plainExpiry, expiry, StringComparison.Ordinal))
        {
            refusal = "expiration en clair differente de la charge signee";
            return false;
        }

        if (license.SessionId is { Length: > 0 } plainSession
            && !string.Equals(plainSession, signed.SessionId ?? string.Empty, StringComparison.Ordinal))
        {
            refusal = "session en clair differente de la charge signee";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Le cas degrade : aucune signature nulle part. On applique alors les
    /// champs en clair, faute de mieux — et en sachant qu'ils ne prouvent rien.
    /// </summary>
    private bool IsWithinPlainConstraints(LocalLicense license, out string refusal)
    {
        refusal = string.Empty;

        if (license.ExpiresAt is { Length: > 0 } expiry)
        {
            if (!DateTimeOffset.TryParse(expiry, out var expiresAt))
            {
                refusal = "expiration illisible";
                return false;
            }

            if (expiresAt <= DateTimeOffset.UtcNow)
            {
                refusal = "licence expiree";
                return false;
            }
        }

        if (license.DeviceId is { Length: > 0 } licensedDevice
            && _device.DeviceId is { Length: > 0 } thisDevice
            && !string.Equals(licensedDevice, thisDevice, StringComparison.OrdinalIgnoreCase))
        {
            refusal = "licence destinee a une autre machine";
            return false;
        }

        return true;
    }

    /// <summary>Ce que la signature couvre reellement, cote serveur.</summary>
    private sealed class SignedLicense
    {
        public string? DeviceId { get; set; }
        public string? SessionId { get; set; }
        public string? Game { get; set; }
        public string? ExpiresAt { get; set; }
    }

    private byte[]? DecryptPackage(string packagePath, byte[] contentKey, LocalLicense license)
    {
        var raw = File.ReadAllBytes(packagePath);
        if (raw.Length < 8 || Encoding.ASCII.GetString(raw, 0, 6) != ContainerMagic)
        {
            _logger.LogWarning("Nelfe Play : conteneur invalide ({Path}).", packagePath);
            return null;
        }

        var headerLength = BitConverter.ToUInt16(raw, 6);
        var bodyOffset = 8 + headerLength;
        if (bodyOffset >= raw.Length)
        {
            return null;
        }

        var iv = Convert.FromBase64String(license.Iv);
        var tag = Convert.FromBase64String(license.Tag);
        var cipher = new ReadOnlySpan<byte>(raw, bodyOffset, raw.Length - bodyOffset);
        var clear = new byte[cipher.Length];

        try
        {
            using var gcm = new AesGcm(contentKey, GcmTagBytes);
            gcm.Decrypt(iv, cipher, tag, clear);
        }
        catch (CryptographicException exception)
        {
            // Tag invalide : paquet altere ou mauvaise cle. On n'ecrit rien.
            _logger.LogWarning(exception, "Nelfe Play : paquet refuse (integrite ou cle).");
            CryptographicOperations.ZeroMemory(clear);
            return null;
        }

        return clear;
    }

    private (string Directory, LocalLicense License)? FindLicenseFor(string targetPath)
    {
        var fileName = Path.GetFileName(targetPath);
        var systemId = Path.GetFileName(Path.GetDirectoryName(targetPath) ?? string.Empty);

        foreach (var (directory, license) in ReadLicenses())
        {
            if (string.Equals(license.TargetFileName, fileName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(license.SystemId, systemId, StringComparison.OrdinalIgnoreCase))
            {
                return (directory, license);
            }
        }

        return null;
    }

    private IEnumerable<(string Directory, LocalLicense License)> ReadLicenses()
    {
        if (!Directory.Exists(_packageRoot))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(_packageRoot))
        {
            var path = Path.Combine(directory, "license.json");
            if (!File.Exists(path))
            {
                continue;
            }

            LocalLicense? license = null;
            try
            {
                license = JsonSerializer.Deserialize<LocalLicense>(File.ReadAllText(path), JsonOptions);
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                _logger.LogWarning(exception, "Nelfe Play : licence locale illisible ({Path}).", path);
            }

            if (license is { TargetFileName.Length: > 0 })
            {
                yield return (directory, license);
            }
        }
    }

    private static string BuildRomPath(string systemId, string fileName)
        => Path.Combine(RetroBatPaths.RomsRoot, systemId, fileName);

    /// <summary>
    /// Chemin reel du fichier vise par l'evenement. On refuse tout ce qui
    /// sortirait du dossier des ROMs.
    /// </summary>
    private static string? ResolveTargetPath(string systemId, string gamePath)
    {
        try
        {
            var candidate = Path.IsPathRooted(gamePath)
                ? Path.GetFullPath(gamePath)
                : Path.GetFullPath(BuildRomPath(systemId, Path.GetFileName(gamePath)));

            var romsRoot = Path.GetFullPath(RetroBatPaths.RomsRoot);
            return candidate.StartsWith(romsRoot, StringComparison.OrdinalIgnoreCase) ? candidate : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsPlaceholder(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 4096)
            {
                return false;
            }

            using var reader = new StreamReader(path);
            var firstLine = reader.ReadLine();
            return firstLine is not null && firstLine.StartsWith(PlaceholderMarker, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WritePlaceholder(string destination, LocalLicense license)
    {
        var payload = string.Join(
            Environment.NewLine,
            PlaceholderMarker,
            "This lightweight file keeps EmulationStation listing a Nelfe Play title.",
            "The real ROM stays encrypted until launch, then is wiped.",
            "system=" + license.SystemId,
            "md5=" + license.RomMd5,
            "group=" + license.CanonicalGroup,
            string.Empty);

        File.WriteAllText(destination, payload, Encoding.UTF8);
    }

    /// <summary>Systeme et chemin du jeu, quel que soit la forme du payload.</summary>
    private static (string SystemId, string? GamePath) ExtractGame(object? payload)
    {
        if (payload is null)
        {
            return (string.Empty, null);
        }

        var context = payload.GetType().GetProperty("Context")?.GetValue(payload);
        var selected = context?.GetType().GetProperty("Selected")?.GetValue(context)
            ?? context?.GetType().GetProperty("Running")?.GetValue(context);
        if (selected is not null)
        {
            var systemId = selected.GetType().GetProperty("SystemId")?.GetValue(selected) as string ?? string.Empty;
            var gamePath = selected.GetType().GetProperty("GamePath")?.GetValue(selected) as string;
            if (!string.IsNullOrWhiteSpace(gamePath))
            {
                return (systemId, gamePath);
            }
        }

        if (payload.GetType().GetProperty("RawArgs")?.GetValue(payload) is IEnumerable<string> args)
        {
            var values = args.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            if (values.Length >= 2)
            {
                return (values[0], values[1]);
            }
        }

        return (string.Empty, null);
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
        // Ce qui rend la licence opposable au lancement.
        public string? ExpiresAt { get; set; }
        public string? DeviceId { get; set; }
        public string? SessionId { get; set; }
        public string? SignedPayload { get; set; }
        public string? Signature { get; set; }
    }
}
