using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Application.Providers;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Application.Vendors;

namespace AVWorkstationToolkit.Application.Details;

public enum OfficialUriKind { Product, Download }

public interface IValidatedUserHandoffService
{
    void OpenOfficialUri(OpenOfficialUriIntent intent);
    void RevealVerifiedPayload(VendorDeliveryAuthorization authorization, VendorDownloadResult payload, string explicitDataRoot);
}

public sealed record OpenOfficialUriIntent
{
    private OpenOfficialUriIntent(OfficialUriKind kind, Uri uri, string packageId)
    {
        Kind = kind;
        Uri = uri;
        PackageId = packageId;
    }

    public OfficialUriKind Kind { get; }
    public Uri Uri { get; }
    public string PackageId { get; }

    public static OpenOfficialUriIntent? FromCatalog(PackageDefinition package, OfficialUriKind kind)
    {
        ArgumentNullException.ThrowIfNull(package);
        var value = kind == OfficialUriKind.Product
            ? package.MetadataDetails.OfficialProductUri
            : package.MetadataDetails.OfficialDownloadUri;
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length > 0 || uri.DnsSafeHost.Length == 0)
            throw new InvalidDataException("The catalogued official address is not a safe HTTPS URI.");
        return new(kind, uri, package.Id);
    }
}

public sealed record CatalogDetailField(string Label, string Value);
public sealed record CatalogDetailGroup(string Name, IReadOnlyList<CatalogDetailField> Fields);
public sealed record CatalogDetail(
    string PackageId,
    string Name,
    string Subtitle,
    IReadOnlyList<CatalogDetailGroup> Groups,
    OpenOfficialUriIntent? ProductIntent,
    OpenOfficialUriIntent? DownloadIntent,
    ExternalProviderReadState ProviderState);

public sealed class CatalogDetailService(ExternalProviderReadModelService? providerService = null)
{
    private readonly ExternalProviderReadModelService providerService = providerService ?? new ExternalProviderReadModelService();

    public CatalogDetail Create(PackageState state, PackageCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalog);
        var package = state.Package;
        var metadata = package.MetadataDetails;
        var known = state.AvailableVersion.Length == 0 ? package.KnownVersion : state.AvailableVersion;
        var coupling = Value(package.VersionCoupling);
        if (package.VersionCouplingTargetId.Length > 0) coupling += $" -> {package.VersionCouplingTargetId}";
        if (metadata.VersionCouplingNotes.Length > 0) coupling += $" ({metadata.VersionCouplingNotes})";
        var providerState = providerService.Create(state, catalog);
        var groups = new[]
        {
            Group("Identity",
                ("Name", Value(package.Name)), ("Package ID", Value(package.Id)), ("Manufacturer", Value(package.Vendor)),
                ("Product family", Value(package.ProductFamily)), ("Purpose / restriction", Value(package.Note)),
                ("Application type", Values(package.ApplicationTypes)), ("Priority", Value(package.Priority)),
                ("Roles", Values(package.Roles)), ("Workflows", Values(package.WorkflowCategories))),
            Group("Workstation state",
                ("Catalog status", StatusLabel(state.Status)), ("Installed", Value(state.Installed)),
                ("Installed version", Value(state.InstalledVersion)), ("Available / known version", Value(known)),
                ("Status detail", Value(state.StatusDetail)), ("Inventory quality", Value(state.InventoryQuality))),
            Group("Policy and compatibility",
                ("Provider", Value(package.Provider)), ("Deployment class", Value(package.DeploymentClass)),
                ("Maintenance policy", Value(package.CatalogMaintenancePolicy)), ("Version rule", Value(package.VersionRule)),
                ("Version coupling", coupling), ("Lifecycle", Value(package.Lifecycle)),
                ("Side-by-side supported", Value(metadata.SideBySideSupported)),
                ("Parent provider", Value(package.ParentProviderId))),
            Group("Access and platform",
                ("Licensing", Values(package.LicensingModels)), ("Download access", Values(package.DownloadAccess)),
                ("Download difficulty", Value(metadata.DownloadDifficulty)), ("Distribution policy", Value(package.DistributionPolicy)),
                ("Installation forms", Values(package.InstallationForms)), ("Vendor account required", Value(package.RequiresVendorAccount)),
                ("Dealer account required", Value(package.RequiresDealerAccount)), ("Training required", Value(package.RequiresTraining)),
                ("License required", Value(package.RequiresLicense)), ("Subscription required", Value(package.RequiresSubscription)),
                ("Supported OS", Values(package.SupportedOperatingSystems)), ("Architecture", Values(metadata.Architectures))),
            Group("System impact",
                ("Installs driver", Value(package.InstallsDriver)), ("Installs service", Value(package.InstallsService)),
                ("Opens listener", Value(package.OpensListener)), ("Firmware utility", Value(package.FirmwareUtility))),
            Group("Metadata verification / provenance",
                ("Validation", Values(metadata.ValidationMethods)), ("Metadata verified on", Value(metadata.MetadataVerifiedOn)),
                ("Verification state", Value(metadata.MetadataVerificationState)), ("Review triggers", Values(metadata.MetadataReviewTriggers)),
                ("Quarantine reason", Value(metadata.MetadataQuarantineReason)), ("Authoritative domain", Value(metadata.AuthoritativeDomain)),
                ("Expected publisher", Value(metadata.ExpectedPublisher)), ("Signature validation", Value(metadata.SignatureValidation)),
                ("Vendor hash availability", Value(metadata.VendorHashAvailability)), ("Download strategy", Value(metadata.DownloadStrategy)),
                ("Release evidence", Value(providerState.ReleaseDetail)), ("Delivery evidence", Value(providerState.DeliveryDetail)),
                ("Cache evidence", Value(providerState.CacheDetail)), ("Notes", Value(package.CatalogNotes))),
            Group("Official source",
                ("Official product", Value(metadata.OfficialProductUri)), ("Official download", Value(metadata.OfficialDownloadUri)))
        };
        return new(package.Id, package.Name, $"{Value(package.Vendor)} | {StatusLabel(state.Status)} | {Value(package.DeploymentClass)}",
            groups, OpenOfficialUriIntent.FromCatalog(package, OfficialUriKind.Product),
            OpenOfficialUriIntent.FromCatalog(package, OfficialUriKind.Download), providerState);
    }

    private static CatalogDetailGroup Group(string name, params (string Label, string Value)[] fields) =>
        new(name, fields.Select(field => new CatalogDetailField(field.Label, field.Value)).ToArray());

    public static string Value(object? value)
    {
        if (value is null) return "Unknown";
        if (value is bool boolean) return boolean ? "Yes" : "No";
        var text = value is Enum enumeration ? enumeration.ToString() : value.ToString() ?? string.Empty;
        return string.IsNullOrWhiteSpace(text) ? "Unknown" : text.Trim();
    }

    public static string Values<T>(IEnumerable<T> values)
    {
        var materialized = values.Select(value => Value(value)).Where(value => value != "Unknown").ToArray();
        return materialized.Length == 0 ? "Unknown" : string.Join(", ", materialized);
    }

    private static string StatusLabel(PackageStatus status) => status switch
    {
        PackageStatus.UpdateAvailable => "Update available",
        PackageStatus.ManualUpdate => "Manual update",
        PackageStatus.Inventory => "Detected",
        PackageStatus.NotDetected => "Not detected",
        PackageStatus.InventoryIncomplete => "Inventory incomplete",
        PackageStatus.InventoryUnavailable => "Inventory unavailable",
        PackageStatus.CheckUnavailable => "Check unavailable",
        PackageStatus.Awareness => "Catalog only",
        _ => status.ToString()
    };
}
