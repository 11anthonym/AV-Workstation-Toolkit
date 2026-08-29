using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;

namespace AVWorkstationToolkit.App.Services;

internal sealed class SmokePlanningCoordinator(WorkstationPlan plan) : IWorkstationPlanningCoordinator
{
    public Task<WorkstationPlan> RefreshAsync(IProgress<PlanningRefreshStage>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(PlanningRefreshStage.ReadingWinGetInventory);
        progress?.Report(PlanningRefreshStage.BuildingPlan);
        progress?.Report(PlanningRefreshStage.Ready);
        return Task.FromResult(plan);
    }

    public static SmokePlanningCoordinator Create()
    {
        var parser = new CatalogParser(new DateOnly(2026, 8, 28));
        var managed = parser.NormalizeManagedCatalog(
        [
            new("Standard", "Fixture missing", "Fixture.Missing", "Fixture Vendor", "None", "Compiled WPF install selection fixture", null, null),
            new("Standard", "Fixture update", "Fixture.Update", "Fixture Vendor", "None", "Compiled WPF update selection fixture", null, null)
        ], "NeverMatchThisFixture").Items;
        var awareness = managed[0] with
        {
            Id = "Fixture.Awareness",
            Name = "Fixture awareness",
            Provider = ProviderKind.External,
            Authority = CatalogAuthority.AwarenessOnly,
            Deployment = DeploymentPolicy.ManualHold,
            Maintenance = MaintenancePolicy.Hold,
            DeploymentClass = DeploymentClass.AwarenessOnly,
            CatalogMaintenancePolicy = CatalogMaintenancePolicy.NotApplicable,
            DetectionMode = DetectionMode.None,
            DeliveryMode = DeliveryMode.Awareness,
            InstallationForms = [],
            KnownVersion = string.Empty
        };
        var states = new PackageState[]
        {
            new(managed[0], false, string.Empty, [], string.Empty, false, PackageStatus.Missing,
                "Available for approved installation", "AllowlistedInstallAvailable", PackageAction.Install, InventoryQuality.Complete),
            new(managed[1], true, "1.0", ["1.0"], "1.1", true, PackageStatus.UpdateAvailable,
                "Allowlisted update available", "AllowlistedUpdateAvailable", PackageAction.Update, InventoryQuality.Complete),
            new(awareness, false, string.Empty, [], string.Empty, false, PackageStatus.Awareness,
                "Known catalog record; this product is not treated as a detectable Windows application.", "AwarenessOnly", PackageAction.None, InventoryQuality.NotApplicable)
        };
        var summary = new WorkstationPlanSummary(3, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0);
        var providers = new ProviderRefreshSummary(ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, []);
        return new SmokePlanningCoordinator(new WorkstationPlan(states, summary, RebootState.Clear, providers));
    }
}
