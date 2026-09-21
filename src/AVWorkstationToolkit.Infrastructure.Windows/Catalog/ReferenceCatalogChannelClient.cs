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
    private readonly FixedOriginCatalogTransport transport;
    private readonly TimeProvider timeProvider;

    public ReferenceCatalogChannelClient(ReferenceCatalogChannelPolicy policy)
        : this(policy, null, ownsHandler: true, TimeProvider.System)
    {
    }

    internal ReferenceCatalogChannelClient(
        ReferenceCatalogChannelPolicy policy,
        HttpMessageHandler? handler,
        bool ownsHandler = true,
        TimeProvider? timeProvider = null)
    {
        this.policy = ValidatePolicy(policy);
        transport = handler is null
            ? new FixedOriginCatalogTransport(this.policy.ApprovedHosts, this.policy.Timeout)
            : new FixedOriginCatalogTransport(this.policy.ApprovedHosts, this.policy.Timeout, handler, ownsHandler);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ReferenceCatalogChannelPackage?> GetLatestAsync(long currentRevision, CancellationToken cancellationToken = default)
    {
        if (currentRevision < 0) throw new ArgumentOutOfRangeException(nameof(currentRevision));
        var metadataBytes = await transport.GetExactAsync(policy.MetadataUri, MaximumMetadataBytes, cancellationToken).ConfigureAwait(false);
        var signatureBytes = await transport.GetExactAsync(policy.SignatureUri, MaximumSignatureBytes, cancellationToken).ConfigureAwait(false);
        var raw = new ReferenceCatalogChannelVerifier(policy.TrustedPublicKeys).Verify(metadataBytes, signatureBytes, timeProvider.GetUtcNow());
        if (raw.Revision <= currentRevision) return null;

        var bundleUri = raw.BundleUri;
        transport.RequireApprovedHttps(bundleUri);
        var bundle = await transport.GetExactAsync(bundleUri, checked((int)ReferenceCatalogBundleVerifier.MaximumBundleBytes), cancellationToken).ConfigureAwait(false);
        var actualHash = Convert.ToHexString(SHA256.HashData(bundle));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actualHash), Encoding.ASCII.GetBytes(raw.BundleSha256!.ToUpperInvariant())))
            throw new CatalogValidationException("Reference catalog channel bundle hash verification failed.");
        return new(raw.Revision, raw.PreviousRevision, raw.CatalogVersion, raw.MinimumAppVersion, actualHash, bundle);
    }

    public void Dispose()
    {
        transport.Dispose();
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
