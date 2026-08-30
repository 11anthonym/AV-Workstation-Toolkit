using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Application.Workers;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Processes;
using AVWorkstationToolkit.Infrastructure.Windows.Reboot;
using AVWorkstationToolkit.Infrastructure.Windows.Registry;
using AVWorkstationToolkit.Infrastructure.Windows.WinGet;

namespace AVWorkstationToolkit.Worker;

internal sealed record LiveRehearsalWorkerServices(
    IActionWorkerPlanProvider Plans,
    IPackageActionExecutor Executor);

internal static class LiveRehearsalWorkerComposition
{
    public static LiveRehearsalWorkerServices Create(string repositoryRoot)
    {
        var catalog = new RepositoryCatalogLoader().Load(repositoryRoot);
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
        return new(new LiveRehearsalPlanProvider(planning), executor);
    }

    private sealed class LiveRehearsalPlanProvider(IWorkstationPlanningCoordinator planning) : IActionWorkerPlanProvider
    {
        public async ValueTask<WorkstationPlan> ReadFreshPlanAsync(CancellationToken cancellationToken = default) =>
            await planning.RefreshAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
