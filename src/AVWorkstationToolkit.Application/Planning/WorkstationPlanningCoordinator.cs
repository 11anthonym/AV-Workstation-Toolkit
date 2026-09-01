using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Providers;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;

namespace AVWorkstationToolkit.Application.Planning;

public enum PlanningRefreshStage
{
    ReadingWinGetInventory,
    ReadingWinGetUpdates,
    ReadingExternalInventory,
    ReadingExternalReleases,
    CheckingRebootState,
    BuildingPlan,
    Ready
}

public sealed record WorkstationPlanSummary(
    int Total,
    int Current,
    int Missing,
    int Updates,
    int Manual,
    int ManualUpdates,
    int Held,
    int Inventory,
    int NotDetected,
    int InventoryIncomplete,
    int InventoryUnavailable,
    int CheckUnavailable,
    int Awareness,
    int Errors)
{
    public int ManagedActions => Missing + Updates;
    public int ManualActions => Manual + ManualUpdates + Held;
    public int InventoryWarnings => InventoryIncomplete + InventoryUnavailable + CheckUnavailable;
}

public sealed record ProviderRefreshSummary(
    ProviderQuality WinGetInventoryQuality,
    ProviderQuality WinGetUpdateQuality,
    ProviderQuality ExternalInventoryQuality,
    ProviderQuality RebootQuality,
    IReadOnlyList<string> Warnings,
    ProviderFailureKind WinGetInventoryFailure = ProviderFailureKind.None,
    ProviderFailureKind WinGetUpdateFailure = ProviderFailureKind.None,
    ProviderFailureKind ExternalInventoryFailure = ProviderFailureKind.None,
    ProviderFailureKind RebootFailure = ProviderFailureKind.None,
    string WinGetInventoryDetail = "",
    string WinGetUpdateDetail = "",
    string ExternalInventoryDetail = "",
    string RebootDetail = "",
    IReadOnlyList<RegistrySourceStatus>? ExternalSources = null);

public sealed record WorkstationPlan(
    IReadOnlyList<PackageState> Packages,
    WorkstationPlanSummary Summary,
    RebootState Reboot,
    ProviderRefreshSummary Providers,
    IReadOnlyDictionary<string, ExternalReleaseEvidence>? ExternalReleases = null);

public interface IWorkstationPlanningCoordinator
{
    Task<WorkstationPlan> RefreshAsync(
        IProgress<PlanningRefreshStage>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates read-only provider evidence and deterministic domain planning.
/// This type has no action-worker or mutation dependency.
/// </summary>
public sealed class WorkstationPlanningCoordinator : IWorkstationPlanningCoordinator
{
    private readonly PackageCatalog catalog;
    private readonly IInstalledPackageInventory installedInventory;
    private readonly IAvailableUpdateInventory availableUpdates;
    private readonly IExternalApplicationInventory externalInventory;
    private readonly IExternalReleaseInventory externalReleases;
    private readonly IRebootStateProvider rebootProvider;
    private readonly ExternalInventoryMatcher externalMatcher;
    private readonly PlanningService planningService;

    public WorkstationPlanningCoordinator(
        PackageCatalog catalog,
        IInstalledPackageInventory installedInventory,
        IAvailableUpdateInventory availableUpdates,
        IExternalApplicationInventory externalInventory,
        IRebootStateProvider rebootProvider,
        ExternalInventoryMatcher? externalMatcher = null,
        PlanningService? planningService = null,
        IExternalReleaseInventory? externalReleases = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.installedInventory = installedInventory ?? throw new ArgumentNullException(nameof(installedInventory));
        this.availableUpdates = availableUpdates ?? throw new ArgumentNullException(nameof(availableUpdates));
        this.externalInventory = externalInventory ?? throw new ArgumentNullException(nameof(externalInventory));
        this.externalReleases = externalReleases ?? new CatalogBaselineExternalReleaseInventory(catalog);
        this.rebootProvider = rebootProvider ?? throw new ArgumentNullException(nameof(rebootProvider));
        this.externalMatcher = externalMatcher ?? new ExternalInventoryMatcher();
        this.planningService = planningService ?? new PlanningService();
    }

    public async Task<WorkstationPlan> RefreshAsync(
        IProgress<PlanningRefreshStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(PlanningRefreshStage.ReadingWinGetInventory);
        var installed = await installedInventory.ReadAsync(cancellationToken).ConfigureAwait(false);

        progress?.Report(PlanningRefreshStage.ReadingWinGetUpdates);
        var updates = await availableUpdates.ReadAsync(cancellationToken).ConfigureAwait(false);

        progress?.Report(PlanningRefreshStage.ReadingExternalInventory);
        var external = await externalInventory.ReadAsync(cancellationToken).ConfigureAwait(false);

        progress?.Report(PlanningRefreshStage.ReadingExternalReleases);
        var releases = await externalReleases.ReadAsync(cancellationToken).ConfigureAwait(false);

        progress?.Report(PlanningRefreshStage.CheckingRebootState);
        var reboot = await rebootProvider.ReadAsync(cancellationToken).ConfigureAwait(false);

        progress?.Report(PlanningRefreshStage.BuildingPlan);
        var states = BuildStates(installed, updates, external, releases);
        var rebootState = new RebootState(reboot.Pending, reboot.Reasons, reboot.Detail);
        var providerSummary = new ProviderRefreshSummary(
            installed.Quality,
            updates.Quality,
            external.Quality,
            reboot.Quality,
            BuildWarnings(installed, updates, external, reboot),
            installed.Failure,
            updates.Failure,
            external.Failure,
            reboot.Failure,
            installed.Detail,
            updates.Detail,
            external.Detail,
            reboot.Detail,
            external.Sources);
        var releaseMap = releases.Releases.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var result = new WorkstationPlan(states, Summarize(states), rebootState, providerSummary, releaseMap);
        progress?.Report(PlanningRefreshStage.Ready);
        return result;
    }

    private IReadOnlyList<PackageState> BuildStates(
        InstalledPackageInventoryResult installed,
        AvailableUpdateInventoryResult updates,
        RegistryInventoryResult external,
        ExternalReleaseInventoryResult releases)
    {
        var installedById = installed.Packages.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var updatesById = updates.Updates.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var externalById = externalMatcher.Match(catalog.Items, external)
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var releasesById = releases.Releases.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var managedSourceAvailable = installed.Quality == ProviderQuality.Complete;

        return catalog.Items.Select(package =>
        {
            PackageEvidence evidence;
            if (package.Provider == ProviderKind.WinGet)
            {
                var isInstalled = installedById.TryGetValue(package.Id, out var installedRecord);
                updatesById.TryGetValue(package.Id, out var updateRecord);
                var hasUpdate = updates.Quality == ProviderQuality.Complete && updateRecord is not null;
                evidence = new PackageEvidence(
                    managedSourceAvailable,
                    true,
                    managedSourceAvailable,
                    managedSourceAvailable ? InventoryQuality.Complete : InventoryQuality.Unavailable,
                    isInstalled,
                    installedRecord?.InstalledVersion ?? string.Empty,
                    isInstalled && installedRecord is not null ? [installedRecord.InstalledVersion] : [],
                    hasUpdate,
                    hasUpdate ? updateRecord?.AvailableVersion ?? string.Empty : string.Empty,
                    installed.Detail,
                    updates.Detail);
            }
            else
            {
                var matched = externalById[package.Id];
                releasesById.TryGetValue(package.Id, out var release);
                evidence = new PackageEvidence(
                    true,
                    true,
                    matched.Reliable,
                    matched.InventoryQuality,
                    matched.Installed,
                    matched.InstalledVersion,
                    matched.InstalledVersions,
                    false,
                    release?.AvailableVersion ?? package.KnownVersion,
                    matched.Detail,
                    release?.Detail ?? (package.KnownVersion.Length > 0
                        ? "Using the validated catalog release baseline."
                        : "No validated catalog release version is available."));
            }

            return planningService.Evaluate(package, evidence);
        }).ToArray();
    }

    private static IReadOnlyList<string> BuildWarnings(
        InstalledPackageInventoryResult installed,
        AvailableUpdateInventoryResult updates,
        RegistryInventoryResult external,
        RebootDetectionResult reboot)
    {
        var warnings = new List<string>();
        if (installed.Quality != ProviderQuality.Complete) warnings.Add($"WinGet inventory: {installed.Detail}");
        if (updates.Quality != ProviderQuality.Complete) warnings.Add($"WinGet update check: {updates.Detail}");
        if (external.Quality != ProviderQuality.Complete) warnings.Add($"External inventory: {external.Detail}");
        if (reboot.Quality != ProviderQuality.Complete) warnings.Add($"Reboot detection: {reboot.Detail}");
        return warnings;
    }

    private static WorkstationPlanSummary Summarize(IReadOnlyList<PackageState> packages)
    {
        return new WorkstationPlanSummary(
            packages.Count,
            Count(PackageStatus.Current),
            Count(PackageStatus.Missing),
            Count(PackageStatus.UpdateAvailable),
            Count(PackageStatus.Manual),
            Count(PackageStatus.ManualUpdate),
            Count(PackageStatus.Held),
            Count(PackageStatus.Inventory),
            Count(PackageStatus.NotDetected),
            Count(PackageStatus.InventoryIncomplete),
            Count(PackageStatus.InventoryUnavailable),
            Count(PackageStatus.CheckUnavailable),
            Count(PackageStatus.Awareness),
            Count(PackageStatus.Error));

        int Count(PackageStatus status) => packages.Count(package => package.Status == status);
    }
}
