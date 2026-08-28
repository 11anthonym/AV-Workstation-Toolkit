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
    IReadOnlyList<string> CatalogTags)
{
    public bool HasManagedExecutionAuthority =>
        Authority == CatalogAuthority.ManagedWinGet &&
        Provider == ProviderKind.WinGet &&
        Deployment == DeploymentPolicy.Allowlisted;
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
