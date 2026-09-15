using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Application.Providers;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Application.Vendors;
using AVWorkstationToolkit.Application.Compatibility;
using System.Text.RegularExpressions;

namespace AVWorkstationToolkit.Application.Details;

public enum OfficialUriKind { Product, Download, Evidence }

public interface IValidatedUserHandoffService
{
    void OpenOfficialUri(OpenOfficialUriIntent intent);
    void RevealVerifiedPayload(VendorDeliveryAuthorization authorization, VendorDownloadResult payload, string explicitDataRoot);
    void OpenLogs(string explicitDataRoot);
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
        if (kind is not (OfficialUriKind.Product or OfficialUriKind.Download))
            throw new ArgumentOutOfRangeException(nameof(kind), "Package catalog links support only product and download intents.");
        var value = kind == OfficialUriKind.Product
            ? package.MetadataDetails.OfficialProductUri
            : package.MetadataDetails.OfficialDownloadUri;
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length > 0 || uri.DnsSafeHost.Length == 0)
            throw new InvalidDataException("The catalogued official address is not a safe HTTPS URI.");
        return new(kind, uri, package.Id);
    }

    public static OpenOfficialUriIntent FromDeliveryCatalog(PackageDefinition package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Provider != ProviderKind.External || package.Authority is not (CatalogAuthority.OperationalExternal or CatalogAuthority.AwarenessOnly))
            throw new InvalidOperationException("The package does not grant an official delivery handoff.");
        var value = package.DeliveryPolicy?.Uri;
        if (string.IsNullOrWhiteSpace(value)) value = package.MetadataDetails.OfficialDownloadUri;
        if (string.IsNullOrWhiteSpace(value)) value = package.MetadataDetails.OfficialProductUri;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length > 0 || uri.DnsSafeHost.Length == 0)
            throw new InvalidDataException("The catalogued vendor delivery address is not a safe HTTPS URI.");
        return new(OfficialUriKind.Download, uri, package.Id);
    }

    public static OpenOfficialUriIntent FromCompatibilityProduct(CompatibilityProductSummary product)
    {
        ArgumentNullException.ThrowIfNull(product);
        return CreateCompatibility(OfficialUriKind.Product, product.OfficialSourceUri, product.Id.Value);
    }

    public static OpenOfficialUriIntent FromCompatibilityRelease(
        SoftwareProductId productId,
        CompatibilityReleaseFamilySummary family)
    {
        ArgumentNullException.ThrowIfNull(family);
        return CreateCompatibility(OfficialUriKind.Evidence, family.EvidenceUri, productId.Value);
    }

    public static OpenOfficialUriIntent FromCompatibilityRelation(RelevantSoftwareSummary relation)
    {
        ArgumentNullException.ThrowIfNull(relation);
        return CreateCompatibility(OfficialUriKind.Evidence, relation.EvidenceUri, relation.ProductId.Value);
    }

    public static OpenOfficialUriIntent FromCompatibilityRelation(
        SoftwareProductId productId,
        ApplicableDevicePurposeSummary relation)
    {
        ArgumentNullException.ThrowIfNull(relation);
        return CreateCompatibility(OfficialUriKind.Evidence, relation.EvidenceUri, productId.Value);
    }

    private static OpenOfficialUriIntent CreateCompatibility(OfficialUriKind kind, Uri uri, string productId)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (kind == OfficialUriKind.Download || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.DnsSafeHost.Length == 0)
            throw new InvalidDataException("Compatibility links must be validated non-download HTTPS product or evidence addresses.");
        return new(kind, uri, productId);
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

    public CatalogDetail Create(PackageState state, PackageCatalog catalog, ExternalReleaseEvidence? releaseEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalog);
        var package = state.Package;
        var metadata = package.MetadataDetails;
        var known = state.AvailableVersion.Length == 0 ? package.KnownVersion : state.AvailableVersion;
        var knownLabel = state.Status is PackageStatus.UpdateAvailable or PackageStatus.ManualUpdate
            ? "Available update"
            : "Catalog version";
        var coupling = Value(package.VersionCoupling);
        if (package.VersionCouplingTargetId.Length > 0)
        {
            var target = catalog.Items.FirstOrDefault(item => item.Id.Equals(package.VersionCouplingTargetId, StringComparison.OrdinalIgnoreCase));
            coupling += $" with {target?.Name ?? "a related product"}";
        }
        if (metadata.VersionCouplingNotes.Length > 0) coupling += $" ({metadata.VersionCouplingNotes})";
        var providerState = providerService.Create(state, catalog, releaseEvidence);
        var parentName = package.ParentProviderId.Length == 0
            ? "Not applicable"
            : catalog.Items.FirstOrDefault(item => item.Id.Equals(package.ParentProviderId, StringComparison.OrdinalIgnoreCase))?.Name ?? "Approved vendor source";
        var groups = new[]
        {
            Group("About this software",
                ("Name", Value(package.Name)), ("Manufacturer", Value(package.Vendor)),
                ("Product family", Value(package.ProductFamily)), ("What it's for", Value(package.Note)),
                ("Software type", Values(package.ApplicationTypes)), ("Priority", Value(package.Priority)),
                ("Roles", Values(package.Roles)), ("Workflows", Values(package.WorkflowCategories))),
            Group("On this PC",
                ("Status", PackageStatePresentation.Status(state)), ("Installed", PackageStatePresentation.Installation(state)),
                ("Installed version", PackageStatePresentation.InstalledVersion(state)), (knownLabel, Value(known)),
                ("What this means", PackageStatePresentation.Detail(state)), ("Installation check", Value(state.InventoryQuality))),
            Group("Installation and compatibility",
                ("Installation source", Value(package.Provider)), ("How AVWT handles it", Value(package.DeploymentClass)),
                ("Update policy", Value(package.CatalogMaintenancePolicy)), ("Version selection", Value(package.VersionRule)),
                ("Version requirements", coupling), ("Lifecycle", Value(package.Lifecycle)),
                ("Multiple versions supported", Value(metadata.SideBySideSupported)),
                ("Vendor download source", parentName)),
            Group("Access and system requirements",
                ("Licensing", Values(package.LicensingModels)), ("Download access", Values(package.DownloadAccess)),
                ("Download difficulty", Value(metadata.DownloadDifficulty)), ("Distribution policy", Value(package.DistributionPolicy)),
                ("Installation forms", Values(package.InstallationForms)), ("Vendor account required", Value(package.RequiresVendorAccount)),
                ("Dealer account required", Value(package.RequiresDealerAccount)), ("Training required", Value(package.RequiresTraining)),
                ("License required", Value(package.RequiresLicense)), ("Subscription required", Value(package.RequiresSubscription)),
                ("Supported OS", Values(package.SupportedOperatingSystems)), ("Architecture", Values(metadata.Architectures))),
            Group("System impact",
                ("Installs driver", Value(package.InstallsDriver)), ("Installs service", Value(package.InstallsService)),
                ("Accepts network connections", Value(package.OpensListener)), ("Firmware tool", Value(package.FirmwareUtility))),
            Group("Source and verification details",
                ("Catalog ID", Value(package.Id)), ("How it was checked", Values(metadata.ValidationMethods)), ("Last checked", Value(metadata.MetadataVerifiedOn)),
                ("Review status", Value(metadata.MetadataVerificationState)), ("Review when", Values(metadata.MetadataReviewTriggers)),
                ("Restricted because", Value(metadata.MetadataQuarantineReason)), ("Official website", Value(metadata.AuthoritativeDomain)),
                ("Expected publisher", Value(metadata.ExpectedPublisher)), ("Signature validation", Value(metadata.SignatureValidation)),
                ("Vendor hash availability", Value(metadata.VendorHashAvailability)), ("Download strategy", Value(metadata.DownloadStrategy)),
                ("Release information", Value(providerState.ReleaseDetail)), ("Download information", Value(providerState.DeliveryDetail)),
                ("Saved download", Value(providerState.CacheDetail)), ("Notes", Value(package.CatalogNotes))),
            Group("Official links",
                ("Official product", Value(metadata.OfficialProductUri)), ("Official download", Value(metadata.OfficialDownloadUri)))
        };
        return new(package.Id, package.Name, $"{Value(package.Vendor)} · {PackageStatePresentation.Status(state)}",
            groups, OpenOfficialUriIntent.FromCatalog(package, OfficialUriKind.Product),
            OpenOfficialUriIntent.FromCatalog(package, OfficialUriKind.Download), providerState);
    }

    private static CatalogDetailGroup Group(string name, params (string Label, string Value)[] fields) =>
        new(name, fields.Select(field => new CatalogDetailField(field.Label, field.Value)).ToArray());

    public static string Value(object? value)
    {
        if (value is null) return "Unknown";
        if (value is bool boolean) return boolean ? "Yes" : "No";
        var text = value is Enum enumeration ? EnumLabel(enumeration) : value.ToString() ?? string.Empty;
        return string.IsNullOrWhiteSpace(text) ? "Unknown" : text.Trim();
    }

    public static string Values<T>(IEnumerable<T> values)
    {
        var materialized = values.Select(value => Value(value)).Where(value => value != "Unknown").ToArray();
        return materialized.Length == 0 ? "Unknown" : string.Join(", ", materialized);
    }

    private static string EnumLabel(Enum value)
    {
        if (value is PackagePriority.P1) return "Priority 1";
        if (value is PackagePriority.P2) return "Priority 2";
        if (value is PackagePriority.Dev) return "Developer";
        if (value is ProviderKind.External) return "Vendor or inventory source";
        if (value is DeploymentClass.Managed) return "Install or update in AVWT";
        if (value is DeploymentClass.ManualHandoff) return "Use the vendor's installer";
        if (value is DeploymentClass.ParentProvider) return "Download through the approved vendor service";
        if (value is DeploymentClass.InventoryOnly) return "Installation check only";
        if (value is DeploymentClass.AwarenessOnly) return "Information only";
        if (value is DeploymentClass.WebOnly) return "Vendor website";
        if (value is DeploymentClass.ServerOnly) return "Server-based";
        if (value is DeploymentClass.Embedded) return "Built into the device";
        if (value is InventoryQuality.PackageError) return "Couldn't check this app";
        if (value is InventoryQuality.NotApplicable) return "Not checked";
        if (value is MetadataVerificationState.VerificationRequired) return "Needs review";
        if (value is MetadataVerificationState.ReviewSoon) return "Review soon";
        var text = Regex.Replace(value.ToString(), "([a-z0-9])([A-Z])", "$1 $2", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return text.Replace("Win Get", "WinGet", StringComparison.Ordinal)
            .Replace("AVo IP", "AV-over-IP", StringComparison.Ordinal)
            .Replace("DSPAudio", "DSP audio", StringComparison.Ordinal)
            .Replace("DSP Engineering", "DSP engineering", StringComparison.Ordinal)
            .Replace("Camera PTZ", "PTZ camera", StringComparison.Ordinal)
            .Replace("EDIDHDCP", "EDID / HDCP", StringComparison.Ordinal)
            .Replace("Dv LED", "dvLED", StringComparison.Ordinal)
            .Replace("Msi", "MSI", StringComparison.Ordinal)
            .Replace("Exe", "EXE", StringComparison.Ordinal)
            .Replace("Usb", "USB", StringComparison.Ordinal)
            .Replace("Mac OS", "macOS", StringComparison.Ordinal)
            .Replace("IOS", "iOS", StringComparison.Ordinal)
            .Replace("Not Applicable", "Not applicable", StringComparison.Ordinal);
    }
}
