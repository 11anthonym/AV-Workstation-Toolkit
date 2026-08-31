using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;

namespace AVWorkstationToolkit.Application.Providers;

public sealed record ExternalProviderReadState(
    string PackageId,
    DiagnosticEvidenceState InventoryEvidence,
    DiagnosticEvidenceState ReleaseEvidence,
    DiagnosticEvidenceState DeliveryEvidence,
    DiagnosticEvidenceState CacheEvidence,
    bool ParentRelationshipValid,
    string ParentProviderId,
    string AvailableVersion,
    string ReleaseDetail,
    string DeliveryDetail,
    string CacheDetail);

/// <summary>
/// Projects validated catalog and inventory facts only. It performs no network,
/// credential, cache mutation, download, browser, or installer operation.
/// </summary>
public sealed class ExternalProviderReadModelService
{
    public ExternalProviderReadState Create(PackageState state, PackageCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalog);
        var package = state.Package;
        if (package.Provider != ProviderKind.External)
            return new(package.Id, DiagnosticEvidenceState.Available, DiagnosticEvidenceState.Unknown,
                DiagnosticEvidenceState.Unknown, DiagnosticEvidenceState.Unknown, false, string.Empty,
                state.AvailableVersion, "Managed package release evidence is supplied by WinGet.",
                "Managed package delivery is outside the external-provider model.", "Not applicable.");

        var inventory = state.InventoryQuality switch
        {
            InventoryQuality.Complete => DiagnosticEvidenceState.Available,
            InventoryQuality.Partial => DiagnosticEvidenceState.Partial,
            InventoryQuality.Unavailable => DiagnosticEvidenceState.Unavailable,
            InventoryQuality.PackageError => DiagnosticEvidenceState.Failed,
            _ => DiagnosticEvidenceState.Unknown
        };
        var release = package.ReleaseMode switch
        {
            ReleaseMode.InventoryOnly => DiagnosticEvidenceState.Unknown,
            ReleaseMode.VendorPage when package.KnownVersion.Length > 0 => DiagnosticEvidenceState.Warning,
            ReleaseMode.ParentCatalog => DiagnosticEvidenceState.Warning,
            _ => DiagnosticEvidenceState.Unknown
        };
        var releaseDetail = package.ReleaseMode switch
        {
            ReleaseMode.InventoryOnly => "Inventory-only provider; no online version comparison is performed.",
            ReleaseMode.VendorPage => package.KnownVersion.Length > 0
                ? $"Validated catalog baseline {package.KnownVersion} is available; live vendor metadata was not requested by the compiled application."
                : "Vendor metadata was not requested and no validated catalog baseline is available.",
            ReleaseMode.ParentCatalog => "Parent-provider metadata was not requested; any validated catalog baseline remains read-only.",
            _ => "Release evidence is unknown."
        };
        var details = package.MetadataDetails;
        var parentValid = package.ParentProviderId.Length > 0 && TryGet(catalog, package.ParentProviderId, out var parent) &&
            parent.DeliveryMode == DeliveryMode.AuthenticatedSftp && package.DeliveryMode == DeliveryMode.ParentProvider &&
            parent.AllowedProductIds.Contains(package.DeliveryProductId, StringComparer.Ordinal);
        var delivery = details.MetadataQuarantined ? DiagnosticEvidenceState.Warning : package.DeliveryMode switch
        {
            DeliveryMode.VendorPage when SafeHttps(details.DeliveryUri) => DiagnosticEvidenceState.Available,
            DeliveryMode.Awareness when SafeHttps(details.OfficialProductUri) => DiagnosticEvidenceState.Available,
            DeliveryMode.ParentProvider when parentValid => DiagnosticEvidenceState.Unknown,
            DeliveryMode.DirectDownload or DeliveryMode.AuthenticatedSftp or DeliveryMode.Bundled => DiagnosticEvidenceState.Unknown,
            DeliveryMode.InventoryOnly => DiagnosticEvidenceState.Unknown,
            _ => DiagnosticEvidenceState.Unavailable
        };
        var deliveryDetail = details.MetadataQuarantined
            ? $"Metadata is quarantined: {details.MetadataQuarantineReason}"
            : package.DeliveryMode switch
            {
                DeliveryMode.VendorPage => delivery == DiagnosticEvidenceState.Available
                    ? "Validated official vendor-page handoff metadata is available."
                    : "Validated vendor-page handoff metadata is unavailable.",
                DeliveryMode.Awareness => delivery == DiagnosticEvidenceState.Available
                    ? "Validated official product-page metadata is available; the record remains awareness-only."
                    : "No validated official product-page metadata is available.",
                DeliveryMode.ParentProvider => parentValid
                    ? "Parent-provider relationship is valid; authenticated availability was not checked."
                    : "Parent-provider relationship is unavailable.",
                DeliveryMode.AuthenticatedSftp => "Authenticated provider configuration exists; no host, credential, or catalog operation was attempted.",
                DeliveryMode.DirectDownload => "Direct-download policy exists; no page, payload, or cache operation was attempted.",
                DeliveryMode.Bundled => "Bundled delivery policy exists; payload/cache presence was not inspected.",
                DeliveryMode.InventoryOnly => "Inventory-only provider has no delivery action.",
                _ => "Delivery evidence is unknown."
            };
        const DiagnosticEvidenceState cacheState = DiagnosticEvidenceState.Unknown;
        var cacheDetail = package.DeliveryMode is DeliveryMode.DirectDownload or DeliveryMode.ParentProvider or DeliveryMode.Bundled
            ? "Cache presence and verification were not inspected by this read-only migration phase."
            : "Not applicable.";
        return new(package.Id, inventory, release, delivery, cacheState, parentValid, package.ParentProviderId,
            state.AvailableVersion.Length > 0 ? state.AvailableVersion : package.KnownVersion,
            releaseDetail, deliveryDetail, cacheDetail);
    }

    private static bool TryGet(PackageCatalog catalog, string id, out PackageDefinition package)
    {
        package = catalog.Items.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))!;
        return package is not null;
    }

    private static bool SafeHttps(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.DnsSafeHost.Length > 0;
}
