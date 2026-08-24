using System.Security.Cryptography;

namespace RetroBat.Api.Scoring;

/// <summary>
/// Clé de signature scoring de l'appareil : ECDSA P-256 PERSISTÉE via Windows
/// CNG/NCrypt, NON exportable. Générée à la demande, retrouvée par nom d'une session à
/// l'autre. Fournit la clé publique (SPKI PEM à enrôler), le key_id et la signature
/// base64url du corps du passeport. Distincte de la clé de licence (RSA) de l'appareil.
/// Chemin de signature prouvé dans NelfeScoringProtocol (dotnet run -- cng).
/// </summary>
public sealed class CngDeviceKey : IDisposable
{
    public const string DefaultKeyName = "Nelfe.Scoring.Device";

    private readonly ECDsaCng _ecdsa;

    private CngDeviceKey(ECDsaCng ecdsa) => _ecdsa = ecdsa;

    /// <summary>Ouvre la clé persistée de l'appareil, ou la crée si absente.</summary>
    public static CngDeviceKey OpenOrCreate(string keyName = DefaultKeyName)
    {
        CngKey key;
        if (CngKey.Exists(keyName))
        {
            key = CngKey.Open(keyName);
        }
        else
        {
            var creation = new CngKeyCreationParameters
            {
                ExportPolicy = CngExportPolicies.None,          // privée non exportable
                KeyUsage = CngKeyUsages.Signing,
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            };
            key = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, creation);
        }

        return new CngDeviceKey(new ECDsaCng(key));
    }

    /// <summary>SPKI DER de la clé publique (ce que le vérifieur manipule).</summary>
    public byte[] SpkiDer => _ecdsa.ExportSubjectPublicKeyInfo();

    /// <summary>Clé publique en PEM (à publier via /scores/enroll-key).</summary>
    public string PublicKeyPem => _ecdsa.ExportSubjectPublicKeyInfoPem();

    /// <summary>key_id = SHA-256(SPKI DER) - l'identifiant du champ device.key_id.</summary>
    public string KeyId => Crypto.KeyId(SpkiDer);

    /// <summary>Signe un message (les octets JCS du corps) → base64url ASN.1 DER.</summary>
    public string SignB64Url(byte[] message) => Crypto.SignB64Url(_ecdsa, message);

    public void Dispose() => _ecdsa.Dispose();
}
