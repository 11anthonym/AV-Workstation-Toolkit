using System.Security.Cryptography;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Infrastructure.Windows.Catalog;

internal static class CatalogSignatureVerifier
{
    public static void VerifyP256(
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        string keyId,
        byte[] content,
        byte[] signature,
        string description)
    {
        if (!trustedPublicKeys.TryGetValue(keyId, out var pem) || signature.Length != 64)
            throw new CatalogValidationException($"{description} signature key or encoding is invalid.");
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            if (key.KeySize != 256 ||
                !key.VerifyData(content, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new CatalogValidationException($"{description} signature verification failed.");
        }
        catch (ArgumentException exception)
        {
            throw new CatalogValidationException($"Trusted catalog public key is invalid: {exception.Message}");
        }
        catch (CryptographicException exception)
        {
            throw new CatalogValidationException($"{description} signature verification failed: {exception.Message}");
        }
    }
}
