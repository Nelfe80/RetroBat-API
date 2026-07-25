using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Identite de CETTE machine aupres de Nelfe Play (doc 06 : enveloppe CEK/KEK).
///
/// La cle privee de l'appareil est creee dans le coffre de cles de Windows et
/// marquee NON EXPORTABLE : elle ne quitte jamais la machine, pas meme vers un
/// fichier. Nelfe Play ne recoit que la cle publique, a l'appairage, et s'en
/// sert pour sceller les licences (la cle de contenu d'un jeu) a destination de
/// cette machine et d'aucune autre.
///
/// Le credential d'appareil (secret porteur) est conserve chiffre SOUS CETTE
/// MEME CLE : le fichier d'etat copie sur une autre machine est inutilisable.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NelfePlayDeviceStore
{
    // Le remplissage doit correspondre a celui du serveur : PHP scelle en
    // OAEP avec SHA-1 (openssl_public_encrypt), pas SHA-256.
    private static readonly RSAEncryptionPadding LicensePadding = RSAEncryptionPadding.OaepSHA1;

    private const string KeyContainerName = "NelfePlay.DeviceKey";
    private const int KeySizeBits = 2048;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILogger<NelfePlayDeviceStore> _logger;
    private readonly string _statePath =
        Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "device.json");
    private readonly object _gate = new();

    public NelfePlayDeviceStore(ILogger<NelfePlayDeviceStore> logger)
    {
        _logger = logger;
    }

    /// <summary>Nom de l'algorithme annonce au serveur a l'appairage.</summary>
    public const string KeyAlgorithm = "rsa-oaep-sha1";

    /// <summary>Cette machine est-elle appairee a un compte Nelfe Play ?</summary>
    public bool IsPaired => ReadState()?.Credential is { Length: > 0 };

    /// <summary>Identifiant d'appareil remis par Nelfe Play, ou null.</summary>
    public string? DeviceId => ReadState()?.DeviceId;

    /// <summary>
    /// Cle publique de l'appareil au format PEM. La cle est creee au premier
    /// appel et reste ensuite la meme pour la duree de vie de la machine.
    /// </summary>
    public string ExportPublicKeyPem()
    {
        using var rsa = OpenOrCreateKey();
        return PemEncoding.WriteString("PUBLIC KEY", rsa.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Enregistre le resultat d'un appairage reussi.</summary>
    public void SavePairing(string deviceId, string credential)
    {
        var sealedCredential = Protect(credential);
        var state = new DeviceState
        {
            DeviceId = deviceId,
            Credential = sealedCredential,
            PairedAtUtc = DateTime.UtcNow.ToString("O"),
            // La cle publique deja epinglee ne doit pas disparaitre : sans
            // elle, plus aucune licence ne serait verifiable.
            LicensePublicKey = ReadState()?.LicensePublicKey,
        };

        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(state, JsonOptions));
        }

        _logger.LogInformation("Nelfe Play : appareil appaire (device {DeviceId}).", deviceId);
    }

    /// <summary>Credential en clair pour les appels sortants, ou null.</summary>
    public string? GetCredential()
    {
        var state = ReadState();
        if (state?.Credential is not { Length: > 0 } sealedCredential)
        {
            return null;
        }

        try
        {
            return Unprotect(sealedCredential);
        }
        catch (CryptographicException exception)
        {
            // Etat copie depuis une autre machine, ou cle du coffre remplacee :
            // on ne peut plus rien en faire, il faut re-appairer.
            _logger.LogWarning(exception, "Nelfe Play : credential illisible sur cette machine, re-appairage necessaire.");
            return null;
        }
    }

    /// <summary>
    /// Cle PUBLIQUE de Nelfe Play, epinglee a l'appairage : elle sert a
    /// VERIFIER les licences. L'agent ne detient aucun secret de signature, il
    /// ne peut donc pas en fabriquer une.
    /// </summary>
    public string? LicensePublicKey
    {
        get => ReadState()?.LicensePublicKey;
    }

    public void SaveLicensePublicKey(string publicKeyPem)
    {
        var state = ReadState() ?? new DeviceState();
        state.LicensePublicKey = publicKeyPem;

        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(state, JsonOptions));
        }
    }

    /// <summary>Oublie l'appairage (le joueur delie sa machine).</summary>
    public void Forget()
    {
        lock (_gate)
        {
            if (File.Exists(_statePath))
            {
                File.Delete(_statePath);
            }
        }
    }

    /// <summary>
    /// Descelle une cle de contenu recue dans une licence : c'est la seule
    /// operation qui utilise la cle privee, et elle n'a lieu qu'ici.
    /// </summary>
    public byte[] UnwrapContentKey(string base64WrappedKey)
    {
        using var rsa = OpenOrCreateKey();
        return rsa.Decrypt(Convert.FromBase64String(base64WrappedKey), LicensePadding);
    }

    /// <summary>
    /// Scelle une cle de contenu SOUS LA CLE DE CETTE MACHINE, pour la
    /// conserver jusqu'au lancement. Le fichier produit est inutilisable
    /// ailleurs : la cle privee n'existe que dans le coffre local.
    /// </summary>
    public string ProtectBytes(byte[] value)
    {
        using var rsa = OpenOrCreateKey();
        return Convert.ToBase64String(rsa.Encrypt(value, LicensePadding));
    }

    /// <summary>Descelle une cle conservee localement (au lancement du jeu).</summary>
    public byte[] UnprotectBytes(string sealedValue)
    {
        using var rsa = OpenOrCreateKey();
        return rsa.Decrypt(Convert.FromBase64String(sealedValue), LicensePadding);
    }

    private string Protect(string value)
    {
        using var rsa = OpenOrCreateKey();
        return Convert.ToBase64String(rsa.Encrypt(Encoding.UTF8.GetBytes(value), LicensePadding));
    }

    private string Unprotect(string sealedValue)
    {
        using var rsa = OpenOrCreateKey();
        return Encoding.UTF8.GetString(rsa.Decrypt(Convert.FromBase64String(sealedValue), LicensePadding));
    }

    /// <summary>
    /// Ouvre la cle de l'appareil, en la creant au besoin. Elle vit dans le
    /// coffre de cles de Windows, en portee UTILISATEUR et sans export
    /// possible : aucun fichier de l'installation ne contient la cle privee.
    /// </summary>
    private RSACng OpenOrCreateKey()
    {
        if (CngKey.Exists(KeyContainerName))
        {
            return new RSACng(CngKey.Open(KeyContainerName));
        }

        var parameters = new CngKeyCreationParameters
        {
            ExportPolicy = CngExportPolicies.None,
            KeyCreationOptions = CngKeyCreationOptions.None,
            KeyUsage = CngKeyUsages.Decryption,
        };
        parameters.Parameters.Add(new CngProperty(
            "Length",
            BitConverter.GetBytes(KeySizeBits),
            CngPropertyOptions.None));

        var key = CngKey.Create(CngAlgorithm.Rsa, KeyContainerName, parameters);
        _logger.LogInformation("Nelfe Play : cle d'appareil creee (non exportable, coffre Windows).");
        return new RSACng(key);
    }

    private DeviceState? ReadState()
    {
        lock (_gate)
        {
            if (!File.Exists(_statePath))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<DeviceState>(File.ReadAllText(_statePath), JsonOptions);
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                _logger.LogWarning(exception, "Nelfe Play : etat d'appareil illisible.");
                return null;
            }
        }
    }

    private sealed class DeviceState
    {
        public string? DeviceId { get; set; }
        public string? Credential { get; set; }
        public string? PairedAtUtc { get; set; }
        public string? LicensePublicKey { get; set; }
    }
}
