using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Infrastructure.Windows.Processes;
using AVWorkstationToolkit.Infrastructure.Windows.Reboot;
using AVWorkstationToolkit.Infrastructure.Windows.Registry;
using AVWorkstationToolkit.Infrastructure.Windows.WinGet;

namespace AVWorkstationToolkit.App.Services;

public static class CompiledAppComposition
{
    public static IWorkstationPlanningCoordinator Create(string repositoryRoot)
    {
        var catalog = new RepositoryCatalogLoader().Load(repositoryRoot);
        var resolver = new WindowsWinGetResolver();
        var runner = new WinGetReadOnlyProcessRunner(resolver);
        var managed = catalog.Items.Where(item => item.HasManagedExecutionAuthority).Select(item => (item.Id, item.Name));
        return new WorkstationPlanningCoordinator(
            catalog,
            new WinGetInstalledPackageInventory(runner, managed),
            new WinGetAvailableUpdateInventory(runner),
            new WindowsUninstallRegistryInventory(),
            new WindowsRebootStateProvider(),
            new ExternalInventoryMatcher());
    }
}
