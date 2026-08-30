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
using AVWorkstationToolkit.Infrastructure.Windows.Authenticode;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Vendors;

namespace AVWorkstationToolkit.App.Services;

public sealed record CompiledAppServices(
    PackageCatalog Catalog,
    IWorkstationPlanningCoordinator Planning,
    IReadOnlyDiagnosticsService Diagnostics,
    CatalogDetailService Details,
    CompiledActionCoordinator? Actions,
    IDiagnosticsExportService DiagnosticsExport,
    VendorInteractionCoordinator? Vendors,
    IValidatedUserHandoffService? Handoffs,
    bool IsLiveRehearsal);

public static class CompiledAppComposition
{
    public static CompiledAppServices Create(string repositoryRoot, string? migrationTestRoot = null)
        => CreateCore(repositoryRoot, migrationTestRoot, liveRehearsal: false);

    public static CompiledAppServices CreateLiveRehearsal(string repositoryRoot, string explicitRehearsalRoot)
        => CreateCore(repositoryRoot, explicitRehearsalRoot, liveRehearsal: true);

    private static CompiledAppServices CreateCore(string repositoryRoot, string? actionRoot, bool liveRehearsal)
    {
        var catalog = new RepositoryCatalogLoader().Load(repositoryRoot);
        var resolver = new WindowsWinGetResolver();
        var runner = new WinGetReadOnlyProcessRunner(resolver);
        var managed = catalog.Items.Where(item => item.HasManagedExecutionAuthority).Select(item => (item.Id, item.Name));
        var planning = new WorkstationPlanningCoordinator(
            catalog,
            new WinGetInstalledPackageInventory(runner, managed),
            new WinGetAvailableUpdateInventory(runner),
            new WindowsUninstallRegistryInventory(),
            new WindowsRebootStateProvider(),
            new ExternalInventoryMatcher());
        var canonicalDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVWorkstationToolkit");
        var dataRoot = liveRehearsal ? LiveRehearsalRootPolicy.RequireExisting(actionRoot!) : canonicalDataRoot;
        var versionPath = Path.Combine(repositoryRoot, "VERSION");
        var version = File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : "Unknown";
        var diagnostics = new ReadOnlyDiagnosticsService(
            new WindowsRuntimeDiagnosticsProvider(resolver, runner),
            new ApplicationDiagnosticContext(version, "Source compiled migration", dataRoot, Path.Combine(dataRoot, "logs")));
        CompiledActionCoordinator? actions = null;
        VendorInteractionCoordinator? vendors = null;
        IValidatedUserHandoffService? handoffs = null;
        if (actionRoot is not null)
        {
            ICompiledWorkerLauncher workerLauncher = liveRehearsal
                ? new CompiledLiveRehearsalWorkerLauncher(repositoryRoot, actionRoot)
                : new CompiledMigrationWorkerLauncher(repositoryRoot, actionRoot);
            actions = new CompiledActionCoordinator(
                new ActionProtocolStore(actionRoot),
                workerLauncher,
                planning);
            var cachePaths = new VendorCachePathPolicy();
            var verifier = new VendorPayloadVerificationService(cachePaths, new WinTrustAuthenticodeVerifier());
            vendors = new VendorInteractionCoordinator(
                actionRoot,
                new VendorHttpsDownloader(cachePaths),
                new VendorSftpDeliveryService(cachePaths),
                new WindowsVendorCredentialStore(),
                verifier);
            handoffs = new WindowsValidatedUserHandoffService(cachePaths, verifier);
        }
        return new(catalog, planning, diagnostics, new CatalogDetailService(), actions, new DiagnosticsExportService(dataRoot), vendors, handoffs, liveRehearsal);
    }
}
