using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using AVWorkstationToolkit.Application.Catalog;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Infrastructure.Windows.Catalog;

public sealed record ManagedCatalogChannelPolicy(
    Uri MetadataUri,
    Uri SignatureUri,
    IReadOnlySet<string> ApprovedHosts,
    IReadOnlyDictionary<string, string> TrustedPublicKeys,
    string ApplicationVersion,
    TimeSpan Timeout);

public sealed class ManagedCatalogChannelClient : IManagedCatalogChannelClient, IDisposable
{
    private readonly ManagedCatalogChannelPolicy policy;
    private readonly ManagedCatalogVerifier verifier;
    private readonly FixedOriginCatalogTransport transport;
    private readonly TimeProvider timeProvider;

    public ManagedCatalogChannelClient(ManagedCatalogChannelPolicy policy)
        : this(policy, null, true, TimeProvider.System)
    {
    }

    internal ManagedCatalogChannelClient(
        ManagedCatalogChannelPolicy policy,
        HttpMessageHandler? handler,
        bool ownsHandler = true,
        TimeProvider? timeProvider = null)
    {
        this.policy = ValidatePolicy(policy);
        verifier = new ManagedCatalogVerifier(new(policy.ApplicationVersion, policy.TrustedPublicKeys));
        transport = handler is null
            ? new FixedOriginCatalogTransport(this.policy.ApprovedHosts, this.policy.Timeout)
            : new FixedOriginCatalogTransport(this.policy.ApprovedHosts, this.policy.Timeout, handler, ownsHandler);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ManagedCatalogChannelPackage?> GetLatestAsync(long currentRevision, CancellationToken cancellationToken = default)
    {
        if (currentRevision < 0) throw new ArgumentOutOfRangeException(nameof(currentRevision));
        var metadata = await transport.GetExactAsync(policy.MetadataUri, ManagedCatalogVerifier.MaximumChannelBytes, cancellationToken).ConfigureAwait(false);
        var signature = await transport.GetExactAsync(policy.SignatureUri, ManagedCatalogVerifier.MaximumSignatureBytes, cancellationToken).ConfigureAwait(false);
        var channel = verifier.VerifyChannel(metadata, signature, timeProvider.GetUtcNow());
        if (channel.Revision <= currentRevision) return null;
        transport.RequireApprovedHttps(channel.BundleUri);
        var bundle = await transport.GetExactAsync(channel.BundleUri, checked((int)ManagedCatalogVerifier.MaximumBundleBytes), cancellationToken).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(bundle));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(channel.BundleSha256)))
            throw new CatalogValidationException("Managed catalog channel bundle hash verification failed.");
        return new(channel.Revision, channel.PreviousRevision, channel.CatalogVersion, channel.MinimumAppVersion,
            channel.CreatedUtc, channel.SigningKeyId, hash, bundle);
    }

    public void Dispose() => transport.Dispose();

    private static ManagedCatalogChannelPolicy ValidatePolicy(ManagedCatalogChannelPolicy value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.ApprovedHosts.Count == 0 || value.TrustedPublicKeys.Count == 0 ||
            value.Timeout <= TimeSpan.Zero || value.Timeout > TimeSpan.FromMinutes(2) ||
            !Version.TryParse(value.ApplicationVersion, out _))
            throw new ArgumentException("Managed catalog channel policy is incomplete.", nameof(value));
        if (value.MetadataUri == value.SignatureUri)
            throw new ArgumentException("Managed catalog metadata and signature URIs must differ.", nameof(value));
        return new(value.MetadataUri, value.SignatureUri,
            value.ApprovedHosts.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            value.TrustedPublicKeys.ToFrozenDictionary(StringComparer.Ordinal), value.ApplicationVersion, value.Timeout);
    }
}
