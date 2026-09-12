using System.Net;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AVWorkstationToolkit.Application.Compatibility;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Infrastructure.Windows.Catalog;

public sealed record ReferenceCatalogChannelPolicy(
    Uri MetadataUri,
    Uri SignatureUri,
    IReadOnlySet<string> ApprovedHosts,
    IReadOnlyDictionary<string, string> TrustedPublicKeys,
    TimeSpan Timeout);

/// <summary>Fetches one exact signed channel. It has no caller-selected URL, headers, credentials, or destination path.</summary>
public sealed class ReferenceCatalogChannelClient : IReferenceCatalogChannelClient, IDisposable
{
    internal const int MaximumMetadataBytes = 32 * 1024;
    internal const int MaximumSignatureBytes = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };
    private readonly ReferenceCatalogChannelPolicy policy;
    private readonly HttpClient client;

    public ReferenceCatalogChannelClient(ReferenceCatalogChannelPolicy policy)
        : this(policy, new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseDefaultCredentials = false
        }, ownsHandler: true)
    {
    }

    internal ReferenceCatalogChannelClient(ReferenceCatalogChannelPolicy policy, HttpMessageHandler handler, bool ownsHandler = true)
    {
        this.policy = ValidatePolicy(policy);
        client = new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler)), ownsHandler) { Timeout = policy.Timeout };
    }

    public async Task<ReferenceCatalogChannelPackage?> GetLatestAsync(long currentRevision, CancellationToken cancellationToken = default)
    {
        if (currentRevision < 0) throw new ArgumentOutOfRangeException(nameof(currentRevision));
        var metadataBytes = await GetExactAsync(policy.MetadataUri, MaximumMetadataBytes, cancellationToken).ConfigureAwait(false);
        var signatureBytes = await GetExactAsync(policy.SignatureUri, MaximumSignatureBytes, cancellationToken).ConfigureAwait(false);
        var raw = ParseMetadata(metadataBytes);
        VerifySignature(raw.SigningKeyId!, metadataBytes, signatureBytes);

        var now = DateTimeOffset.UtcNow;
        if (raw.CreatedUtc == default || raw.CreatedUtc.Offset != TimeSpan.Zero || raw.ExpiresUtc == default || raw.ExpiresUtc.Offset != TimeSpan.Zero ||
            raw.CreatedUtc > now.AddMinutes(5) || raw.ExpiresUtc <= now || raw.ExpiresUtc <= raw.CreatedUtc || raw.ExpiresUtc - raw.CreatedUtc > TimeSpan.FromDays(31))
            throw new CatalogValidationException("Reference catalog channel freshness metadata is invalid or expired.");
        if (raw.Revision <= currentRevision) return null;

        var bundleUri = RequireUri(raw.BundleUri, "BundleUri");
        if (!Path.GetExtension(bundleUri.AbsolutePath).Equals(".avwtcatalog", StringComparison.OrdinalIgnoreCase))
            throw new CatalogValidationException("Reference catalog channel bundle must use the .avwtcatalog extension.");
        var bundle = await GetExactAsync(bundleUri, checked((int)ReferenceCatalogBundleVerifier.MaximumBundleBytes), cancellationToken).ConfigureAwait(false);
        var actualHash = Convert.ToHexString(SHA256.HashData(bundle));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actualHash), Encoding.ASCII.GetBytes(raw.BundleSha256!.ToUpperInvariant())))
            throw new CatalogValidationException("Reference catalog channel bundle hash verification failed.");
        return new(raw.Revision, raw.PreviousRevision, raw.CatalogVersion!, raw.MinimumAppVersion!, actualHash, bundle);
    }

    public void Dispose()
    {
        client.Dispose();
    }

    private async Task<byte[]> GetExactAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        RequireApprovedHttps(uri);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/octet-stream, application/json;q=0.9");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new CatalogValidationException("Reference catalog channel redirects are not permitted.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && (length <= 0 || length > maximumBytes))
            throw new CatalogValidationException("Reference catalog channel response size is invalid.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16_384];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes) throw new CatalogValidationException("Reference catalog channel response exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
        if (output.Length == 0) throw new CatalogValidationException("Reference catalog channel response is empty.");
        return output.ToArray();
    }

    private ChannelDocument ParseMetadata(byte[] bytes)
    {
        string json;
        try { json = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException exception) { throw new CatalogValidationException($"Reference catalog channel is not valid UTF-8: {exception.Message}"); }
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
            RejectDuplicates(document.RootElement);
            var raw = JsonSerializer.Deserialize<ChannelDocument>(json, JsonOptions)
                ?? throw new CatalogValidationException("Reference catalog channel is empty.");
            if (!string.Equals(raw.CatalogId, ReferenceCatalogBundleNames.CatalogId, StringComparison.Ordinal) || raw.SchemaVersion != 1 || raw.Revision <= 0 ||
                raw.PreviousRevision < 0 || raw.PreviousRevision >= raw.Revision || !IsVersion(raw.CatalogVersion) || !IsVersion(raw.MinimumAppVersion) ||
                string.IsNullOrWhiteSpace(raw.SigningKeyId) || raw.SigningKeyId.Length > 128 ||
                string.IsNullOrWhiteSpace(raw.BundleSha256) || raw.BundleSha256.Length != 64 || !raw.BundleSha256.All(Uri.IsHexDigit))
                throw new CatalogValidationException("Reference catalog channel metadata is invalid.");
            _ = RequireUri(raw.BundleUri, "BundleUri");
            return raw;
        }
        catch (JsonException exception) { throw new CatalogValidationException($"Reference catalog channel JSON is invalid: {exception.Message}"); }
    }

    private void VerifySignature(string keyId, byte[] metadata, byte[] signature)
    {
        if (!policy.TrustedPublicKeys.TryGetValue(keyId, out var pem) || signature.Length != 64)
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

    private Uri RequireUri(string? value, string field)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw new CatalogValidationException($"Reference catalog channel {field} is invalid.");
        RequireApprovedHttps(uri);
        return uri;
    }

    private void RequireApprovedHttps(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !policy.ApprovedHosts.Contains(uri.IdnHost))
            throw new CatalogValidationException("Reference catalog channel URI is outside the approved HTTPS origin.");
    }

    private static ReferenceCatalogChannelPolicy ValidatePolicy(ReferenceCatalogChannelPolicy value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.ApprovedHosts.Count == 0 || value.TrustedPublicKeys.Count == 0 || value.Timeout <= TimeSpan.Zero || value.Timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentException("Reference catalog channel policy is incomplete.", nameof(value));
        if (value.MetadataUri == value.SignatureUri) throw new ArgumentException("Reference catalog metadata and signature URIs must differ.", nameof(value));
        return new(value.MetadataUri, value.SignatureUri,
            value.ApprovedHosts.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            value.TrustedPublicKeys.ToFrozenDictionary(StringComparer.Ordinal), value.Timeout);
    }

    private static bool IsVersion(string? value) => Version.TryParse(value, out _) && value.Length <= 32;

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
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
        public DateTimeOffset ExpiresUtc { get; init; }
        public string? SigningKeyId { get; init; }
        public string? BundleUri { get; init; }
        public string? BundleSha256 { get; init; }
    }
}
