using System.Net;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
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
        var raw = new ReferenceCatalogChannelVerifier(policy.TrustedPublicKeys).Verify(metadataBytes, signatureBytes, DateTimeOffset.UtcNow);
        if (raw.Revision <= currentRevision) return null;

        var bundleUri = raw.BundleUri;
        RequireApprovedHttps(bundleUri);
        var bundle = await GetExactAsync(bundleUri, checked((int)ReferenceCatalogBundleVerifier.MaximumBundleBytes), cancellationToken).ConfigureAwait(false);
        var actualHash = Convert.ToHexString(SHA256.HashData(bundle));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actualHash), Encoding.ASCII.GetBytes(raw.BundleSha256!.ToUpperInvariant())))
            throw new CatalogValidationException("Reference catalog channel bundle hash verification failed.");
        return new(raw.Revision, raw.PreviousRevision, raw.CatalogVersion, raw.MinimumAppVersion, actualHash, bundle);
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

}
