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

namespace AVWorkstationToolkit.App.Services;

public sealed record CompiledAppServices(
    PackageCatalog Catalog,
    CompatibilityCatalogQueryService Compatibility,
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
    bool IsLiveRehearsal,
    bool IsProduction);

public static class CompiledAppComposition
{
    public static CompiledAppServices Create(string repositoryRoot)
        => CreateCore(repositoryRoot, null, production: false);

    public static CompiledAppServices CreateProduction(
        string applicationRoot,
        string dataRoot,
        string version,
        string expectedWorkerSha256)
    {
        var canonicalRoot = ProductionRuntimePolicy.RequireDataRoot(dataRoot);
        var runtimeRoot = ProductionRuntimePolicy.RequireApplicationRoot(canonicalRoot, applicationRoot);
        return CreateCore(runtimeRoot, canonicalRoot, production: true, version, expectedWorkerSha256);
    }

    private static CompiledAppServices CreateCore(
        string repositoryRoot,
        string? actionRoot,
        bool production = false,
        string? packagedVersion = null,
        string? expectedWorkerSha256 = null)
    {
        var catalog = new RepositoryCatalogLoader().Load(repositoryRoot);
        var compatibility = new CompatibilityCatalogQueryService(
            new RepositoryCompatibilityCatalogLoader().Load(repositoryRoot),
            new UnresolvedInstalledVersionEvidenceProvider(),
            new RepositoryHardwareIdentityCatalogLoader().Load(repositoryRoot));
        var resolver = new WindowsWinGetResolver();
        var runner = new WinGetReadOnlyProcessRunner(resolver);
        var managed = catalog.Items.Where(item => item.HasManagedExecutionAuthority).Select(item => (item.Id, item.Name));
        var planning = new WorkstationPlanningCoordinator(
            catalog,
            new WinGetInstalledPackageInventory(runner, managed),
            new WinGetAvailableUpdateInventory(runner),
            new WindowsUninstallRegistryInventory(),
            new WindowsRebootStateProvider(),
            new ExternalInventoryMatcher(),
            externalReleases: new VendorExternalReleaseInventory(catalog));
        var canonicalDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVWorkstationToolkit");
        var dataRoot = production ? ProductionRuntimePolicy.RequireDataRoot(actionRoot!) : canonicalDataRoot;
        var versionPath = Path.Combine(repositoryRoot, "VERSION");
        var version = packagedVersion ?? (File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : "Unknown");
        var diagnostics = new ReadOnlyDiagnosticsService(
            new WindowsRuntimeDiagnosticsProvider(resolver, runner),
            new ApplicationDiagnosticContext(version, production ? "Packaged compiled runtime" : "Source compiled migration", dataRoot, Path.Combine(dataRoot, "logs")));
        CompiledActionCoordinator? actions = null;
        VendorInteractionCoordinator? vendors = null;
        IPackageDeliveryWorkflow? packageDelivery = null;
        var cachePaths = new VendorCachePathPolicy();
        var verifier = new VendorPayloadVerificationService(cachePaths, new WinTrustAuthenticodeVerifier());
        IValidatedUserHandoffService handoffs = new WindowsValidatedUserHandoffService(cachePaths, verifier);
        IApplicationMenuWorkflow applicationMenu = new ApplicationMenuWorkflow(dataRoot, handoffs);
        if (production)
        {
            ICompiledWorkerLauncher workerLauncher = new ProductionCompiledWorkerLauncher(dataRoot, repositoryRoot, expectedWorkerSha256!);
            actions = new CompiledActionCoordinator(
                new ActionProtocolStore(dataRoot),
                workerLauncher,
                planning);
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
        var executionMode = production ? "Packaged compiled runtime" : "Source compiled runtime";
        return new(catalog, compatibility, planning, diagnostics, new CatalogDetailService(), actions, new DiagnosticsExportService(dataRoot), vendors,
            handoffs, packageDelivery, applicationMenu, version, executionMode, false, production);
    }
}
