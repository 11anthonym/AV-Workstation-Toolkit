using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Application.Inventory;

namespace AVWorkstationToolkit.Application.Providers;

public sealed record VendorCatalogProduct(
    string ProductId,
    string Name,
    string Version,
    string RemotePath,
    string FileName,
    long SizeBytes,
    bool RebootRequired);

public sealed record ExternalReleaseEvidence(
    string Id,
    string AvailableVersion,
    string ObservedVersion,
    bool OnlineChecked,
    bool OnlineAvailable,
    string ReleaseUri,
    string DownloadUri,
    string Detail,
    IReadOnlyList<VendorCatalogProduct> Products);

public sealed record ExternalReleaseInventoryResult(
    IReadOnlyList<ExternalReleaseEvidence> Releases,
    ProviderQuality Quality,
    ProviderFailureKind Failure,
    string Detail);

public interface IExternalReleaseInventory
{
    Task<ExternalReleaseInventoryResult> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Fail-closed baseline used by compositions that do not need online external
/// release evidence, such as the managed-only worker revalidation path.
/// </summary>
public sealed class CatalogBaselineExternalReleaseInventory(PackageCatalog catalog) : IExternalReleaseInventory
{
    public Task<ExternalReleaseInventoryResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var releases = catalog.Items.Where(item => item.Provider == ProviderKind.External).Select(item => new ExternalReleaseEvidence(
            item.Id,
            item.KnownVersion,
            string.Empty,
            false,
            false,
            item.MetadataDetails.ReleaseUri,
            string.Empty,
            item.KnownVersion.Length > 0 ? "Using the validated catalog release baseline." : "No validated catalog release version is available.",
            [])).ToArray();
        return Task.FromResult(new ExternalReleaseInventoryResult(releases, ProviderQuality.Complete, ProviderFailureKind.None,
            "Online external release evidence was not requested by this composition."));
    }
}
