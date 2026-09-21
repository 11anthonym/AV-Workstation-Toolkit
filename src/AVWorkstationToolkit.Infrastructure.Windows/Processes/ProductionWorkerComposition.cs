using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Application.Workers;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Reboot;
using AVWorkstationToolkit.Infrastructure.Windows.Registry;
using AVWorkstationToolkit.Infrastructure.Windows.WinGet;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Infrastructure.Windows.Processes;

/// <summary>Builds the reviewed real-provider services used by the production compiled worker.</summary>
public sealed record ProductionWorkerServices(
    IActionWorkerPlanProvider Plans,
    IPackageActionExecutor Executor,
    long ManagedCatalogRevision);

public static class ProductionWorkerComposition
{
    public static ProductionWorkerServices CreateSourceCheckout(string repositoryRoot)
    {
        var catalog = new RepositoryCatalogLoader().Load(repositoryRoot);
        return CreateServices(catalog, 0);
    }

    public static ProductionWorkerServices Create(
        string dataRoot,
        string applicationRoot,
        string applicationVersion,
        ManagedCatalogRuntimeServices? managedCatalogRuntime = null)
    {
        var runtime = managedCatalogRuntime ?? ProductionManagedCatalogConfiguration.Create(applicationVersion);
        var managed = new ManagedCatalogStore(applicationRoot, dataRoot, runtime.Verifier, channel: null, requireSignedBaseline: true)
            .LoadActiveOrEmbedded();
        var supplementary = new RepositoryCatalogLoader().LoadSupplementary(applicationRoot);
        var catalog = new PackageCatalog(managed.Catalog.Items.Concat(supplementary.Items));
        return CreateServices(catalog, managed.Source.Revision);
    }

    private static ProductionWorkerServices CreateServices(PackageCatalog catalog, long managedCatalogRevision)
    {
        var resolver = new WindowsWinGetResolver();
        var readOnlyRunner = new WinGetReadOnlyProcessRunner(resolver);
        var managedPackages = catalog.Items
            .Where(item => item.HasManagedExecutionAuthority)
            .Select(item => (item.Id, item.Name));
        var planning = new WorkstationPlanningCoordinator(
            catalog,
            new WinGetInstalledPackageInventory(readOnlyRunner, managedPackages),
            new WinGetAvailableUpdateInventory(readOnlyRunner),
            new WindowsUninstallRegistryInventory(),
            new WindowsRebootStateProvider(),
            new ExternalInventoryMatcher());
        var executor = new WinGetPackageActionExecutor(new WinGetMutationProcessRunner(resolver));
        return new(new ProductionPlanProvider(planning, managedCatalogRevision), executor, managedCatalogRevision);
    }

    private sealed class ProductionPlanProvider(IWorkstationPlanningCoordinator planning, long revision) : IActionWorkerPlanProvider
    {
        public long ManagedCatalogRevision { get; } = revision;
        public async ValueTask<WorkstationPlan> ReadFreshPlanAsync(CancellationToken cancellationToken = default) =>
            await planning.RefreshAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
