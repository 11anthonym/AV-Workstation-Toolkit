using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Diagnostics;
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
            KnownVersion = string.Empty,
            Details = CatalogMetadataDetails.Unknown with
            {
                OfficialProductUri = "https://example.com/fixture-awareness",
                AuthoritativeDomain = "example.com",
                MetadataVerifiedOn = "2026-08-28",
                MetadataVerificationState = MetadataVerificationState.Current,
                ValidationMethods = ["WebPresence"],
                DownloadStrategy = "None"
            }
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
        var sources = new RegistrySourceStatus[]
        {
            new(RegistryInventorySource.Hklm64, true, 2, "Fixture source read successfully."),
            new(RegistryInventorySource.Hklm32, true, 1, "Fixture source read successfully."),
            new(RegistryInventorySource.Hkcu, false, 0, "Fixture source unavailable.")
        };
        var providers = new ProviderRefreshSummary(
            ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Partial, ProviderQuality.Complete,
            ["External inventory: 1 of 3 registry sources unavailable."],
            ExternalInventoryFailure: ProviderFailureKind.PartialInventory,
            ExternalInventoryDetail: "1 of 3 registry sources unavailable.",
            ExternalSources: sources);
        var reboot = new RebootState(true, [RebootReason.WindowsUpdate], "Windows Update restart is pending.");
        return new SmokePlanningCoordinator(new WorkstationPlan(states, summary, reboot, providers));
    }

    public static IReadOnlyDiagnosticsService CreateDiagnostics()
    {
        var runtime = new SmokeRuntimeDiagnosticsProvider();
        return new ReadOnlyDiagnosticsService(runtime, new("1.1.1", "Compiled migration smoke", @"C:\Fixture\AVWorkstationToolkit", @"C:\Fixture\AVWorkstationToolkit\logs"));
    }

    private sealed class SmokeRuntimeDiagnosticsProvider : IRuntimeDiagnosticsProvider
    {
        public Task<RuntimeDiagnosticFacts> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new RuntimeDiagnosticFacts(
                new(DiagnosticEvidenceState.Available, "Fixture Windows"),
                new(DiagnosticEvidenceState.Unknown, "Not loaded by compiled app"),
                new(DiagnosticEvidenceState.Available, ".NET 10 fixture"),
                new(DiagnosticEvidenceState.Available, "X64"),
                new(DiagnosticEvidenceState.Available, "Standard user"),
                new(DiagnosticEvidenceState.Available, @"C:\WindowsApps\winget.exe"),
                new(DiagnosticEvidenceState.Available, "v1.fixture")));
        }
    }
}
