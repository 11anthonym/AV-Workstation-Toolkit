using System.Collections.Frozen;
using AVWorkstationToolkit.Application.Compatibility;

namespace AVWorkstationToolkit.Infrastructure.Windows.Catalog;

public sealed record ProductionReferenceCatalogServices(
    ReferenceCatalogBundleVerifier BundleVerifier,
    IReferenceCatalogChannelClient? ChannelClient);

/// <summary>
/// Exact production distribution origin and public trust anchors for descriptive reference catalogs.
/// No URL, host, or key is accepted from a caller or environment variable.
/// </summary>
public static class ProductionReferenceCatalogConfiguration
{
    public const string ApprovedHost = "11anthonym.github.io";
    public const string PrimarySigningKeyId = "avwt-catalog-2026-a";
    public static Uri FeedRoot { get; } = new("https://11anthonym.github.io/AVWT-Catalog/");
    public static Uri MetadataUri { get; } = new("https://11anthonym.github.io/AVWT-Catalog/stable/catalog-channel.json");
    public static Uri SignatureUri { get; } = new("https://11anthonym.github.io/AVWT-Catalog/stable/catalog-channel.sig");
    public static int TrustedPublicKeyCount => ProductionReferenceCatalogTrustAnchors.All.Count;

    public static ProductionReferenceCatalogServices Create(string applicationVersion)
    {
        if (string.IsNullOrWhiteSpace(applicationVersion)) throw new ArgumentException("Application version is required.", nameof(applicationVersion));

        var keys = ProductionReferenceCatalogTrustAnchors.All;
        var verifier = new ReferenceCatalogBundleVerifier(new(applicationVersion, keys));
        if (keys.Count == 0) return new(verifier, null);
        if (!keys.ContainsKey(PrimarySigningKeyId))
            throw new InvalidOperationException($"The primary production reference-catalog key '{PrimarySigningKeyId}' is not configured.");

        var policy = new ReferenceCatalogChannelPolicy(
            MetadataUri,
            SignatureUri,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ApprovedHost },
            keys,
            TimeSpan.FromSeconds(30));
        return new(verifier, new ReferenceCatalogChannelClient(policy));
    }
}

internal static class ProductionReferenceCatalogTrustAnchors
{
    // Add only owner-supplied ECDSA P-256 PUBLIC keys here after out-of-repository key generation.
    // The empty set deliberately keeps online checks and signed imports fail-closed until that handoff.
    internal static IReadOnlyDictionary<string, string> All { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal);
}
