using System.Security.Cryptography;
using System.Text;

// VENDORÉ depuis NelfeScoringProtocol/ref/csharp (source de vérité + vecteurs).
// Ne pas diverger : corriger dans le repo public puis re-vendorer.
namespace RetroBat.Api.Scoring;

/// <summary>
/// Primitives crypto FIGÉES (SPEC v1.0 §5.1) : SHA-256 hex minuscule ; ECDSA P-256,
/// clé publique SPKI DER, signature ASN.1 DER, base64url sans padding ;
/// key_id = SHA-256(SPKI_DER).
/// </summary>
public static class Crypto
{
    public static string Sha256Hex(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256Hex(string utf8) => Sha256Hex(Encoding.UTF8.GetBytes(utf8));

    public static string KeyId(byte[] spkiDer) => Sha256Hex(spkiDer);

    public static string B64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromB64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        switch (t.Length % 4) { case 2: t += "=="; break; case 3: t += "="; break; }
        return Convert.FromBase64String(t);
    }

    public static string SignB64Url(ECDsa privateKey, byte[] message)
        => B64Url(privateKey.SignData(message, HashAlgorithmName.SHA256,
                                      DSASignatureFormat.Rfc3279DerSequence));

    public static bool Verify(byte[] spkiDer, byte[] message, string signatureB64Url)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(spkiDer, out _);
            return ec.VerifyData(message, FromB64Url(signatureB64Url),
                                 HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch { return false; }
    }
}
