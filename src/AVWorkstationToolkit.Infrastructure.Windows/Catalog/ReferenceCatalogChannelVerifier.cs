using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Infrastructure.Windows.Catalog;

public sealed record VerifiedReferenceCatalogChannel(
    string CatalogVersion,
    long Revision,
    long PreviousRevision,
    string MinimumAppVersion,
    DateTimeOffset CreatedUtc,
    string SigningKeyId,
    Uri BundleUri,
    string BundleSha256);

/// <summary>Verifies the exact detached-signature channel document consumed by the runtime HTTP client.</summary>
public sealed class ReferenceCatalogChannelVerifier
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };
    private readonly IReadOnlyDictionary<string, string> trustedPublicKeys;

    public ReferenceCatalogChannelVerifier(IReadOnlyDictionary<string, string> trustedPublicKeys)
    {
        ArgumentNullException.ThrowIfNull(trustedPublicKeys);
        this.trustedPublicKeys = trustedPublicKeys.ToFrozenDictionary(StringComparer.Ordinal);
        if (this.trustedPublicKeys.Count == 0)
            throw new ArgumentException("At least one trusted reference-catalog channel key is required.", nameof(trustedPublicKeys));
    }

    public VerifiedReferenceCatalogChannel Verify(byte[] metadataBytes, byte[] signatureBytes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(metadataBytes);
        ArgumentNullException.ThrowIfNull(signatureBytes);
        if (metadataBytes.Length is <= 0 or > ReferenceCatalogChannelClient.MaximumMetadataBytes ||
            signatureBytes.Length is <= 0 or > ReferenceCatalogChannelClient.MaximumSignatureBytes)
            throw new CatalogValidationException("Reference catalog channel metadata or signature size is invalid.");

        string json;
        try { json = StrictUtf8.GetString(metadataBytes); }
        catch (DecoderFallbackException exception) { throw new CatalogValidationException($"Reference catalog channel is not valid UTF-8: {exception.Message}"); }

        ChannelDocument raw;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
            RejectDuplicates(document.RootElement);
            raw = JsonSerializer.Deserialize<ChannelDocument>(json, JsonOptions)
                ?? throw new CatalogValidationException("Reference catalog channel is empty.");
        }
        catch (JsonException exception) { throw new CatalogValidationException($"Reference catalog channel JSON is invalid: {exception.Message}"); }

        if (!string.Equals(raw.CatalogId, ReferenceCatalogBundleNames.CatalogId, StringComparison.Ordinal) || raw.SchemaVersion != 1 || raw.Revision <= 0 ||
            raw.PreviousRevision < 0 || raw.PreviousRevision >= raw.Revision || !IsVersion(raw.CatalogVersion) || !IsVersion(raw.MinimumAppVersion) ||
            string.IsNullOrWhiteSpace(raw.SigningKeyId) || raw.SigningKeyId.Length > 128 || raw.SigningKeyId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(raw.BundleSha256) || raw.BundleSha256.Length != 64 || !raw.BundleSha256.All(Uri.IsHexDigit))
            throw new CatalogValidationException("Reference catalog channel metadata is invalid.");

        // CreatedUtc is the publication timestamp, kept for display and audit. Age alone never
        // invalidates an authentic signed pointer: descriptive catalog data cannot grant execution
        // authority, so a replayed old pointer costs the user nothing, while a hard expiry would
        // break Device Lookup updates whenever the pointer was not re-signed on a schedule.
        if (raw.CreatedUtc == default || raw.CreatedUtc.Offset != TimeSpan.Zero || raw.CreatedUtc > now.AddMinutes(5))
            throw new CatalogValidationException("Reference catalog channel publication timestamp is invalid.");

        if (!Uri.TryCreate(raw.BundleUri, UriKind.Absolute, out var bundleUri) || bundleUri.Scheme != Uri.UriSchemeHttps || !bundleUri.IsDefaultPort ||
            !string.IsNullOrEmpty(bundleUri.UserInfo) || string.IsNullOrWhiteSpace(bundleUri.IdnHost) ||
            !Path.GetExtension(bundleUri.AbsolutePath).Equals(".avwtcatalog", StringComparison.OrdinalIgnoreCase))
            throw new CatalogValidationException("Reference catalog channel BundleUri is invalid.");

        VerifySignature(raw.SigningKeyId!, metadataBytes, signatureBytes);
        return new(raw.CatalogVersion!, raw.Revision, raw.PreviousRevision, raw.MinimumAppVersion!, raw.CreatedUtc,
            raw.SigningKeyId!, bundleUri, raw.BundleSha256!.ToUpperInvariant());
    }

    private void VerifySignature(string keyId, byte[] metadata, byte[] signature)
    {
        if (!trustedPublicKeys.TryGetValue(keyId, out var pem) || signature.Length != 64)
            throw new CatalogValidationException("Reference catalog channel signature key or encoding is invalid.");
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            if (key.KeySize != 256 || !key.VerifyData(metadata, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new CatalogValidationException("Reference catalog channel signature verification failed.");
        }
        catch (ArgumentException exception) { throw new CatalogValidationException($"Trusted reference catalog public key is invalid: {exception.Message}"); }
        catch (CryptographicException exception) { throw new CatalogValidationException($"Reference catalog channel signature verification failed: {exception.Message}"); }
    }

    private static bool IsVersion(string? value) => Version.TryParse(value, out _) && value.Length <= 32;

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new CatalogValidationException("Reference catalog channel must be a JSON object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name)) throw new CatalogValidationException($"Reference catalog channel repeats JSON property '{property.Name}'.");
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ChannelDocument
    {
        public string? CatalogId { get; init; }
        public int SchemaVersion { get; init; }
        public string? CatalogVersion { get; init; }
        public long Revision { get; init; }
        public long PreviousRevision { get; init; }
        public string? MinimumAppVersion { get; init; }
        public DateTimeOffset CreatedUtc { get; init; }
        // Accepted but ignored. Already-signed pointers carry it and their bytes cannot be re-signed here.
        public DateTimeOffset? ExpiresUtc { get; init; }
        public string? SigningKeyId { get; init; }
        public string? BundleUri { get; init; }
        public string? BundleSha256 { get; init; }
    }
}
