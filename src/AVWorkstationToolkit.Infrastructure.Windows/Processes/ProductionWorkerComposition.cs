using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Application.Workers;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Reboot;
using AVWorkstationToolkit.Infrastructure.Windows.Registry;
using AVWorkstationToolkit.Infrastructure.Windows.WinGet;

namespace AVWorkstationToolkit.Infrastructure.Windows.Processes;

/// <summary>Builds the reviewed real-provider services used by the production compiled worker.</summary>
public sealed record ProductionWorkerServices(
    IActionWorkerPlanProvider Plans,
    IPackageActionExecutor Executor);

public static class ProductionWorkerComposition
{
    public static ProductionWorkerServices Create(string applicationRoot)
    {
        var catalog = new RepositoryCatalogLoader().Load(applicationRoot);
        var resolver = new WindowsWinGetResolver();
        var readOnlyRunner = new WinGetReadOnlyProcessRunner(resolver);
        var managed = catalog.Items
            .Where(item => item.HasManagedExecutionAuthority)
            .Select(item => (item.Id, item.Name));
        var planning = new WorkstationPlanningCoordinator(
            catalog,
            new WinGetInstalledPackageInventory(readOnlyRunner, managed),
            new WinGetAvailableUpdateInventory(readOnlyRunner),
            new WindowsUninstallRegistryInventory(),
            new WindowsRebootStateProvider(),
            new ExternalInventoryMatcher());
        var executor = new WinGetPackageActionExecutor(new WinGetMutationProcessRunner(resolver));
        return new(new ProductionPlanProvider(planning), executor);
    }

    private sealed class ProductionPlanProvider(IWorkstationPlanningCoordinator planning) : IActionWorkerPlanProvider
    {
        public async ValueTask<WorkstationPlan> ReadFreshPlanAsync(CancellationToken cancellationToken = default) =>
            await planning.RefreshAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
