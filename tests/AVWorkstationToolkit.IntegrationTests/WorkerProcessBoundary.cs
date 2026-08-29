using System.Diagnostics;
using System.Text.Json;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Infrastructure.Windows.Files;

namespace AVWorkstationToolkit.IntegrationTests;

internal sealed record WorkerProcessScenarioResult(string Name, string Status, int ExitCode, int ProgressRecords, int PackageOutcomes);
internal sealed record WorkerProcessBoundaryResult(int ScenarioCount, IReadOnlyList<WorkerProcessScenarioResult> Scenarios);

internal static class WorkerProcessBoundary
{
    private const string RequestId = "request-20260829-220000-a1b2c3d4";

    public static async Task<WorkerProcessBoundaryResult> RunAsync(string workerExecutable)
    {
        var executable = Path.GetFullPath(workerExecutable);
        if (!File.Exists(executable)) throw new FileNotFoundException("The compiled worker executable was not built.", executable);
        var results = new List<WorkerProcessScenarioResult>
        {
            await RunScenarioAsync(executable, "success", ["Vendor.One"],
                [Plan(Package("Vendor.One")), Plan(Package("Vendor.One"))],
                [Execution("Vendor.One", "Success", 0, 0)], ActionResultStatus.Succeeded),
            await RunScenarioAsync(executable, "failure", ["Vendor.One"],
                [Plan(Package("Vendor.One")), Plan(Package("Vendor.One"))],
                [Execution("Vendor.One", "Failure", 23, 0)], ActionResultStatus.Failed),
            await RunScenarioAsync(executable, "revalidation", ["Vendor.One"],
                [Plan(Package("Vendor.One")), Plan(Package("Vendor.One", "None", "Current"))],
                [], ActionResultStatus.Blocked),
            await RunCancellationAsync(executable)
        };
        return new(results.Count, results.AsReadOnly());
    }

    private static async Task<WorkerProcessScenarioResult> RunScenarioAsync(
        string executable,
        string name,
        IReadOnlyList<string> ids,
        IReadOnlyList<object> plans,
        IReadOnlyList<object> executions,
        ActionResultStatus expectedStatus)
    {
        var root = CreateRoot(name);
        try
        {
            var (request, store, paths) = await PersistRequestAsync(root, ids);
            WriteFixture(root, plans, executions);
            var exitCode = await LaunchAsync(executable, root, paths.RequestPath);
            var result = await ReadResultAsync(store, request, paths);
            AssertResult(name, expectedStatus, exitCode, result);
            var progress = ParseProgress(await store.ReadArtifactAsync(request.RequestId, ActionArtifactKind.Progress), request);
            if (progress.Issues.Count != 0 || progress.Records.Count == 0)
                throw new InvalidDataException($"Worker process scenario '{name}' produced invalid progress.");
            return new(name, result.Status.ToString(), exitCode, progress.Records.Count, result.Packages.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<WorkerProcessScenarioResult> RunCancellationAsync(string executable)
    {
        var root = CreateRoot("cancellation");
        try
        {
            var ids = new[] { "Vendor.One", "Vendor.Two" };
            var (request, store, paths) = await PersistRequestAsync(root, ids);
            var both = new[] { Package("Vendor.One"), Package("Vendor.Two") };
            WriteFixture(root, [Plan(both), Plan(both)], [Execution("Vendor.One", "Success", 0, 800)]);

            using var process = Start(executable, root, paths.RequestPath);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(paths.ProgressPath) && ReadSharedText(paths.ProgressPath).Contains("\"Stage\":\"Starting\"", StringComparison.Ordinal))
                    break;
                if (process.HasExited) throw new InvalidOperationException("Worker exited before the cancellation boundary was reached.");
                await Task.Delay(50);
            }
            if (!File.Exists(paths.ProgressPath) || !ReadSharedText(paths.ProgressPath).Contains("\"Stage\":\"Starting\"", StringComparison.Ordinal))
                throw new TimeoutException("Worker did not reach the deterministic cancellation boundary.");
            if (!await store.CreateCancellationMarkerAsync(request.RequestId))
                throw new IOException("Cancellation marker was not created exactly once.");
            await WaitForExitAsync(process);

            var result = await ReadResultAsync(store, request, paths);
            AssertResult("cancellation", ActionResultStatus.Cancelled, process.ExitCode, result);
            if (result.Packages.Count != 1 || result.Packages[0].Id != "Vendor.One")
                throw new InvalidDataException("Cancellation was not observed between packages.");
            var progress = ParseProgress(await store.ReadArtifactAsync(request.RequestId, ActionArtifactKind.Progress), request);
            if (!progress.Records.Any(item => item.Stage == "Cancelled"))
                throw new InvalidDataException("Cancellation progress was not emitted.");
            return new("cancellation", result.Status.ToString(), process.ExitCode, progress.Records.Count, result.Packages.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(ActionRequest Request, ActionProtocolStore Store, ActionArtifactPaths Paths)> PersistRequestAsync(
        string root,
        IReadOnlyList<string> ids)
    {
        var request = new ActionRequest(1, RequestId, ManagedRequestAction.Install, ids, false, false);
        var states = ids.Select(id => State(id)).ToArray();
        var authorized = new AuthorizedActionRequest(request, states);
        var store = new ActionProtocolStore(root);
        var paths = await store.PersistRequestAsync(authorized);
        return (request, store, paths);
    }

    private static void WriteFixture(string root, IReadOnlyList<object> plans, IReadOnlyList<object> executions)
    {
        var fixture = new { SchemaVersion = 1, Computer = "FixtureHost", Plans = plans, Executions = executions };
        File.WriteAllText(Path.Combine(root, "worker-fixture.json"), JsonSerializer.Serialize(fixture));
    }

    private static object Plan(params object[] packages) => new { RebootPending = false, RebootReason = "", Packages = packages };
    private static object Plan(IReadOnlyList<object> packages) => new { RebootPending = false, RebootReason = "", Packages = packages };
    private static object Package(string id, string action = "Install", string status = "Missing", string risk = "None") =>
        new { Id = id, Name = id, Action = action, Status = status, Risk = risk };
    private static object Execution(string id, string outcome, int exitCode, int delayMilliseconds) =>
        new { PackageId = id, Outcome = outcome, ExitCode = exitCode, DelayMilliseconds = delayMilliseconds };

    private static Process Start(string executable, string root, string requestPath)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--test-mode");
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add("--request");
        start.ArgumentList.Add(requestPath);
        return Process.Start(start) ?? throw new InvalidOperationException("The compiled worker process did not start.");
    }

    private static async Task<int> LaunchAsync(string executable, string root, string requestPath)
    {
        using var process = Start(executable, root, requestPath);
        await WaitForExitAsync(process);
        return process.ExitCode;
    }

    private static async Task WaitForExitAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("The compiled fake worker did not exit within 20 seconds.");
        }
        var error = await process.StandardError.ReadToEndAsync();
        if (process.ExitCode is < 0 or > 3) throw new InvalidOperationException($"Worker exited unexpectedly: {error}");
    }

    private static async Task<ActionFinalResult> ReadResultAsync(ActionProtocolStore store, ActionRequest request, ActionArtifactPaths paths)
    {
        var payload = await store.ReadArtifactAsync(request.RequestId, ActionArtifactKind.Result);
        return new ActionResultCodec().Parse(payload, request, paths);
    }

    private static ActionProgressParseBatch ParseProgress(byte[] payload, ActionRequest request) =>
        new ActionProgressCodec().ParseIncremental(payload, request, flush: true);

    private static string ReadSharedText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void AssertResult(string name, ActionResultStatus expected, int exitCode, ActionFinalResult result)
    {
        if (result.Status != expected || result.ExitCode != exitCode)
            throw new InvalidDataException($"Worker process scenario '{name}' returned {result.Status}/{result.ExitCode}, process exit {exitCode}; expected {expected}.");
    }

    private static PackageState State(string id)
    {
        var definition = new PackageDefinition(
            id, id, "Fixture", string.Empty, "Test", ProviderKind.WinGet, CatalogAuthority.ManagedWinGet,
            PackageProfile.Standard, PackagePriority.P2, PackageRisk.None, DeploymentPolicy.Allowlisted, MaintenancePolicy.Allowlisted,
            DeploymentClass.Managed, CatalogMaintenancePolicy.Managed, VersionRule.Latest, VersionCouplingMode.Independent,
            string.Empty, Lifecycle.Current, [], [], [], [LicensingModel.Free], ["PUBLIC-DL"], DistributionPolicy.PackageManagerOnly,
            [InstallationForm.WinGet], [SupportedOperatingSystem.Windows], DeliveryMode.None, ReleaseMode.None, DetectionMode.WinGet,
            DetectionVersionPolicy.None, "Stable", string.Empty, string.Empty, string.Empty, [], null, null, null, null, null,
            false, false, false, null, string.Empty, []);
        return new(definition, false, string.Empty, [], string.Empty, false, PackageStatus.Missing, "Missing", "Missing", PackageAction.Install, InventoryQuality.Complete);
    }

    private static string CreateRoot(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"awt-worker-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
