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

namespace AVWorkstationToolkit.App.Services;

public sealed record CompiledAppServices(
    PackageCatalog Catalog,
    IWorkstationPlanningCoordinator Planning,
    IReadOnlyDiagnosticsService Diagnostics,
    CatalogDetailService Details);

public static class CompiledAppComposition
{
    public static CompiledAppServices Create(string repositoryRoot)
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
        var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVWorkstationToolkit");
        var versionPath = Path.Combine(repositoryRoot, "VERSION");
        var version = File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : "Unknown";
        var diagnostics = new ReadOnlyDiagnosticsService(
            new WindowsRuntimeDiagnosticsProvider(resolver, runner),
            new ApplicationDiagnosticContext(version, "Source compiled migration", dataRoot, Path.Combine(dataRoot, "logs")));
        return new(catalog, planning, diagnostics, new CatalogDetailService());
    }
}
