using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Infrastructure.Windows.Files;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class ActionProtocolTests
{
    private const string RequestId = "request-20260829-142233-0123abcd";
    private static readonly DateTimeOffset Timestamp = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ArtifactPolicyDerivesOnlyCanonicalDirectChildPaths()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var policy = new ActionArtifactPathPolicy();
            var paths = policy.GetPaths(root, RequestId);
            var requests = Path.Combine(root, "logs", "requests");
            Assert.AreEqual(Path.Combine(requests, $"{RequestId}.json"), paths.RequestPath);
            Assert.AreEqual(Path.Combine(requests, $"{RequestId}.progress.jsonl"), paths.ProgressPath);
            Assert.AreEqual(Path.Combine(requests, $"{RequestId}.result.json"), paths.ResultPath);
            Assert.AreEqual(Path.Combine(requests, $"{RequestId}.cancel"), paths.CancellationPath);
            Assert.AreEqual(Path.Combine(requests, $"{RequestId}.winget.log"), paths.WinGetLogPath);
            Assert.ThrowsExactly<ActionRequestValidationException>(() => policy.GetPaths(root, "../request-invalid"));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ArtifactPolicyRejectsNestedWrongExtensionMismatchedAndReparsePaths()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var requests = Directory.CreateDirectory(Path.Combine(root, "logs", "requests"));
            var canonical = Path.Combine(requests.FullName, $"{RequestId}.result.json");
            File.WriteAllText(canonical, "{}");
            var policy = new ActionArtifactPathPolicy();
            var mismatch = Assert.ThrowsExactly<ActionProtocolValidationException>(() =>
                policy.ValidateExistingArtifactPath(root, RequestId, ActionArtifactKind.Progress, canonical));
            Assert.AreEqual(ActionProtocolFailure.RequestMismatch, mismatch.Failure);

            var nested = Directory.CreateDirectory(Path.Combine(requests.FullName, "nested"));
            var nestedPath = Path.Combine(nested.FullName, $"{RequestId}.result.json");
            File.WriteAllText(nestedPath, "{}");
            Assert.ThrowsExactly<ActionProtocolValidationException>(() =>
                policy.ValidateExistingArtifactPath(root, RequestId, ActionArtifactKind.Result, nestedPath));

            var deterministic = new ActionArtifactPathPolicy(
                path => string.Equals(Path.GetFullPath(path), requests.FullName, StringComparison.OrdinalIgnoreCase)
                    ? FileAttributes.Directory | FileAttributes.ReparsePoint
                    : File.GetAttributes(path),
                File.Exists,
                Directory.Exists,
                path => new FileInfo(path).Length);
            Assert.ThrowsExactly<IOException>(() => deterministic.ValidateExistingArtifact(root, RequestId, ActionArtifactKind.Result));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task StorePersistsAuthorizedRequestAtomicallyWithoutOverwriteOrResidue()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var request = Request(["Vendor.One"]);
            var store = new ActionProtocolStore(root);
            var paths = await store.PersistRequestAsync(Authorized(request));
            Assert.IsTrue(File.Exists(paths.RequestPath));
            Assert.HasCount(0, Directory.GetFiles(Path.GetDirectoryName(paths.RequestPath)!, "*.tmp"));
            var parsed = new ActionRequestCodec().Parse(await store.ReadArtifactAsync(RequestId, ActionArtifactKind.Request), RequestId);
            Assert.AreEqual(RequestId, parsed.RequestId);
            await Assert.ThrowsExactlyAsync<IOException>(() => store.PersistRequestAsync(Authorized(request)));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task StoreRejectsIncompleteAuthorizationAndUsesIdempotentCancellationIntent()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var request = Request(["Vendor.One", "Vendor.Two"]);
            var incomplete = new AuthorizedActionRequest(request, [State("Vendor.One")]);
            await Assert.ThrowsExactlyAsync<ActionRequestValidationException>(() => new ActionProtocolStore(root).PersistRequestAsync(incomplete));

            var store = new ActionProtocolStore(root);
            await store.PersistRequestAsync(Authorized(Request(["Vendor.One"])));
            Assert.IsTrue(await store.CreateCancellationMarkerAsync(RequestId));
            Assert.IsFalse(await store.CreateCancellationMarkerAsync(RequestId));
            var marker = Encoding.UTF8.GetString(await store.ReadArtifactAsync(RequestId, ActionArtifactKind.Cancellation));
            StringAssert.Contains(marker, "Stop requested by user.");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ProgressParserRetainsIncompleteUtf8AndLineUntilComplete()
    {
        var request = Request(["Vendor.One"]);
        var codec = new ActionProgressCodec();
        var line = ProgressJson("Starting café") + "\r\n";
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(line)).ToArray();
        var split = Array.IndexOf(bytes, (byte)0xC3) + 1;
        var first = codec.ParseIncremental(bytes.AsSpan(0, split), request);
        Assert.IsEmpty(first.Records);
        Assert.IsGreaterThan(0, first.State.PendingBytes.Length);
        var second = codec.ParseIncremental(bytes.AsSpan(split), request, first.State);
        Assert.HasCount(1, second.Records);
        Assert.AreEqual("Starting café", second.Records[0].Message);
        Assert.IsEmpty(second.Issues);
        Assert.AreEqual(0, second.State.PendingBytes.Length);
    }

    [TestMethod]
    public void ProgressParserReportsMalformedCompleteRecordButContinues()
    {
        var request = Request(["Vendor.One"]);
        var input = Encoding.UTF8.GetBytes("{bad}\n" + ProgressJson("Valid") + "\n");
        var batch = new ActionProgressCodec().ParseIncremental(input, request);
        Assert.HasCount(1, batch.Records);
        Assert.HasCount(1, batch.Issues);
        Assert.AreEqual(ActionProtocolFailure.MalformedJson, batch.Issues[0].Failure);
        Assert.AreEqual("Valid", batch.Records[0].Message);
    }

    [TestMethod]
    public void ProgressParserRejectsUnknownFieldsWrongTypesAndForeignPackagesAsIssues()
    {
        var request = Request(["Vendor.One"]);
        var valid = JsonNode.Parse(ProgressJson("message"))!.AsObject();
        var cases = new List<string>();
        valid["Extra"] = true;
        cases.Add(valid.ToJsonString());
        valid.Remove("Extra");
        valid["Level"] = 1;
        cases.Add(valid.ToJsonString());
        valid["Level"] = "Info";
        valid["PackageId"] = "Vendor.Other";
        cases.Add(valid.ToJsonString());
        var batch = new ActionProgressCodec().ParseIncremental(Encoding.UTF8.GetBytes(string.Join("\n", cases) + "\n"), request);
        Assert.IsEmpty(batch.Records);
        Assert.HasCount(3, batch.Issues);
        CollectionAssert.AreEqual(
            new[] { ActionProtocolFailure.UnknownField, ActionProtocolFailure.WrongType, ActionProtocolFailure.PackageMismatch },
            batch.Issues.Select(item => item.Failure).ToArray());
    }

    [TestMethod]
    public void ProgressParserFlushesTrailingRecordAndBoundsFilesAndLines()
    {
        var codec = new ActionProgressCodec();
        var request = Request(["Vendor.One"]);
        var payload = Encoding.UTF8.GetBytes(ProgressJson("flush"));
        var unflushed = codec.ParseIncremental(payload, request);
        Assert.IsEmpty(unflushed.Records);
        var flushed = codec.ParseIncremental([], request, unflushed.State, flush: true);
        Assert.HasCount(1, flushed.Records);
        Assert.ThrowsExactly<ActionProtocolValidationException>(() =>
            codec.ParseIncremental(new byte[ActionProtocolLimits.MaximumProgressRecordBytes + 1], request));
        var state = new ActionProgressParseState(ActionProtocolLimits.MaximumProgressBytes, 0, []);
        Assert.ThrowsExactly<ActionProtocolValidationException>(() => codec.ParseIncremental([1], request, state));
    }

    [TestMethod]
    public void ResultParserAcceptsSuccessfulFailedCancelledAndDryRunSemantics()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var paths = new ActionArtifactPathPolicy().GetPaths(root, RequestId);
            var codec = new ActionResultCodec();
            Assert.AreEqual(ActionResultStatus.Succeeded,
                codec.Parse(ResultJson(paths, "Succeeded", 0, PackageJson("Succeeded", 0, true)), Request(["Vendor.One"]), paths).Status);
            Assert.AreEqual(ActionResultStatus.Failed,
                codec.Parse(ResultJson(paths, "Failed", 1, PackageJson("Failed", 42, false)), Request(["Vendor.One"]), paths).Status);
            Assert.AreEqual(ActionResultStatus.Cancelled,
                codec.Parse(ResultJson(paths, "Cancelled", 2, null), Request(["Vendor.One"]), paths).Status);
            Assert.AreEqual(PackageOutcomeStatus.Planned,
                codec.Parse(ResultJson(paths, "Succeeded", 0, PackageJson("Planned", 0, false)), Request(["Vendor.One"], dryRun: true), paths).Packages[0].Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ResultParserRejectsMalformedExistenceWrongTypesUnknownFieldsAndPathMismatch()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var paths = new ActionArtifactPathPolicy().GetPaths(root, RequestId);
            var codec = new ActionResultCodec();
            var request = Request(["Vendor.One"]);
            Assert.ThrowsExactly<ActionProtocolValidationException>(() => codec.Parse("{}"u8, request, paths));
            Assert.ThrowsExactly<ActionProtocolValidationException>(() => codec.Parse("{"u8, request, paths));

            var node = JsonNode.Parse(ResultText(paths, "Succeeded", 0, PackageJson("Succeeded", 0, true)))!.AsObject();
            node["Extra"] = true;
            Assert.AreEqual(ActionProtocolFailure.UnknownField,
                Assert.ThrowsExactly<ActionProtocolValidationException>(() => codec.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()), request, paths)).Failure);
            node.Remove("Extra");
            node["ExitCode"] = "0";
            Assert.AreEqual(ActionProtocolFailure.WrongType,
                Assert.ThrowsExactly<ActionProtocolValidationException>(() => codec.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()), request, paths)).Failure);
            node["ExitCode"] = 0;
            node["RequestPath"] = paths.RequestPath + ".stale";
            Assert.AreEqual(ActionProtocolFailure.RequestMismatch,
                Assert.ThrowsExactly<ActionProtocolValidationException>(() => codec.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()), request, paths)).Failure);

            node = ResultNode(paths, "Succeeded", 0, PackageJson("Succeeded", 0, true));
            node.Remove("ManagedCatalogRevision");
            Assert.AreEqual(ActionProtocolFailure.MissingField,
                Assert.ThrowsExactly<ActionProtocolValidationException>(() => codec.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()), request, paths)).Failure);
            node["ManagedCatalogRevision"] = new JsonArray();
            Assert.AreEqual(ActionProtocolFailure.WrongType,
                Assert.ThrowsExactly<ActionProtocolValidationException>(() => codec.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()), request, paths)).Failure);
            node["ManagedCatalogRevision"] = 1;
            Assert.AreEqual(ActionProtocolFailure.RequestMismatch,
                Assert.ThrowsExactly<ActionProtocolValidationException>(() => codec.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()), request, paths)).Failure);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ResultParserAllowsOnlyTheExactEmptyRevisionMismatchRejection()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var paths = new ActionArtifactPathPolicy().GetPaths(root, RequestId);
            var codec = new ActionResultCodec();
            var request = Request(["Vendor.One"], revision: 1);
            var message = "The request targets managed catalog revision 1, but the worker independently verified revision 2. Restart AV Workstation Toolkit and try again.";

            var legitimate = ResultNode(paths, "Rejected", 1, null);
            legitimate["ManagedCatalogRevision"] = 2;
            legitimate["Message"] = message;
            var accepted = codec.Parse(Encoding.UTF8.GetBytes(legitimate.ToJsonString()), request, paths);
            Assert.AreEqual(ActionResultStatus.Rejected, accepted.Status);
            Assert.AreEqual(2L, accepted.ManagedCatalogRevision);
            Assert.IsEmpty(accepted.Packages);

            var matching = ResultNode(paths, "Rejected", 1, null);
            matching["ManagedCatalogRevision"] = 1;
            Assert.AreEqual(1L, codec.Parse(Encoding.UTF8.GetBytes(matching.ToJsonString()), request, paths).ManagedCatalogRevision);

            var successful = ResultNode(paths, "Succeeded", 0, PackageJson("Succeeded", 0, true));
            successful["ManagedCatalogRevision"] = 2;
            Assert.AreEqual(ActionProtocolFailure.RequestMismatch,
                Assert.ThrowsExactly<ActionProtocolValidationException>(() =>
                    codec.Parse(Encoding.UTF8.GetBytes(successful.ToJsonString()), request, paths)).Failure);

            var completedOutcome = ResultNode(paths, "Rejected", 1, PackageJson("Succeeded", 0, true));
            completedOutcome["ManagedCatalogRevision"] = 2;
            completedOutcome["Message"] = message;
            Assert.AreEqual(ActionProtocolFailure.RequestMismatch,
                Assert.ThrowsExactly<ActionProtocolValidationException>(() =>
                    codec.Parse(Encoding.UTF8.GetBytes(completedOutcome.ToJsonString()), request, paths)).Failure);

            legitimate["Message"] = "Restart requested without independently verified revision evidence.";
            Assert.AreEqual(ActionProtocolFailure.RequestMismatch,
                Assert.ThrowsExactly<ActionProtocolValidationException>(() =>
                    codec.Parse(Encoding.UTF8.GetBytes(legitimate.ToJsonString()), request, paths)).Failure);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ResultParserRejectsForeignDuplicateAndSemanticallyInvalidPackages()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var paths = new ActionArtifactPathPolicy().GetPaths(root, RequestId);
            var codec = new ActionResultCodec();
            var request = Request(["Vendor.One"]);
            var foreign = PackageJson("Succeeded", 0, true); foreign["Id"] = "Vendor.Other";
            Assert.AreEqual(ActionProtocolFailure.PackageMismatch,
                Assert.ThrowsExactly<ActionProtocolValidationException>(() => codec.Parse(ResultJson(paths, "Succeeded", 0, foreign), request, paths)).Failure);
            var duplicate = ResultNode(paths, "Succeeded", 0, PackageJson("Succeeded", 0, true));
            duplicate["Packages"] = new JsonArray(PackageJson("Succeeded", 0, true), PackageJson("Succeeded", 0, true));
            Assert.ThrowsExactly<ActionProtocolValidationException>(() => codec.Parse(Encoding.UTF8.GetBytes(duplicate.ToJsonString()), request, paths));
            Assert.ThrowsExactly<ActionProtocolValidationException>(() =>
                codec.Parse(ResultJson(paths, "Succeeded", 0, PackageJson("Succeeded", 0, false)), request, paths));
            var arbitraryArguments = PackageJson("Succeeded", 0, true);
            arbitraryArguments["Arguments"] = new JsonArray("uninstall", "Vendor.One");
            Assert.ThrowsExactly<ActionProtocolValidationException>(() =>
                codec.Parse(ResultJson(paths, "Succeeded", 0, arbitraryArguments), request, paths));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void LifecycleEnforcesCorrelationCancellationAndTerminalStates()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var request = Request(["Vendor.One"]);
            var paths = new ActionArtifactPathPolicy().GetPaths(root, RequestId);
            var result = new ActionResultCodec().Parse(ResultJson(paths, "Succeeded", 0, PackageJson("Succeeded", 0, true)), request, paths);
            var lifecycle = new ActionRequestLifecycle(request);
            Assert.ThrowsExactly<ActionProtocolValidationException>(() => lifecycle.AttachProgress(RequestId, ProgressRecord()));
            lifecycle.MarkPersisted();
            lifecycle.MarkAwaitingWorker();
            lifecycle.AttachProgress(RequestId, ProgressRecord());
            Assert.AreEqual(ActionLifecycleState.Running, lifecycle.State);
            Assert.ThrowsExactly<ActionProtocolValidationException>(() => lifecycle.AttachProgress("request-20260829-142233-deadbeef", ProgressRecord()));
            lifecycle.RequestCancellation(RequestId);
            Assert.AreEqual(ActionCancellationState.Requested, lifecycle.Cancellation);
            Assert.IsTrue(lifecycle.AttachFinalResult(result));
            Assert.AreEqual(ActionLifecycleState.Completed, lifecycle.State);
            Assert.AreEqual(ActionCancellationState.CompletedBeforeObservation, lifecycle.Cancellation);
            Assert.IsFalse(lifecycle.AttachFinalResult(result));
            Assert.ThrowsExactly<ActionProtocolValidationException>(() => lifecycle.AttachProgress(RequestId, ProgressRecord()));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void LifecycleTreatsCancellationIntentObservationAndConfirmationAsDistinct()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var request = Request(["Vendor.One"]);
            var paths = new ActionArtifactPathPolicy().GetPaths(root, RequestId);
            var cancelled = new ActionResultCodec().Parse(ResultJson(paths, "Cancelled", 2, null), request, paths);
            var lifecycle = new ActionRequestLifecycle(request);
            lifecycle.MarkPersisted();
            lifecycle.MarkAwaitingWorker();
            lifecycle.RequestCancellation(RequestId);
            Assert.AreEqual(ActionLifecycleState.CancellationRequested, lifecycle.State);
            lifecycle.MarkCancellationObserved(RequestId);
            Assert.AreEqual(ActionCancellationState.ObservedBetweenPackages, lifecycle.Cancellation);
            lifecycle.AttachFinalResult(cancelled);
            Assert.AreEqual(ActionLifecycleState.Cancelled, lifecycle.State);
            Assert.AreEqual(ActionCancellationState.ConfirmedCancelled, lifecycle.Cancellation);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void FinalResultAcceptsExactlyTheThreeReviewedArgumentVectors()
    {
        var root = CreateTemporaryRoot();
        try
        {
        var request = Request(["Vendor.One"]);
        var paths = new ActionArtifactPathPolicy().GetPaths(root, RequestId);
        var codec = new ActionResultCodec();

        // The three shapes ManagedWinGetArgumentPolicy can emit, all of which are legitimate results.
        foreach (var tail in new[]
        {
            Array.Empty<string>(),                             // risk-bearing package
            ["--disable-interactivity"],                       // low risk, InstallerDefault
            new[] { "--silent", "--disable-interactivity" }    // low risk, Silent
        })
        {
            var parsed = codec.Parse(ResultJson(paths, "Succeeded", 0, PackageWithTail(tail)), request, paths);
            Assert.AreEqual(ActionResultStatus.Succeeded, parsed.Status);
            CollectionAssert.AreEqual(BaseVector.Concat(tail).ToArray(), parsed.Packages.Single().Arguments.ToArray());
        }

        // Everything else stays rejected: an unexpected flag, a missing required flag, a reordered
        // tail, a partially reviewed tail, an altered ID, and an altered source.
        foreach (var rejected in new[]
        {
            new[] { "--silent", "--disable-interactivity", "--force" },
            ["--silent"],
            ["--disable-interactivity", "--silent"],
            ["--interactive"],
            new[] { "--disable-interactivity", "--disable-interactivity" }
        })
        {
            Assert.ThrowsExactly<ActionProtocolValidationException>(
                () => codec.Parse(ResultJson(paths, "Succeeded", 0, PackageWithTail(rejected)), request, paths),
                $"tail was accepted: {string.Join(' ', rejected)}");
        }

        foreach (var (index, replacement) in new[] { (2, "Vendor.Other"), (5, "msstore"), (3, "--fuzzy"), (0, "uninstall") })
        {
            var altered = PackageWithTail(["--silent", "--disable-interactivity"]);
            var vector = altered["Arguments"]!.AsArray();
            vector[index] = replacement;
            Assert.ThrowsExactly<ActionProtocolValidationException>(
                () => codec.Parse(ResultJson(paths, "Succeeded", 0, altered), request, paths),
                $"altered argument {index} was accepted: {replacement}");
        }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task InstallerDefaultUpgradeResultSurvivesRealFinalResultPersistence()
    {
        // The regression: a PuTTY upgrade that WinGet completed could not be persisted, so the UI
        // reported no final result. The builder alone cannot prove this - it has to survive the file
        // protocol's own write-and-reparse path.
        var root = CreateTemporaryRoot();
        try
        {
            var request = new ActionRequest(
                ActionRequestRules.CurrentSchemaVersion, RequestId, ManagedRequestAction.Update, ["PuTTY.PuTTY"], false, false);
            var store = new ActionProtocolStore(root);
            var upgradable = State("PuTTY.PuTTY") with
            {
                Installed = true,
                UpgradeAvailable = true,
                Status = PackageStatus.UpdateAvailable,
                Action = PackageAction.Update
            };
            var paths = await store.PersistRequestAsync(new AuthorizedActionRequest(request, [upgradable]));
            await using var protocol = new ActionWorkerFileProtocol(root, RequestId);
            await protocol.InitializeAsync(request);

            var package = PackageJson("Succeeded", 0, true);
            package["Id"] = "PuTTY.PuTTY";
            package["Name"] = "PuTTY";
            package["Action"] = "Update";
            package["Arguments"] = new JsonArray(
                "upgrade", "--id", "PuTTY.PuTTY", "--exact", "--source", "winget",
                "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity");
            var result = new ActionResultCodec().Parse(ResultJson(paths, "Succeeded", 0, package), request, paths);
            await protocol.PersistFinalResultAsync(request, result);

            // Read back exactly what the UI would read.
            var persisted = new ActionResultCodec().Parse(
                await store.ReadArtifactAsync(RequestId, ActionArtifactKind.Result), request, paths);

            // A completed mutation must not be downgraded into a failure by the persistence layer.
            Assert.AreEqual(ActionResultStatus.Succeeded, persisted.Status);
            Assert.AreEqual(0, persisted.ExitCode);
            Assert.AreEqual(request.ManagedCatalogRevision, persisted.ManagedCatalogRevision);
            var outcome = persisted.Packages.Single();
            Assert.AreEqual(PackageOutcomeStatus.Succeeded, outcome.Status);
            Assert.IsTrue(outcome.Verified);
            CollectionAssert.DoesNotContain(outcome.Arguments.ToArray(), "--silent");
            CollectionAssert.Contains(outcome.Arguments.ToArray(), "--disable-interactivity");
        }
        finally { Directory.Delete(root, true); }
    }

    private static readonly string[] BaseVector =
    [
        "install", "--id", "Vendor.One", "--exact", "--source", "winget",
        "--accept-package-agreements", "--accept-source-agreements"
    ];

    private static JsonObject PackageWithTail(IReadOnlyList<string> tail)
    {
        var package = PackageJson("Succeeded", 0, true);
        package["Arguments"] = new JsonArray([.. BaseVector.Concat(tail).Select(value => (JsonNode)value!)]);
        return package;
    }

    [TestMethod]
    public async Task WorkerFileProtocolAppendsProgressAndWritesFinalResultExactlyOnce()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var request = Request(["Vendor.One"]);
            var store = new ActionProtocolStore(root);
            var paths = await store.PersistRequestAsync(Authorized(request));
            await using var protocol = new ActionWorkerFileProtocol(root, RequestId);
            await protocol.InitializeAsync(request);
            await protocol.AppendProgressAsync(request, ProgressRecord());
            await protocol.AppendProgressAsync(request, new ActionProgressRecord(Timestamp.AddSeconds(1), ActionProgressLevel.Success, "Verified", "Vendor.One", "Verified install."));
            var result = new ActionResultCodec().Parse(ResultJson(paths, "Succeeded", 0, PackageJson("Succeeded", 0, true)), request, paths);
            await protocol.PersistFinalResultAsync(request, result);

            var progress = new ActionProgressCodec().ParseIncremental(
                await store.ReadArtifactAsync(RequestId, ActionArtifactKind.Progress), request, flush: true);
            Assert.HasCount(2, progress.Records);
            Assert.IsEmpty(progress.Issues);
            Assert.AreEqual(ActionResultStatus.Succeeded,
                new ActionResultCodec().Parse(await store.ReadArtifactAsync(RequestId, ActionArtifactKind.Result), request, paths).Status);
            await Assert.ThrowsExactlyAsync<IOException>(() => protocol.PersistFinalResultAsync(request, result).AsTask());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task WorkerFileProtocolRejectsStaleOrMismatchedArtifactsBeforeWriting()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var request = Request(["Vendor.One"]);
            var store = new ActionProtocolStore(root);
            var paths = await store.PersistRequestAsync(Authorized(request));
            File.WriteAllText(paths.ProgressPath, ProgressJson("stale") + "\n");
            await using var stale = new ActionWorkerFileProtocol(root, RequestId);
            await Assert.ThrowsExactlyAsync<IOException>(() => stale.InitializeAsync(request).AsTask());

            File.Delete(paths.ProgressPath);
            await using var mismatched = new ActionWorkerFileProtocol(root, "request-20260829-142233-deadbeef");
            await Assert.ThrowsExactlyAsync<ActionProtocolValidationException>(() => mismatched.InitializeAsync(request).AsTask());
        }
        finally { Directory.Delete(root, true); }
    }

    private static string ProgressJson(string message) => JsonSerializer.Serialize(new
    {
        Timestamp = Timestamp.ToString("o"),
        Level = "Info",
        Stage = "Starting",
        PackageId = "Vendor.One",
        Message = message
    }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static ActionProgressRecord ProgressRecord() => new(Timestamp, ActionProgressLevel.Info, "Starting", "Vendor.One", "Starting package.");

    private static byte[] ResultJson(ActionArtifactPaths paths, string status, int exitCode, JsonObject? package) =>
        Encoding.UTF8.GetBytes(ResultText(paths, status, exitCode, package));

    private static string ResultText(ActionArtifactPaths paths, string status, int exitCode, JsonObject? package) =>
        ResultNode(paths, status, exitCode, package).ToJsonString();

    private static JsonObject ResultNode(ActionArtifactPaths paths, string status, int exitCode, JsonObject? package) => new()
    {
        ["SchemaVersion"] = ActionProtocolLimits.CurrentResultSchemaVersion,
        ["GeneratedAt"] = Timestamp.ToString("o"),
        ["Computer"] = "TEST-HOST",
        ["Status"] = status,
        ["Message"] = status,
        ["ExitCode"] = exitCode,
        ["ManagedCatalogRevision"] = 0,
        ["RequestPath"] = paths.RequestPath,
        ["ProgressPath"] = paths.ProgressPath,
        ["WingetLogPath"] = paths.WinGetLogPath,
        ["Packages"] = package is null ? new JsonArray() : new JsonArray(package)
    };

    private static JsonObject PackageJson(string status, int exitCode, bool verified) => new()
    {
        ["Id"] = "Vendor.One",
        ["Name"] = "Vendor One",
        ["Action"] = "Install",
        ["Status"] = status,
        ["ExitCode"] = exitCode,
        ["Verified"] = verified,
        ["StartedAt"] = status == "Blocked" ? null : Timestamp.ToString("o"),
        ["FinishedAt"] = Timestamp.AddSeconds(1).ToString("o"),
        ["Arguments"] = new JsonArray(
            "install", "--id", "Vendor.One", "--exact", "--source", "winget",
            "--accept-package-agreements", "--accept-source-agreements")
    };

    private static ActionRequest Request(IReadOnlyList<string> ids, bool dryRun = false, long revision = 0) =>
        new(ActionRequestRules.CurrentSchemaVersion, RequestId, ManagedRequestAction.Install, ids, false, dryRun, revision);

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
        var path = Path.Combine(Path.GetTempPath(), $"awt-action-protocol-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
