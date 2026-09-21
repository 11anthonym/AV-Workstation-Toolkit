using System.Collections.Frozen;
using AVWorkstationToolkit.Application.Catalog;

namespace AVWorkstationToolkit.Infrastructure.Windows.Catalog;

public sealed record ManagedCatalogRuntimeServices(
    ManagedCatalogVerifier Verifier,
    IManagedCatalogChannelClient? ChannelClient);

/// <summary>
/// Fixed production origin and separately owned public trust anchors for executable managed policy.
/// The empty anchor set is an intentional release gate until the owner provisions the production key.
/// </summary>
public static class ProductionManagedCatalogConfiguration
{
    public const string ApprovedHost = "11anthonym.github.io";
    public const string PrimarySigningKeyId = "avwt-managed-2026-a";
    public static Uri MetadataUri { get; } = new("https://11anthonym.github.io/AVWT-Catalog/managed/stable/managed-catalog-channel.json");
    public static Uri SignatureUri { get; } = new("https://11anthonym.github.io/AVWT-Catalog/managed/stable/managed-catalog-channel.sig");
    public static int TrustedPublicKeyCount => ProductionManagedCatalogTrustAnchors.All.Count;

    public static ManagedCatalogRuntimeServices Create(string applicationVersion)
    {
        if (string.IsNullOrWhiteSpace(applicationVersion))
            throw new ArgumentException("Application version is required.", nameof(applicationVersion));
        var keys = ProductionManagedCatalogTrustAnchors.All;
        if (!keys.ContainsKey(PrimarySigningKeyId))
            throw new InvalidOperationException(
                $"The production managed-catalog trust anchor '{PrimarySigningKeyId}' has not been owner-provisioned. Production startup is blocked.");
        var verifier = new ManagedCatalogVerifier(new(applicationVersion, keys));
        var policy = new ManagedCatalogChannelPolicy(
            MetadataUri, SignatureUri,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ApprovedHost },
            keys, applicationVersion, TimeSpan.FromSeconds(30));
        return new(verifier, new ManagedCatalogChannelClient(policy));
    }
}

internal static class ProductionManagedCatalogTrustAnchors
{
    // Owner-supplied ECDSA P-256 PUBLIC keys only. A development key must never be added here.
    internal static IReadOnlyDictionary<string, string> All { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal);
}
