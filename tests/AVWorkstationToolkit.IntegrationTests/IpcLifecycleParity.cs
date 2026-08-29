using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Infrastructure.Windows.Files;

namespace AVWorkstationToolkit.IntegrationTests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IpcLifecycleFixture(int SchemaVersion, string ScenarioId, IReadOnlyList<IpcLifecycleCase> Cases);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IpcLifecycleCase(string CaseId, string Kind, string Mutation, IReadOnlyList<string> PackageIds, bool DryRun);

public static class IpcLifecycleParityEvaluator
{
    private const string RequestId = "request-20260829-142233-0123abcd";
    private static readonly DateTimeOffset Timestamp = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    public static async Task<object> EvaluateAsync(IpcLifecycleFixture fixture)
    {
        if (fixture.SchemaVersion != 1) throw new InvalidDataException($"Unsupported IPC lifecycle fixture schema: {fixture.SchemaVersion}.");
        var cases = new List<object>();
        foreach (var item in fixture.Cases) cases.Add(await EvaluateCaseAsync(item));
        return new { SchemaVersion = 1, fixture.ScenarioId, Cases = cases };
    }

    private static async Task<object> EvaluateCaseAsync(IpcLifecycleCase input)
    {
        try
        {
            return input.Kind switch
            {
                "Persistence" => await EvaluatePersistenceAsync(input),
                "Progress" => EvaluateProgress(input),
                "Result" => EvaluateResult(input),
                "Lifecycle" => EvaluateLifecycle(input),
                "Path" => EvaluatePath(input),
                _ => throw new InvalidDataException($"Unknown IPC parity kind: {input.Kind}.")
            };
        }
        catch (Exception exception) when (exception is ActionProtocolValidationException or ActionRequestValidationException or IOException or JsonException)
        {
            return Canonical(input, false);
        }
    }

    private static async Task<object> EvaluatePersistenceAsync(IpcLifecycleCase input)
    {
        var root = CreateTemporaryRoot();
        try
        {
            var request = Request(input.PackageIds, input.DryRun);
            var store = new ActionProtocolStore(root);
            var paths = await store.PersistRequestAsync(Authorized(request));
            var persisted = new ActionRequestCodec().Parse(await store.ReadArtifactAsync(RequestId, ActionArtifactKind.Request), RequestId);
            var residue = Directory.GetFiles(Path.GetDirectoryName(paths.RequestPath)!, "*.tmp").Length;
            return Canonical(input, residue == 0, packageIds: persisted.PackageIds);
        }
        finally { Directory.Delete(root, true); }
    }

    private static object EvaluateProgress(IpcLifecycleCase input)
    {
        var request = Request(input.PackageIds, input.DryRun);
        var codec = new ActionProgressCodec();
        ActionProgressParseBatch batch;
        if (input.Mutation == "IncompleteThenComplete")
        {
            var bytes = Encoding.UTF8.GetBytes(ProgressJson("complete") + "\n");
            var first = codec.ParseIncremental(bytes.AsSpan(0, bytes.Length - 2), request);
            batch = codec.ParseIncremental(bytes.AsSpan(bytes.Length - 2), request, first.State);
        }
        else
        {
            var payload = input.Mutation switch
            {
                "One" => ProgressJson("one") + "\n",
                "Multiple" => ProgressJson("one") + "\n" + ProgressJson("two") + "\n",
                "Malformed" => "{bad}\n",
                "WrongType" => MutateProgress(node => node["Level"] = 1) + "\n",
                "UnknownField" => MutateProgress(node => node["Extra"] = true) + "\n",
                "ForeignPackage" => MutateProgress(node => node["PackageId"] = "Vendor.Foreign") + "\n",
                "Oversized" => new string('x', ActionProtocolLimits.MaximumProgressRecordBytes + 1),
                _ => throw new InvalidDataException($"Unknown progress mutation: {input.Mutation}.")
            };
            batch = codec.ParseIncremental(Encoding.UTF8.GetBytes(payload), request, flush: input.Mutation == "Oversized");
        }
        return Canonical(input, batch.Issues.Count == 0, batch.Records.Count, batch.Issues.Count);
    }

    private static object EvaluateResult(IpcLifecycleCase input)
    {
        var root = CreateTemporaryRoot();
        try
        {
            var request = Request(input.PackageIds, input.DryRun);
            var paths = new ActionArtifactPathPolicy().GetPaths(root, RequestId);
            if (input.Mutation == "Oversized")
                _ = new ActionResultCodec().Parse(new byte[ActionProtocolLimits.MaximumResultBytes + 1], request, paths);
            var node = input.Mutation switch
            {
                "Success" => ResultNode(paths, "Succeeded", 0, [PackageNode("Succeeded", 0, true)]),
                "Failed" => ResultNode(paths, "Failed", 1, [PackageNode("Failed", 42, false)]),
                "Cancelled" => ResultNode(paths, "Cancelled", 2, []),
                "DryRun" => ResultNode(paths, "Succeeded", 0, [PackageNode("Planned", 0, false)]),
                "Malformed" => null,
                "WrongType" => MutateResult(paths, node => node["ExitCode"] = "0"),
                "UnknownField" => MutateResult(paths, node => node["Extra"] = true),
                "MismatchedRequest" => MutateResult(paths, node => node["RequestPath"] = paths.RequestPath + ".stale"),
                "Oversized" => throw new InvalidOperationException("Oversized result should already have been rejected."),
                _ => throw new InvalidDataException($"Unknown result mutation: {input.Mutation}.")
            };
            var payload = input.Mutation == "Malformed" ? "{"u8.ToArray() : Encoding.UTF8.GetBytes(node!.ToJsonString());
            var result = new ActionResultCodec().Parse(payload, request, paths);
            return Canonical(input, true, resultStatus: result.Status.ToString(), packageIds: result.Packages.Select(item => item.Id).ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    private static object EvaluateLifecycle(IpcLifecycleCase input)
    {
        var root = CreateTemporaryRoot();
        try
        {
            var request = Request(input.PackageIds, input.DryRun);
            var paths = new ActionArtifactPathPolicy().GetPaths(root, RequestId);
            var success = new ActionResultCodec().Parse(
                Encoding.UTF8.GetBytes(ResultNode(paths, "Succeeded", 0, [PackageNode(input.DryRun ? "Planned" : "Succeeded", 0, !input.DryRun)]).ToJsonString()),
                request,
                paths);
            var lifecycle = new ActionRequestLifecycle(request);
            lifecycle.MarkPersisted();
            lifecycle.MarkAwaitingWorker();
            switch (input.Mutation)
            {
                case "CancellationConfirmed":
                    lifecycle.RequestCancellation(RequestId);
                    lifecycle.MarkCancellationObserved(RequestId);
                    var cancelled = new ActionResultCodec().Parse(Encoding.UTF8.GetBytes(ResultNode(paths, "Cancelled", 2, []).ToJsonString()), request, paths);
                    lifecycle.AttachFinalResult(cancelled);
                    break;
                case "CompletedBeforeCancellationObserved":
                    lifecycle.RequestCancellation(RequestId);
                    lifecycle.AttachFinalResult(success);
                    break;
                case "ForeignProgress":
                    lifecycle.AttachProgress("request-20260829-142233-deadbeef", ProgressRecord());
                    break;
                case "DuplicateFinal":
                    lifecycle.AttachFinalResult(success);
                    if (lifecycle.AttachFinalResult(success)) throw new InvalidDataException("Duplicate result was not idempotent.");
                    break;
                default: throw new InvalidDataException($"Unknown lifecycle mutation: {input.Mutation}.");
            }
            return Canonical(input, true, lifecycleState: lifecycle.State.ToString(), cancellationState: lifecycle.Cancellation.ToString());
        }
        finally { Directory.Delete(root, true); }
    }

    private static object EvaluatePath(IpcLifecycleCase input)
    {
        var root = CreateTemporaryRoot();
        try
        {
            var requests = Directory.CreateDirectory(Path.Combine(root, "logs", "requests"));
            var policy = new ActionArtifactPathPolicy();
            var candidate = input.Mutation switch
            {
                "WrongExtension" => Path.Combine(requests.FullName, $"{RequestId}.result.txt"),
                "Nested" => Path.Combine(Directory.CreateDirectory(Path.Combine(requests.FullName, "nested")).FullName, $"{RequestId}.result.json"),
                "ForeignId" => Path.Combine(requests.FullName, "request-20260829-142233-deadbeef.result.json"),
                "Reparse" => Path.Combine(requests.FullName, $"{RequestId}.result.json"),
                _ => throw new InvalidDataException($"Unknown path mutation: {input.Mutation}.")
            };
            File.WriteAllText(candidate, "{}");
            if (input.Mutation == "Reparse")
            {
                policy = new ActionArtifactPathPolicy(
                    path => string.Equals(Path.GetFullPath(path), requests.FullName, StringComparison.OrdinalIgnoreCase)
                        ? FileAttributes.Directory | FileAttributes.ReparsePoint
                        : File.GetAttributes(path),
                    File.Exists,
                    Directory.Exists,
                    path => new FileInfo(path).Length);
            }
            policy.ValidateExistingArtifactPath(root, RequestId, ActionArtifactKind.Result, candidate);
            return Canonical(input, true);
        }
        finally { Directory.Delete(root, true); }
    }

    private static object Canonical(
        IpcLifecycleCase input,
        bool accepted,
        int recordCount = 0,
        int issueCount = 0,
        string resultStatus = "",
        string lifecycleState = "",
        string cancellationState = "",
        IReadOnlyList<string>? packageIds = null) => new
        {
            input.CaseId,
            Accepted = accepted,
            input.Kind,
            RecordCount = recordCount,
            IssueCount = issueCount,
            ResultStatus = resultStatus,
            LifecycleState = lifecycleState,
            CancellationState = cancellationState,
            PackageIds = packageIds ?? []
        };

    private static string ProgressJson(string message) => JsonSerializer.Serialize(new
    {
        Timestamp = Timestamp.ToString("o"),
        Level = "Info",
        Stage = "Starting",
        PackageId = "Vendor.One",
        Message = message
    });

    private static string MutateProgress(Action<JsonObject> mutation)
    {
        var node = JsonNode.Parse(ProgressJson("message"))!.AsObject();
        mutation(node);
        return node.ToJsonString();
    }

    private static JsonObject MutateResult(ActionArtifactPaths paths, Action<JsonObject> mutation)
    {
        var node = ResultNode(paths, "Succeeded", 0, [PackageNode("Succeeded", 0, true)]);
        mutation(node);
        return node;
    }

    private static JsonObject ResultNode(ActionArtifactPaths paths, string status, int exitCode, IReadOnlyList<JsonNode?> packages) => new()
    {
        ["SchemaVersion"] = 1,
        ["GeneratedAt"] = Timestamp.ToString("o"),
        ["Computer"] = "TEST-HOST",
        ["Status"] = status,
        ["Message"] = status,
        ["ExitCode"] = exitCode,
        ["RequestPath"] = paths.RequestPath,
        ["ProgressPath"] = paths.ProgressPath,
        ["WingetLogPath"] = paths.WinGetLogPath,
        ["Packages"] = new JsonArray(packages.ToArray())
    };

    private static JsonObject PackageNode(string status, int exitCode, bool verified) => new()
    {
        ["Id"] = "Vendor.One",
        ["Name"] = "Vendor One",
        ["Action"] = "Install",
        ["Status"] = status,
        ["ExitCode"] = exitCode,
        ["Verified"] = verified,
        ["StartedAt"] = Timestamp.ToString("o"),
        ["FinishedAt"] = Timestamp.AddSeconds(1).ToString("o"),
        ["Arguments"] = new JsonArray(
            "install", "--id", "Vendor.One", "--exact", "--source", "winget",
            "--accept-package-agreements", "--accept-source-agreements")
    };

    private static ActionProgressRecord ProgressRecord() => new(Timestamp, ActionProgressLevel.Info, "Starting", "Vendor.One", "message");
    private static ActionRequest Request(IReadOnlyList<string> packageIds, bool dryRun) =>
        new(1, RequestId, ManagedRequestAction.Install, packageIds, false, dryRun);
    private static AuthorizedActionRequest Authorized(ActionRequest request) =>
        new(request, request.PackageIds.Distinct(StringComparer.OrdinalIgnoreCase).Select(State).ToArray());

    private static PackageState State(string id)
    {
        var definition = new PackageDefinition(
            id, id, "Example", string.Empty, "Test", ProviderKind.WinGet, CatalogAuthority.ManagedWinGet,
            PackageProfile.Standard, PackagePriority.P2, PackageRisk.None, DeploymentPolicy.Allowlisted, MaintenancePolicy.Allowlisted,
            DeploymentClass.Managed, CatalogMaintenancePolicy.Managed, VersionRule.Latest, VersionCouplingMode.Independent,
            string.Empty, Lifecycle.Current, [], [], [], [LicensingModel.Free], ["PUBLIC-DL"], DistributionPolicy.PackageManagerOnly,
            [InstallationForm.WinGet], [SupportedOperatingSystem.Windows], DeliveryMode.None, ReleaseMode.None, DetectionMode.WinGet,
            DetectionVersionPolicy.None, "Stable", string.Empty, string.Empty, string.Empty, [], null, null, null, null, null,
            false, false, false, null, string.Empty, []);
        return new(definition, false, string.Empty, [], string.Empty, false, PackageStatus.Missing, "Missing", "Missing", PackageAction.Install, InventoryQuality.Complete);
    }

    private static string CreateTemporaryRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"awt-ipc-parity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
