using System.Text.Json;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Development;
using AVWorkstationToolkit.Infrastructure.Windows.Files;

namespace AVWorkstationToolkit.IntegrationTests;

internal sealed record CompiledActionFlowResult(string Status, int ProgressRecords, int RefreshCount, bool RequestCorrelated);

internal static class CompiledActionFlowBoundary
{
    public static async Task<CompiledActionFlowResult> RunAsync(string repositoryRoot)
    {
        var root = Path.Combine(Path.GetTempPath(), $"awt-phase11-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var initial = Plan(State(PackageStatus.Missing, PackageAction.Install));
            var current = Plan(State(PackageStatus.Current, PackageAction.None, installed: true));
            WriteFixture(root);
            var planning = new FixedPlanning(current);
            var coordinator = new CompiledActionCoordinator(
                new ActionProtocolStore(root),
                new CompiledMigrationWorkerLauncher(Path.GetFullPath(repositoryRoot), root),
                planning,
                pollInterval: TimeSpan.FromMilliseconds(20),
                resultTimeout: TimeSpan.FromSeconds(20));

            var result = await coordinator.StartAsync(ManagedRequestAction.Install, initial.Packages, initial, false, false);
            return new(result.Result.Status.ToString(), coordinator.Snapshot.Progress.Count, planning.Calls,
                coordinator.Snapshot.RequestId == result.Result.RequestId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteFixture(string root)
    {
        var missing = new { Id = "Vendor.One", Name = "Vendor One", Action = "Install", Status = "Missing", Risk = "None" };
        var current = new { Id = "Vendor.One", Name = "Vendor One", Action = "None", Status = "Current", Risk = "None" };
        var fixture = new
        {
            SchemaVersion = 1,
            Computer = "FixtureHost",
            Plans = new object[]
            {
                new { RebootPending = false, RebootReason = "", Packages = new[] { missing } },
                new { RebootPending = false, RebootReason = "", Packages = new[] { missing } },
                new { RebootPending = false, RebootReason = "", Packages = new[] { current } }
            },
            Executions = new[] { new { PackageId = "Vendor.One", Outcome = "Success", ExitCode = 0, DelayMilliseconds = 10 } }
        };
        File.WriteAllText(Path.Combine(root, "worker-fixture.json"), JsonSerializer.Serialize(fixture));
    }

    private static WorkstationPlan Plan(PackageState package) => new(
        [package], new(1, package.Status == PackageStatus.Current ? 1 : 0, package.Status == PackageStatus.Missing ? 1 : 0,
            0, 0, package.CanSelect ? 1 : 0, 0, 0, 0, 0, 0, 0, 0, 0), RebootState.Clear,
        new(ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, []));

    private static PackageState State(PackageStatus status, PackageAction action, bool installed = false)
    {
        var package = new PackageDefinition(
            "Vendor.One", "Vendor One", "Vendor", string.Empty, "Fixture", ProviderKind.WinGet, CatalogAuthority.ManagedWinGet,
            PackageProfile.Standard, PackagePriority.P2, PackageRisk.None, DeploymentPolicy.Allowlisted, MaintenancePolicy.Allowlisted,
            DeploymentClass.Managed, CatalogMaintenancePolicy.Managed, VersionRule.Latest, VersionCouplingMode.Independent,
            string.Empty, Lifecycle.Current, [], [], [], [LicensingModel.Free], ["PUBLIC-DL"], DistributionPolicy.PackageManagerOnly,
            [InstallationForm.WinGet], [SupportedOperatingSystem.Windows], DeliveryMode.None, ReleaseMode.None, DetectionMode.WinGet,
            DetectionVersionPolicy.None, "Stable", string.Empty, string.Empty, string.Empty, [], null, null, null, null, null,
            false, false, false, null, string.Empty, []);
        return new(package, installed, string.Empty, [], string.Empty, false, status, status.ToString(), status.ToString(), action, InventoryQuality.Complete);
    }

    private sealed class FixedPlanning(WorkstationPlan plan) : IWorkstationPlanningCoordinator
    {
        public int Calls { get; private set; }
        public Task<WorkstationPlan> RefreshAsync(IProgress<PlanningRefreshStage>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(plan);
        }
    }
}
