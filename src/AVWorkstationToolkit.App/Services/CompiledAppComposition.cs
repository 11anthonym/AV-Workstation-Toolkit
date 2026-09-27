using System.IO;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Details;
using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Diagnostics;
using AVWorkstationToolkit.Infrastructure.Windows.Processes;
using AVWorkstationToolkit.Infrastructure.Windows.Reboot;
using AVWorkstationToolkit.Infrastructure.Windows.Registry;
using AVWorkstationToolkit.Infrastructure.Windows.WinGet;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Infrastructure.Windows.Files;
using AVWorkstationToolkit.Application.Vendors;
using AVWorkstationToolkit.Application.Compatibility;
using AVWorkstationToolkit.Infrastructure.Windows.Authenticode;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Vendors;
using AVWorkstationToolkit.Application.Catalog;
using AVWorkstationToolkit.Application.Workstation;
using AVWorkstationToolkit.Infrastructure.Windows.Migration;

namespace AVWorkstationToolkit.App.Services;

public sealed record CompiledAppServices(
    PackageCatalog Catalog,
    CompatibilityCatalogQueryService Compatibility,
    IReferenceCatalogUpdateService ReferenceCatalogUpdates,
    IManagedCatalogUpdateService ManagedCatalogUpdates,
    long ManagedCatalogRevision,
    IWorkstationPlanningCoordinator Planning,
    IReadOnlyDiagnosticsService Diagnostics,
    CatalogDetailService Details,
    CompiledActionCoordinator? Actions,
    IDiagnosticsExportService DiagnosticsExport,
    VendorInteractionCoordinator? Vendors,
    IValidatedUserHandoffService? Handoffs,
    IPackageDeliveryWorkflow? PackageDelivery,
    IApplicationMenuWorkflow ApplicationMenu,
    string Version,
    string ExecutionMode,
    bool IsProduction,
    MigrationComposition Migration);

/// <summary>The workstation migration service and the per-user data root that holds its checklist and profiles.</summary>
/// <remarks>OpenApplications finds apps that are open before an install; it is Restart Manager only in the packaged app.</remarks>
public sealed record MigrationComposition(WorkstationMigrationService Service, string DataRoot, string ProductVersion,
    IOpenApplicationService? OpenApplications = null);

public static class CompiledAppComposition
{
    // A source run uses the canonical per-user data root unless the caller supplies one; tests and QA always do.
    public static CompiledAppServices Create(string repositoryRoot, string? dataRoot = null)
        => CreateCore(repositoryRoot, dataRoot is null ? null : Path.GetFullPath(dataRoot), production: false);

    // The packaged production smoke that package QA runs uses the same composition against a disposable root that
    // ProductionRuntimePolicy bounds to the temporary folder, and its worker launcher never starts the worker.
    public static CompiledAppServices CreateProduction(
        string applicationRoot,
        string dataRoot,
        string version,
        string expectedWorkerSha256,
        string prerelease = "",
        bool packageQaSmoke = false)
    {
        var root = packageQaSmoke ? ProductionRuntimePolicy.RequirePackageQaDataRoot(dataRoot) : ProductionRuntimePolicy.RequireDataRoot(dataRoot);
        var runtimeRoot = packageQaSmoke
            ? ProductionRuntimePolicy.RequirePackageQaApplicationRoot(root, applicationRoot)
            : ProductionRuntimePolicy.RequireApplicationRoot(root, applicationRoot);
        return CreateCore(runtimeRoot, root, production: true, version, expectedWorkerSha256,
            ProductionManagedCatalogConfiguration.Create(version), prerelease, packageQaSmoke);
    }

    public static CompiledAppServices CreateManagedCatalogDevelopment(
        string applicationRoot,
        string dataRoot,
        string version,
        ManagedCatalogRuntimeServices managedCatalogRuntime) =>
        CreateCore(Path.GetFullPath(applicationRoot), Path.GetFullPath(dataRoot), production: false, version,
            expectedWorkerSha256: null, managedCatalogRuntime ?? throw new ArgumentNullException(nameof(managedCatalogRuntime)));

    private static CompiledAppServices CreateCore(
        string repositoryRoot,
        string? actionRoot,
        bool production = false,
        string? packagedVersion = null,
        string? expectedWorkerSha256 = null,
        ManagedCatalogRuntimeServices? managedCatalogRuntime = null,
        string prerelease = "",
        bool packageQaSmoke = false)
    {
        var loader = new RepositoryCatalogLoader();
        var managedUpdates = managedCatalogRuntime is null
            ? (IManagedCatalogUpdateService)new RepositoryManagedCatalogUpdateService(repositoryRoot)
            : new ManagedCatalogStore(repositoryRoot, actionRoot!, managedCatalogRuntime.Verifier,
                managedCatalogRuntime.ChannelClient, requireSignedBaseline: true);
        var managedCatalog = managedUpdates.LoadActiveOrEmbedded();
        var supplementary = loader.LoadSupplementary(repositoryRoot);
        var catalog = new PackageCatalog(managedCatalog.Catalog.Items.Concat(supplementary.Items));
        var resolver = new WindowsWinGetResolver();
        var runner = new WinGetReadOnlyProcessRunner(resolver);
        var managed = catalog.Items.Where(item => item.HasManagedExecutionAuthority).Select(item => (item.Id, item.Name));
        var installedInventory = new WinGetInstalledPackageInventory(runner, managed);
        var registryInventory = new WindowsUninstallRegistryInventory();
        var planning = new WorkstationPlanningCoordinator(
            catalog,
            installedInventory,
            new WinGetAvailableUpdateInventory(runner),
            registryInventory,
            new WindowsRebootStateProvider(),
            new ExternalInventoryMatcher(),
            externalReleases: new VendorExternalReleaseInventory(catalog));
        var canonicalDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVWorkstationToolkit");
        var dataRoot = !production ? actionRoot ?? canonicalDataRoot
            : packageQaSmoke ? ProductionRuntimePolicy.RequirePackageQaDataRoot(actionRoot!) : ProductionRuntimePolicy.RequireDataRoot(actionRoot!);
        var versionPath = Path.Combine(repositoryRoot, "VERSION");
        var version = packagedVersion ?? (File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : "Unknown");
        var productionCatalog = production
            ? ProductionReferenceCatalogConfiguration.Create(version)
            : new ProductionReferenceCatalogServices(
                new ReferenceCatalogBundleVerifier(new ReferenceCatalogTrustPolicy(version, new Dictionary<string, string>(StringComparer.Ordinal))),
                null);
        var catalogUpdates = new ReferenceCatalogStore(
            repositoryRoot,
            dataRoot,
            productionCatalog.BundleVerifier,
            productionCatalog.ChannelClient);
        var referenceCatalog = catalogUpdates.LoadActiveOrEmbedded();
        var compatibility = new CompatibilityCatalogQueryService(
            referenceCatalog.Compatibility,
            new UnresolvedInstalledVersionEvidenceProvider(),
            referenceCatalog.Hardware);
        var diagnostics = new ReadOnlyDiagnosticsService(
            new WindowsRuntimeDiagnosticsProvider(resolver, runner),
            new ApplicationDiagnosticContext(new ProductRelease(version, prerelease).SemanticVersion,
                production ? "Packaged compiled runtime" : "Source compiled migration", dataRoot, Path.Combine(dataRoot, "logs")));
        CompiledActionCoordinator? actions = null;
        VendorInteractionCoordinator? vendors = null;
        IPackageDeliveryWorkflow? packageDelivery = null;
        var cachePaths = new VendorCachePathPolicy();
        var verifier = new VendorPayloadVerificationService(cachePaths, new WinTrustAuthenticodeVerifier());
        IValidatedUserHandoffService handoffs = new WindowsValidatedUserHandoffService(cachePaths, verifier);
        IApplicationMenuWorkflow applicationMenu = new ApplicationMenuWorkflow(dataRoot, handoffs);
        if (production)
        {
            ICompiledWorkerLauncher workerLauncher = packageQaSmoke
                ? ProductionCompiledWorkerLauncher.ForPackageQaSmoke(dataRoot, repositoryRoot, expectedWorkerSha256!)
                : new ProductionCompiledWorkerLauncher(dataRoot, repositoryRoot, expectedWorkerSha256!);
            actions = new CompiledActionCoordinator(
                new ActionProtocolStore(dataRoot),
                workerLauncher,
                planning,
                new ActionRequestFactory(managedCatalogRevision: managedCatalog.Source.Revision));
            var credentialStore = new WindowsVendorCredentialStore();
            var sftp = new VendorSftpDeliveryService(cachePaths);
            vendors = new VendorInteractionCoordinator(
                dataRoot,
                new VendorHttpsDownloader(cachePaths),
                sftp,
                sftp,
                new VendorTrustedHostStore(),
                credentialStore,
                verifier);
            packageDelivery = new PackageDeliveryWorkflow(catalog, vendors, handoffs, cachePaths, dataRoot);
        }
        // Migration reads the same providers as the plan, and installs only through the action coordinator above,
        // which exists only in the packaged composition.
        var migration = new WorkstationMigrationService(
            planning,
            new WorkstationInventoryService(catalog, installedInventory, registryInventory, new WindowsWorkstationMachineInfoProvider()),
            new MigrationSessionFileStore(dataRoot),
            actions);
        var executionMode = production ? "Packaged compiled runtime" : "Source compiled runtime";
        return new(catalog, compatibility, catalogUpdates, managedUpdates, managedCatalog.Source.Revision, planning, diagnostics, new CatalogDetailService(), actions, new DiagnosticsExportService(dataRoot), vendors,
            handoffs, packageDelivery, applicationMenu, version, executionMode, production,
            new MigrationComposition(migration, dataRoot, new ProductRelease(version, prerelease).DisplayVersion,
                production ? new RestartManagerOpenApplications() : NoOpenApplications.Instance));
    }
}
