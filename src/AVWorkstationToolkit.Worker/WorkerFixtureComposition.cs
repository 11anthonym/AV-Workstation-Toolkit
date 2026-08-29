using System.Text.Json;
using System.Text.Json.Serialization;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Application.Workers;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;

namespace AVWorkstationToolkit.Worker;

internal sealed record WorkerFixture(int SchemaVersion, string Computer, WorkerPlanFixture[] Plans, WorkerExecutionFixture[] Executions);
internal sealed record WorkerPlanFixture(bool RebootPending, string RebootReason, WorkerPackageFixture[] Packages);
internal sealed record WorkerPackageFixture(string Id, string Name, string Action, string Status, string Risk);
internal sealed record WorkerExecutionFixture(string PackageId, string Outcome, int ExitCode, int DelayMilliseconds);

internal static class WorkerFixtureLoader
{
    private const int MaximumFixtureBytes = 256 * 1024;

    public static WorkerFixture Load(string explicitRoot)
    {
        if (!Path.IsPathFullyQualified(explicitRoot)) throw new InvalidDataException("The worker fixture root must be absolute.");
        var root = Path.GetFullPath(explicitRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The worker fixture root must be an existing regular directory.");
        var path = Path.Combine(root, "worker-fixture.json");
        if (!File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("The canonical worker fixture file is missing or unsafe.");
        var info = new FileInfo(path);
        if (info.Length > MaximumFixtureBytes) throw new InvalidDataException("The worker fixture exceeds its size limit.");

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        byte[] payload;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
        {
            if (stream.Length > MaximumFixtureBytes) throw new InvalidDataException("The worker fixture exceeds its size limit.");
            using var output = new MemoryStream(checked((int)stream.Length));
            var buffer = new byte[4096];
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                if (output.Length > MaximumFixtureBytes - read) throw new InvalidDataException("The worker fixture exceeds its size limit.");
                output.Write(buffer, 0, read);
            }
            payload = output.ToArray();
        }
        var fixture = JsonSerializer.Deserialize<WorkerFixture>(payload, options)
            ?? throw new InvalidDataException("The worker fixture is empty.");
        Validate(fixture);
        return fixture;
    }

    private static void Validate(WorkerFixture fixture)
    {
        if (fixture.SchemaVersion != 1) throw new InvalidDataException("The worker fixture schema version is unsupported.");
        if (string.IsNullOrWhiteSpace(fixture.Computer) || fixture.Computer.Length > ActionProtocolLimits.MaximumComputerCharacters || fixture.Computer.Any(char.IsControl))
            throw new InvalidDataException("The worker fixture computer label is invalid.");
        if (fixture.Plans is null || fixture.Plans.Length == 0 || fixture.Plans.Length > 101)
            throw new InvalidDataException("The worker fixture must contain a bounded plan sequence.");
        if (fixture.Executions is null || fixture.Executions.Length > 100)
            throw new InvalidDataException("The worker fixture executor sequence is invalid.");
        foreach (var plan in fixture.Plans)
        {
            if (plan.Packages is null || plan.Packages.Length > 100) throw new InvalidDataException("A worker fixture plan is invalid.");
            _ = ParseReboot(plan);
            foreach (var package in plan.Packages) _ = MapPackage(package);
        }
        foreach (var execution in fixture.Executions)
        {
            ActionRequestRules.ValidatePackageIds([execution.PackageId]);
            if (execution.Outcome is not ("Success" or "Failure" or "VerificationFailure"))
                throw new InvalidDataException("The fake executor outcome is unsupported.");
            if (execution.DelayMilliseconds is < 0 or > 10_000)
                throw new InvalidDataException("The fake executor delay is outside its test bound.");
            if (execution.Outcome == "Failure" && execution.ExitCode == 0 || execution.Outcome != "Failure" && execution.ExitCode != 0)
                throw new InvalidDataException("The fake executor outcome and exit code are inconsistent.");
        }
    }

    internal static WorkstationPlan MapPlan(WorkerPlanFixture fixture)
    {
        var packages = fixture.Packages.Select(MapPackage).ToArray();
        return new(
            packages,
            new WorkstationPlanSummary(packages.Length, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            ParseReboot(fixture),
            new ProviderRefreshSummary(ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, []));
    }

    private static RebootState ParseReboot(WorkerPlanFixture fixture)
    {
        if (!fixture.RebootPending)
        {
            if (!string.IsNullOrEmpty(fixture.RebootReason)) throw new InvalidDataException("A clear reboot fixture cannot contain a reason.");
            return RebootState.Clear;
        }
        var reason = fixture.RebootReason switch
        {
            "WindowsUpdate" => RebootReason.WindowsUpdate,
            "ComponentBasedServicing" => RebootReason.ComponentBasedServicing,
            _ => throw new InvalidDataException("The worker fixture reboot reason is unsupported.")
        };
        return new(true, [reason], fixture.RebootReason);
    }

    private static PackageState MapPackage(WorkerPackageFixture fixture)
    {
        ActionRequestRules.ValidatePackageIds([fixture.Id]);
        if (string.IsNullOrWhiteSpace(fixture.Name) || fixture.Name.Length > ActionProtocolLimits.MaximumNameCharacters)
            throw new InvalidDataException("A worker fixture package name is invalid.");
        if (!Enum.TryParse<PackageAction>(fixture.Action, false, out var action) || !Enum.IsDefined(action))
            throw new InvalidDataException("A worker fixture package action is unsupported.");
        if (!Enum.TryParse<PackageStatus>(fixture.Status, false, out var status) || !Enum.IsDefined(status))
            throw new InvalidDataException("A worker fixture package status is unsupported.");
        if (!Enum.TryParse<PackageRisk>(fixture.Risk, false, out var risk) || !Enum.IsDefined(risk))
            throw new InvalidDataException("A worker fixture package risk is unsupported.");
        var definition = new PackageDefinition(
            fixture.Id, fixture.Name, "Fixture", string.Empty, "Phase 8 deterministic fixture", ProviderKind.WinGet,
            CatalogAuthority.ManagedWinGet, PackageProfile.Standard, PackagePriority.P2, risk, DeploymentPolicy.Allowlisted,
            MaintenancePolicy.Allowlisted, DeploymentClass.Managed, CatalogMaintenancePolicy.Managed, VersionRule.Latest,
            VersionCouplingMode.Independent, string.Empty, Lifecycle.Current, [], [], [], [LicensingModel.Free], ["PUBLIC-DL"],
            DistributionPolicy.PackageManagerOnly, [InstallationForm.WinGet], [SupportedOperatingSystem.Windows], DeliveryMode.None,
            ReleaseMode.None, DetectionMode.WinGet, DetectionVersionPolicy.None, "Stable", string.Empty, string.Empty, string.Empty,
            [], null, null, null, null, null, risk == PackageRisk.Driver, risk == PackageRisk.Service,
            risk == PackageRisk.Listener, null, string.Empty, []);
        return new(definition, status != PackageStatus.Missing, string.Empty, [], string.Empty,
            status == PackageStatus.UpdateAvailable, status, status.ToString(), status.ToString(), action, InventoryQuality.Complete);
    }
}

internal sealed class FixtureWorkerPlanProvider(IReadOnlyList<WorkerPlanFixture> plans) : IActionWorkerPlanProvider
{
    private int index;

    public ValueTask<WorkstationPlan> ReadFreshPlanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (index >= plans.Count) throw new InvalidOperationException("The deterministic plan sequence was exhausted.");
        return ValueTask.FromResult(WorkerFixtureLoader.MapPlan(plans[index++]));
    }
}

internal sealed class DeterministicFakePackageExecutor(IReadOnlyList<WorkerExecutionFixture> executions) : IPackageActionExecutor
{
    private int index;

    public async ValueTask<PackageExecutionResult> ExecuteAsync(PackageExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (index >= executions.Count) throw new InvalidOperationException("The deterministic fake executor sequence was exhausted.");
        var fixture = executions[index++];
        if (!string.Equals(fixture.PackageId, request.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The fake executor package does not match the authorized package.");
        if (fixture.DelayMilliseconds > 0)
            await Task.Delay(fixture.DelayMilliseconds, cancellationToken).ConfigureAwait(false);
        return fixture.Outcome switch
        {
            "Success" => PackageExecutionResult.Success,
            "Failure" => PackageExecutionResult.Failure(fixture.ExitCode),
            "VerificationFailure" => PackageExecutionResult.VerificationFailure,
            _ => throw new InvalidOperationException("The deterministic fake executor outcome is unsupported.")
        };
    }
}
