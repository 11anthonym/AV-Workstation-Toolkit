using AVWorkstationToolkit.Application.Actions;
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
    long ManagedCatalogRevision,
    IOpenApplicationService OpenApplications);

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
        string applicationVersion)
    {
        var runtime = ProductionManagedCatalogConfiguration.Create(applicationVersion);
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
        return new(new ReusingWorkerPlanProvider(planning, managedCatalogRevision), executor, managedCatalogRevision,
            new RestartManagerOpenApplications());
    }
}

/// <summary>
/// The worker's live plan. A full refresh (WinGet export, the upgrade list, the registry, and restart state) takes several
/// seconds, and the worker needs one before every package and after every installer. A plan read after the last change is
/// still current, so it is reused until the worker says state may change (before each installer run), which halves the
/// refreshes in a multi-package run without ever checking a package against a plan from before an installer ran.
/// </summary>
public sealed class ReusingWorkerPlanProvider(IWorkstationPlanningCoordinator planning, long revision) : IActionWorkerPlanProvider
{
    private readonly IWorkstationPlanningCoordinator planning = planning ?? throw new ArgumentNullException(nameof(planning));
    private WorkstationPlan? current;

    public long ManagedCatalogRevision { get; } = revision;

    public async ValueTask<WorkstationPlan> ReadFreshPlanAsync(CancellationToken cancellationToken = default)
    {
        if (current is { } unchanged) return unchanged;
        var plan = await planning.RefreshAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        current = plan;
        return plan;
    }

    public void StateMayChange() => current = null;
}
