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
    public ExternalProviderReadState Create(PackageState state, PackageCatalog catalog, ExternalReleaseEvidence? releaseEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalog);
        var package = state.Package;
        if (package.Provider != ProviderKind.External)
            return new(package.Id, DiagnosticEvidenceState.Available, DiagnosticEvidenceState.Unknown,
                DiagnosticEvidenceState.Unknown, DiagnosticEvidenceState.Unknown, false, string.Empty,
                state.AvailableVersion, "WinGet provides version information for this app.",
                "AVWT installs this supported app through WinGet.", "Not applicable.");

        var inventory = state.InventoryQuality switch
        {
            InventoryQuality.Complete => DiagnosticEvidenceState.Available,
            InventoryQuality.Partial => DiagnosticEvidenceState.Partial,
            InventoryQuality.Unavailable => DiagnosticEvidenceState.Unavailable,
            InventoryQuality.PackageError => DiagnosticEvidenceState.Failed,
            _ => DiagnosticEvidenceState.Unknown
        };
        var release = releaseEvidence switch
        {
            { OnlineAvailable: true } => DiagnosticEvidenceState.Available,
            { OnlineChecked: true } => DiagnosticEvidenceState.Unavailable,
            _ => package.ReleaseMode switch
            {
                ReleaseMode.InventoryOnly => DiagnosticEvidenceState.Unknown,
                ReleaseMode.VendorPage when package.KnownVersion.Length > 0 => DiagnosticEvidenceState.Warning,
                ReleaseMode.ParentCatalog => DiagnosticEvidenceState.Warning,
                _ => DiagnosticEvidenceState.Unknown
            }
        };
        var releaseDetail = releaseEvidence?.Detail ?? (package.ReleaseMode switch
        {
            ReleaseMode.InventoryOnly => "AVWT checks whether this app is installed but doesn't check its latest version online.",
            ReleaseMode.VendorPage => package.KnownVersion.Length > 0
                ? $"The software catalog lists version {package.KnownVersion}. AVWT hasn't checked the vendor for a newer version."
                : "AVWT hasn't checked the vendor for a newer version, and the software catalog doesn't list a version.",
            ReleaseMode.ParentCatalog => "The vendor's product list wasn't checked. Any version in the software catalog is for reference only.",
            _ => "Version information isn't available."
        });
        var details = package.MetadataDetails;
        var parentValid = package.ParentProviderId.Length > 0 && TryGet(catalog, package.ParentProviderId, out var parent) &&
            parent.DeliveryMode == DeliveryMode.AuthenticatedSftp && package.DeliveryMode == DeliveryMode.ParentProvider &&
            parent.AllowedProductIds.Contains(package.DeliveryProductId, StringComparer.Ordinal);
        var delivery = details.MetadataQuarantined ? DiagnosticEvidenceState.Warning : package.DeliveryMode switch
        {
            DeliveryMode.VendorPage when SafeHttps(details.DeliveryUri) => DiagnosticEvidenceState.Available,
            DeliveryMode.Awareness when SafeHttps(details.OfficialProductUri) => DiagnosticEvidenceState.Available,
            DeliveryMode.ParentProvider when parentValid && releaseEvidence?.Products.Count > 0 => DiagnosticEvidenceState.Available,
            DeliveryMode.ParentProvider when parentValid => DiagnosticEvidenceState.Unknown,
            DeliveryMode.AuthenticatedSftp when releaseEvidence?.Products.Count > 0 => DiagnosticEvidenceState.Available,
            DeliveryMode.DirectDownload when !string.IsNullOrWhiteSpace(releaseEvidence?.DownloadUri) => DiagnosticEvidenceState.Available,
            DeliveryMode.DirectDownload or DeliveryMode.AuthenticatedSftp or DeliveryMode.Bundled => DiagnosticEvidenceState.Unknown,
            DeliveryMode.InventoryOnly => DiagnosticEvidenceState.Unknown,
            _ => DiagnosticEvidenceState.Unavailable
        };
        var deliveryDetail = details.MetadataQuarantined
            ? $"Some download information is restricted because it didn't pass verification: {details.MetadataQuarantineReason}"
            : package.DeliveryMode switch
            {
                DeliveryMode.VendorPage => delivery == DiagnosticEvidenceState.Available
                    ? "AVWT can open the approved official vendor page."
                    : "An approved official vendor page isn't available.",
                DeliveryMode.Awareness => delivery == DiagnosticEvidenceState.Available
                    ? "AVWT can open the approved official product page. This item is for reference only."
                    : "An approved official product page isn't available.",
                DeliveryMode.ParentProvider => parentValid
                    ? releaseEvidence?.Products.Count > 0
                        ? "The vendor lists an approved package for this app. AVWT hasn't accessed your sign-in."
                        : releaseEvidence is null
                            ? "The approved vendor source is configured, but package availability was not checked."
                            : "The approved vendor source is configured, but its current product list isn't available."
                    : "The approved vendor source for this app isn't available.",
                DeliveryMode.AuthenticatedSftp => releaseEvidence?.Products.Count > 0
                    ? $"The vendor lists {releaseEvidence.Products.Count} approved package{(releaseEvidence.Products.Count == 1 ? string.Empty : "s")}. AVWT hasn't accessed your sign-in."
                    : "The approved vendor source is configured. AVWT hasn't accessed your sign-in, and the current product list isn't available.",
                DeliveryMode.DirectDownload => !string.IsNullOrWhiteSpace(releaseEvidence?.DownloadUri)
                    ? "An approved vendor download is available. AVWT will verify the downloaded file before showing it."
                    : "A current approved download isn't available. You can still open the official vendor page.",
                DeliveryMode.Bundled => "This app uses a packaged download. AVWT hasn't checked whether a saved copy is available.",
                DeliveryMode.InventoryOnly => "AVWT checks this app's installation status but doesn't download it.",
                _ => "Download information isn't available."
            };
        const DiagnosticEvidenceState cacheState = DiagnosticEvidenceState.Unknown;
        var cacheDetail = package.DeliveryMode is DeliveryMode.DirectDownload or DeliveryMode.ParentProvider or DeliveryMode.Bundled
            ? "AVWT hasn't checked for a previously downloaded and verified copy."
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
