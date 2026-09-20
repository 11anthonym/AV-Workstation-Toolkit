namespace AVWorkstationToolkit.Domain.Catalog;

public sealed record PackageDefinition(
    string Id,
    string Name,
    string Vendor,
    string ProductFamily,
    string Note,
    ProviderKind Provider,
    CatalogAuthority Authority,
    PackageProfile Profile,
    PackagePriority Priority,
    PackageRisk Risk,
    DeploymentPolicy Deployment,
    MaintenancePolicy Maintenance,
    DeploymentClass DeploymentClass,
    CatalogMaintenancePolicy CatalogMaintenancePolicy,
    VersionRule VersionRule,
    VersionCouplingMode VersionCoupling,
    string VersionCouplingTargetId,
    Lifecycle Lifecycle,
    IReadOnlyList<ApplicationType> ApplicationTypes,
    IReadOnlyList<PackageRole> Roles,
    IReadOnlyList<string> WorkflowCategories,
    IReadOnlyList<LicensingModel> LicensingModels,
    IReadOnlyList<string> DownloadAccess,
    DistributionPolicy DistributionPolicy,
    IReadOnlyList<InstallationForm> InstallationForms,
    IReadOnlyList<SupportedOperatingSystem> SupportedOperatingSystems,
    DeliveryMode DeliveryMode,
    ReleaseMode ReleaseMode,
    DetectionMode DetectionMode,
    DetectionVersionPolicy DetectionVersionPolicy,
    string ReleaseChannel,
    string KnownVersion,
    string ParentProviderId,
    string DeliveryProductId,
    IReadOnlyList<string> AllowedProductIds,
    bool? RequiresVendorAccount,
    bool? RequiresDealerAccount,
    bool? RequiresTraining,
    bool? RequiresLicense,
    bool? RequiresSubscription,
    bool? InstallsDriver,
    bool? InstallsService,
    bool? OpensListener,
    bool? FirmwareUtility,
    string CatalogNotes,
    IReadOnlyList<string> CatalogTags,
    string DetectionDisplayNamePattern = "",
    string DetectionVersionPattern = "",
    CatalogMetadataDetails? Details = null,
    CatalogDeliveryPolicy? DeliveryPolicy = null,
    InstallerExecutionMode InstallerMode = InstallerExecutionMode.Silent)
{
    public CatalogMetadataDetails MetadataDetails => Details ?? CatalogMetadataDetails.Unknown;

    public bool HasManagedExecutionAuthority =>
        Authority == CatalogAuthority.ManagedWinGet &&
        Provider == ProviderKind.WinGet &&
        Deployment == DeploymentPolicy.Allowlisted;
}

public sealed record CatalogDeliveryPolicy(
    string Uri,
    string DownloadUriPattern,
    IReadOnlyList<string> AllowedHosts,
    string PublisherPattern,
    long MaximumBytes,
    string Host,
    int Port,
    string CatalogUri,
    string RemoteRoot,
    IReadOnlyList<string> AllowedProductIds,
    string ProductId,
    string RelativePath,
    string Sha256,
    string PublisherSubject);

public sealed record CatalogMetadataDetails(
    string VersionCouplingNotes,
    string DownloadDifficulty,
    IReadOnlyList<string> Architectures,
    string SideBySideSupported,
    string OfficialDownloadUri,
    string OfficialProductUri,
    IReadOnlyList<string> ValidationMethods,
    string MetadataVerifiedOn,
    MetadataVerificationState MetadataVerificationState,
    IReadOnlyList<string> MetadataReviewTriggers,
    bool MetadataQuarantined,
    string MetadataQuarantineReason,
    string AuthoritativeDomain,
    string ExpectedPublisher,
    string SignatureValidation,
    string VendorHashAvailability,
    string DownloadStrategy,
    string ReleaseUri,
    string ReleaseVersionPattern,
    string DeliveryUri)
{
    public static CatalogMetadataDetails Unknown { get; } = new(
        string.Empty,
        "HARD",
        ["Unknown"],
        "Unknown",
        string.Empty,
        string.Empty,
        ["Unknown"],
        string.Empty,
        MetadataVerificationState.VerificationRequired,
        [],
        false,
        string.Empty,
        string.Empty,
        string.Empty,
        "Unknown",
        "Unknown",
        "Unknown",
        string.Empty,
        string.Empty,
        string.Empty);
}

public sealed class PackageCatalog
{
    private readonly IReadOnlyDictionary<string, PackageDefinition> packages;

    public PackageCatalog(IEnumerable<PackageDefinition> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        var materialized = packages.ToArray();
        var duplicate = materialized.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new CatalogValidationException($"Catalog contains duplicate package ID '{duplicate.Key}'.");
        this.packages = materialized.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        Items = materialized;
    }

    public IReadOnlyList<PackageDefinition> Items { get; }

    public PackageDefinition GetRequired(string id) =>
        packages.TryGetValue(id, out var package) ? package : throw new KeyNotFoundException($"Package is not in the catalog: {id}.");
}
