using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Application.Workers;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Files;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class ActionWorkerOrchestratorTests
{
    private const string RequestId = "request-20260829-210000-89abcdef";

    [TestMethod]
    public async Task SuccessfulPackageIsRevalidatedAndVerified()
    {
        var protocol = new MemoryProtocol(Request());
        var plans = new SequencePlans(
            Plan([State("Vendor.One")]),
            Plan([State("Vendor.One")]),
            Plan([State("Vendor.One", PackageAction.None, PackageStatus.Current)]));
        var executor = new FakeExecutor(PackageExecutionResult.Success);

        var result = await Worker(plans, executor, protocol).RunAsync(Request());

        Assert.AreEqual(ActionResultStatus.Succeeded, result.Status);
        Assert.AreEqual(3, plans.ReadCount);
        Assert.AreEqual(1, executor.CallCount);
        Assert.AreEqual(PackageOutcomeStatus.Succeeded, result.Packages.Single().Status);
        Assert.IsTrue(protocol.Progress.Any(item => item.Stage == "Verified"));
        Assert.IsTrue(protocol.Progress.Any(item => item.Message == "Preparing to install 1 app."));
        Assert.IsTrue(protocol.Progress.Any(item => item.Message == "Installing Vendor.One."));
        Assert.IsTrue(protocol.Progress.Any(item => item.Message == "Installed and verified."));
        Assert.AreEqual("All selected apps were completed and verified.", protocol.Result?.Message);
    }

    [TestMethod]
    public async Task FailureAndPostActionVerificationFailureContinueButProduceFailedResult()
    {
        var request = Request(ids: ["Vendor.One", "Vendor.Two"]);
        var states = new[] { State("Vendor.One"), State("Vendor.Two") };
        var protocol = new MemoryProtocol(request);
        var plans = new SequencePlans(Plan(states), Plan(states), Plan(states), Plan(states));
        var executor = new FakeExecutor(PackageExecutionResult.Failure(17), PackageExecutionResult.Success);

        var result = await Worker(plans, executor, protocol).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Failed, result.Status);
        CollectionAssert.AreEqual(
            new[] { PackageOutcomeStatus.Failed, PackageOutcomeStatus.Unverified },
            result.Packages.Select(item => item.Status).ToArray());
        Assert.AreEqual(2, executor.CallCount);
        Assert.IsTrue(protocol.Progress.Any(item => item.Message.Contains("Exit code: 17", StringComparison.Ordinal)));
        Assert.IsTrue(protocol.Progress.Any(item => item.Message == "WinGet finished, but AVWT couldn't confirm the installed version."));
        Assert.AreEqual("2 apps couldn't be completed or verified.", protocol.Result?.Message);
    }

    // 0x8A150030 is APPINSTALLER_CLI_ERROR_EXEC_UNINSTALL_COMMAND_FAILED, the result WinGet returned
    // when an upgrade whose manifest declares UpgradeBehavior: uninstallPrevious could not remove the
    // installed version. The operator saw only the signed decimal and no installer detail.
    private const int UninstallCommandFailed = -1978335184;

    [TestMethod]
    public async Task PuttyUpgradeRunsFromTheRealCatalogThroughToPersistedFinalResult()
    {
        // The earlier protocol regression built an approved result by hand. This drives the whole
        // sequence instead: the shipped catalog supplies PuTTY's InstallerDefault mode, the worker
        // builds the vector from it, a fake executor reports success, the existing verification path
        // runs, and the real file protocol persists the result that the UI would read back.
        var root = Path.Combine(Path.GetTempPath(), $"avwt-putty-flow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var putty = new RepositoryCatalogLoader().Load(RepositoryRoot()).GetRequired("PuTTY.PuTTY");
            // The mode is the catalog's, not the test's.
            Assert.AreEqual(InstallerExecutionMode.InstallerDefault, putty.InstallerMode);
            Assert.AreEqual(PackageRisk.None, putty.Risk);

            var request = new ActionRequest(
                ActionRequestRules.CurrentSchemaVersion, RequestId, ManagedRequestAction.Update, ["PuTTY.PuTTY"], false, false);
            var upgradable = PlanState(putty, PackageStatus.UpdateAvailable, PackageAction.Update);
            var upgraded = PlanState(putty, PackageStatus.Current, PackageAction.None);

            var store = new ActionProtocolStore(root);
            var paths = await store.PersistRequestAsync(new AuthorizedActionRequest(request, [upgradable]));
            await using var protocol = new ActionWorkerFileProtocol(root, RequestId);
            var executor = new FakeExecutor(PackageExecutionResult.Success);
            // Authorize, re-authorize per package, then verify.
            var plans = new SequencePlans(Plan([upgradable]), Plan([upgradable]), Plan([upgraded]));

            var run = await new ActionWorkerOrchestrator(plans, executor, protocol, "FixtureHost", timeProvider: new FixedTimeProvider())
                .RunAsync(request);

            Assert.AreEqual(ActionResultStatus.Succeeded, run.Status);
            Assert.AreEqual(1, executor.CallCount);

            // Read back exactly what the UI consumes, through the real codec and store.
            var persisted = new ActionResultCodec().Parse(
                await store.ReadArtifactAsync(RequestId, ActionArtifactKind.Result), request, paths);
            Assert.AreEqual(ActionResultStatus.Succeeded, persisted.Status);
            Assert.AreEqual(0, persisted.ExitCode);

            var outcome = persisted.Packages.Single();
            Assert.AreEqual("PuTTY.PuTTY", outcome.Id);
            Assert.AreEqual(ManagedRequestAction.Update, outcome.Action);
            Assert.AreEqual(PackageOutcomeStatus.Succeeded, outcome.Status);
            Assert.AreEqual(0, outcome.ExitCode);
            Assert.IsTrue(outcome.Verified);
            CollectionAssert.AreEqual(new[]
            {
                "upgrade", "--id", "PuTTY.PuTTY", "--exact", "--source", "winget",
                "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"
            }, outcome.Arguments.ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    private static PackageState PlanState(PackageDefinition definition, PackageStatus status, PackageAction action) =>
        new(definition, status != PackageStatus.Missing, string.Empty, [], string.Empty,
            status == PackageStatus.UpdateAvailable, status, status.ToString(), status.ToString(), action, InventoryQuality.Complete);

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "VERSION"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    [TestMethod]
    public async Task FailedUninstallDuringUpgradeReportsWinGetDiagnosticsAndDoesNotRetry()
    {
        var request = Request(action: ManagedRequestAction.Update);
        var updatable = State("Vendor.One", PackageAction.Update, PackageStatus.UpdateAvailable);
        var protocol = new MemoryProtocol(request);
        var plans = new SequencePlans(Plan([updatable]), Plan([updatable]));
        var executor = new FakeExecutor(PackageExecutionResult.Failure(UninstallCommandFailed) with
        {
            StandardError = "Uninstall failed with exit code: 1603\r\n  Installer failed with exit code: 1603",
            StandardOutput = "Starting package uninstall...\r\n"
        });

        var result = await Worker(plans, executor, protocol).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Failed, result.Status);
        Assert.AreEqual(PackageOutcomeStatus.Failed, result.Packages.Single().Status);
        Assert.AreEqual(UninstallCommandFailed, result.Packages.Single().ExitCode);
        // One execution only: an uninstall may already have changed the system, so nothing is repeated.
        Assert.AreEqual(1, executor.CallCount);

        var failure = protocol.Progress.Single(item => item.Stage == "Failed");
        Assert.Contains("Exit code: -1978335184", failure.Message);
        Assert.Contains("0x8A150030", failure.Message);
        Assert.Contains("Uninstall failed with exit code: 1603", failure.Message);
        Assert.Contains("Starting package uninstall...", failure.Message);
    }

    [TestMethod]
    public async Task FailureDiagnosticsAreRedactedBoundedAndProtocolSerializable()
    {
        var request = Request(action: ManagedRequestAction.Update);
        var updatable = State("Vendor.One", PackageAction.Update, PackageStatus.UpdateAvailable);
        var protocol = new MemoryProtocol(request);
        var executor = new FakeExecutor(PackageExecutionResult.Failure(UninstallCommandFailed) with
        {
            StandardError = "password: hunter2 Authorization: Bearer abc.def\r\n" + new string('X', 500_000),
            StandardOutput = new string('Y', 500_000)
        });

        await Worker(new SequencePlans(Plan([updatable]), Plan([updatable])), executor, protocol).RunAsync(request);
        var failure = protocol.Progress.Single(item => item.Stage == "Failed");

        Assert.DoesNotContain("hunter2", failure.Message);
        Assert.DoesNotContain("abc.def", failure.Message);
        Assert.IsLessThanOrEqualTo(ActionProtocolLimits.MaximumMessageCharacters, failure.Message.Length);
        // DiagnosticsRedactor keeps tab/CR/LF, but the codec rejects any control character outright,
        // so an unfolded excerpt would have thrown inside the worker instead of reporting the failure.
        Assert.IsFalse(failure.Message.Any(char.IsControl), "the progress message still carries a control character");
        // The real codec, not the in-memory fake, is what the worker writes through.
        var payload = new ActionProgressCodec().Serialize(failure, request);
        Assert.IsLessThanOrEqualTo(ActionProtocolLimits.MaximumProgressRecordBytes, payload.Length);
    }

    [TestMethod]
    public async Task PartiallyCompletedUpgradeIsNeverReportedAsSucceeded()
    {
        var updatable = State("Vendor.One", PackageAction.Update, PackageStatus.UpdateAvailable);
        foreach (var (verificationPlan, expected) in new[]
        {
            // WinGet exited 0, but the old version was removed and the new one never registered.
            (Plan([State("Vendor.One", PackageAction.Update, PackageStatus.Missing)]), PackageOutcomeStatus.Unverified),
            // WinGet exited 0, but the package is still upgradable, so the upgrade did not take effect.
            (Plan([updatable]), PackageOutcomeStatus.Unverified),
            // Only a fresh plan showing it installed and no longer upgradable counts as a success.
            (Plan([State("Vendor.One", PackageAction.None, PackageStatus.Current)]), PackageOutcomeStatus.Succeeded)
        })
        {
            var request = Request(action: ManagedRequestAction.Update);
            var protocol = new MemoryProtocol(request);
            var plans = new SequencePlans(Plan([updatable]), Plan([updatable]), verificationPlan);
            var result = await Worker(plans, new FakeExecutor(PackageExecutionResult.Success), protocol).RunAsync(request);

            Assert.AreEqual(expected, result.Packages.Single().Status);
            Assert.AreEqual(expected == PackageOutcomeStatus.Succeeded, result.Packages.Single().Verified);
        }
    }

    [TestMethod]
    public async Task TimeoutFailsWithoutPostActionVerification()
    {
        var request = Request();
        var plans = new SequencePlans(Plan([State("Vendor.One")]), Plan([State("Vendor.One")]));
        var result = await Worker(plans, new FakeExecutor(PackageExecutionResult.Timeout), new MemoryProtocol(request)).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Failed, result.Status);
        Assert.AreEqual(PackageOutcomeStatus.Failed, result.Packages.Single().Status);
        Assert.AreEqual(-1, result.Packages.Single().ExitCode);
        Assert.AreEqual(2, plans.ReadCount);
    }

    [TestMethod]
    public async Task UpdateSuccessRequiresFreshCompleteNoUpdateEvidence()
    {
        var request = Request(action: ManagedRequestAction.Update);
        var update = State("Vendor.One", PackageAction.Update, PackageStatus.UpdateAvailable);
        var current = State("Vendor.One", PackageAction.None, PackageStatus.Current);
        var result = await Worker(
            new SequencePlans(Plan([update]), Plan([update]), Plan([current])),
            new FakeExecutor(PackageExecutionResult.Success),
            new MemoryProtocol(request)).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Succeeded, result.Status);
        Assert.AreEqual(PackageOutcomeStatus.Succeeded, result.Packages.Single().Status);
    }

    [TestMethod]
    public async Task UpdateExitZeroFailsClosedWhenFreshUpdateEvidenceIsUnavailable()
    {
        var request = Request(action: ManagedRequestAction.Update);
        var update = State("Vendor.One", PackageAction.Update, PackageStatus.UpdateAvailable);
        var apparentlyCurrent = State("Vendor.One", PackageAction.None, PackageStatus.Current);
        var result = await Worker(
            new SequencePlans(Plan([update]), Plan([update]), Plan([apparentlyCurrent], updateQuality: ProviderQuality.Unavailable)),
            new FakeExecutor(PackageExecutionResult.Success),
            new MemoryProtocol(request)).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Failed, result.Status);
        Assert.AreEqual(PackageOutcomeStatus.Unverified, result.Packages.Single().Status);
    }

    [TestMethod]
    public async Task BecameCurrentHeldOrActionMismatchBlocksWithoutExecution()
    {
        foreach (var changed in new[]
        {
            State("Vendor.One", PackageAction.None, PackageStatus.Current),
            State("Vendor.One", PackageAction.Update, PackageStatus.Held),
            State("Vendor.One", PackageAction.Update, PackageStatus.UpdateAvailable)
        })
        {
            var request = Request();
            var protocol = new MemoryProtocol(request);
            var executor = new FakeExecutor();
            var result = await Worker(new SequencePlans(Plan([State("Vendor.One")]), Plan([changed])), executor, protocol).RunAsync(request);

            Assert.AreEqual(ActionResultStatus.Blocked, result.Status);
            Assert.AreEqual(PackageOutcomeStatus.Blocked, result.Packages.Single().Status);
            Assert.AreEqual(0, executor.CallCount);
        }
    }

    [TestMethod]
    public async Task RiskChangeOrPendingRebootBlocksFreshPackage()
    {
        foreach (var freshPlan in new[]
        {
            Plan([State("Vendor.One", risk: PackageRisk.Service)]),
            Plan([State("Vendor.One", risk: PackageRisk.Service)], pending: true)
        })
        {
            var request = Request(riskAcknowledged: false);
            var protocol = new MemoryProtocol(request);
            var executor = new FakeExecutor();
            var result = await Worker(new SequencePlans(Plan([State("Vendor.One")]), freshPlan), executor, protocol).RunAsync(request);

            Assert.AreEqual(ActionResultStatus.Blocked, result.Status);
            Assert.AreEqual(0, executor.CallCount);
        }
    }

    [TestMethod]
    public async Task RebootBecomingPendingBlocksAcknowledgedRiskPackage()
    {
        var risky = State("Vendor.One", risk: PackageRisk.Service);
        var request = Request(riskAcknowledged: true);
        var protocol = new MemoryProtocol(request);
        var executor = new FakeExecutor();

        var result = await Worker(
            new SequencePlans(Plan([risky]), Plan([risky], pending: true)), executor, protocol).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Blocked, result.Status);
        Assert.AreEqual(0, executor.CallCount);
    }

    [TestMethod]
    public async Task EarlierExecutionCanChangeLaterEligibility()
    {
        var request = Request(ids: ["Vendor.One", "Vendor.Two"]);
        var both = new[] { State("Vendor.One"), State("Vendor.Two") };
        var protocol = new MemoryProtocol(request);
        var executor = new FakeExecutor(PackageExecutionResult.Success);
        var plans = new SequencePlans(
            Plan(both),
            Plan(both),
            Plan([State("Vendor.One", PackageAction.None, PackageStatus.Current), State("Vendor.Two")]),
            Plan([State("Vendor.One"), State("Vendor.Two", PackageAction.None, PackageStatus.Current)]));

        var result = await Worker(plans, executor, protocol).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Blocked, result.Status);
        CollectionAssert.AreEqual(
            new[] { PackageOutcomeStatus.Succeeded, PackageOutcomeStatus.Blocked },
            result.Packages.Select(item => item.Status).ToArray());
        Assert.AreEqual(1, executor.CallCount);
    }

    [TestMethod]
    public async Task MissingFreshPackageBlocksAndInitialMissingPackageRejects()
    {
        var request = Request();
        var freshMissingProtocol = new MemoryProtocol(request);
        var freshMissing = await Worker(
            new SequencePlans(Plan([State("Vendor.One")]), Plan([])), new FakeExecutor(), freshMissingProtocol).RunAsync(request);
        Assert.AreEqual(ActionResultStatus.Blocked, freshMissing.Status);

        var initialMissingProtocol = new MemoryProtocol(request);
        var initialMissing = await Worker(
            new SequencePlans(Plan([])), new FakeExecutor(), initialMissingProtocol).RunAsync(request);
        Assert.AreEqual(ActionResultStatus.Rejected, initialMissing.Status);
        Assert.IsEmpty(initialMissing.Packages);
    }

    [TestMethod]
    public async Task CancellationIsObservedOnlyAtPackageBoundary()
    {
        var request = Request(ids: ["Vendor.One", "Vendor.Two"]);
        var states = new[] { State("Vendor.One"), State("Vendor.Two") };
        var protocol = new MemoryProtocol(request);
        var executor = new FakeExecutor(PackageExecutionResult.Success, PackageExecutionResult.Success)
        {
            AfterCall = _ => protocol.CancellationRequested = true
        };

        var result = await Worker(new SequencePlans(
            Plan(states),
            Plan(states),
            Plan([State("Vendor.One", PackageAction.None, PackageStatus.Current), State("Vendor.Two")])), executor, protocol).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Cancelled, result.Status);
        Assert.HasCount(1, result.Packages);
        Assert.AreEqual(1, executor.CallCount);
        Assert.IsTrue(protocol.Progress.Any(item => item.Stage == "Cancelled"));
    }

    [TestMethod]
    public async Task EveryTerminalOutcomeReportsTheIndependentlyVerifiedCatalogRevision()
    {
        const long revision = 12;

        var dryRunRequest = Request(dryRun: true, revision: revision);
        var dryRunProtocol = new MemoryProtocol(dryRunRequest);
        await Worker(new SequencePlans(Plan([State("Vendor.One")]), Plan([State("Vendor.One")]))
            { ManagedCatalogRevision = revision }, new FakeExecutor(), dryRunProtocol).RunAsync(dryRunRequest);
        Assert.AreEqual(revision, dryRunProtocol.Result!.ManagedCatalogRevision);

        var failedRequest = Request(revision: revision);
        var failedProtocol = new MemoryProtocol(failedRequest);
        await Worker(new SequencePlans(Plan([State("Vendor.One")]), Plan([State("Vendor.One")]))
            { ManagedCatalogRevision = revision }, new FakeExecutor(PackageExecutionResult.Failure(17)), failedProtocol)
            .RunAsync(failedRequest);
        Assert.AreEqual(ActionResultStatus.Failed, failedProtocol.Result!.Status);
        Assert.AreEqual(revision, failedProtocol.Result.ManagedCatalogRevision);

        var cancelledRequest = Request(revision: revision);
        var cancelledProtocol = new MemoryProtocol(cancelledRequest) { CancellationRequested = true };
        await Worker(new SequencePlans(Plan([State("Vendor.One")])) { ManagedCatalogRevision = revision },
            new FakeExecutor(), cancelledProtocol).RunAsync(cancelledRequest);
        Assert.AreEqual(ActionResultStatus.Cancelled, cancelledProtocol.Result!.Status);
        Assert.AreEqual(revision, cancelledProtocol.Result.ManagedCatalogRevision);

        var partialRequest = Request(ids: ["Vendor.One", "Vendor.Two"], revision: revision);
        var both = new[] { State("Vendor.One"), State("Vendor.Two") };
        var partialProtocol = new MemoryProtocol(partialRequest);
        await Worker(new SequencePlans(
                Plan(both),
                Plan(both),
                Plan([State("Vendor.One", PackageAction.None, PackageStatus.Current), State("Vendor.Two")]),
                Plan([State("Vendor.One", PackageAction.None, PackageStatus.Current)]))
            { ManagedCatalogRevision = revision }, new FakeExecutor(PackageExecutionResult.Success), partialProtocol)
            .RunAsync(partialRequest);
        Assert.AreEqual(ActionResultStatus.Blocked, partialProtocol.Result!.Status);
        CollectionAssert.AreEqual(new[] { PackageOutcomeStatus.Succeeded, PackageOutcomeStatus.Blocked },
            partialProtocol.Result.Packages.Select(item => item.Status).ToArray());
        Assert.AreEqual(revision, partialProtocol.Result.ManagedCatalogRevision);
    }

    private static ActionWorkerOrchestrator Worker(
        IActionWorkerPlanProvider plans,
        IPackageActionExecutor executor,
        IActionWorkerProtocol protocol) =>
        new(plans, executor, protocol, "FixtureHost", timeProvider: new FixedTimeProvider());

    private static ActionRequest Request(
        IReadOnlyList<string>? ids = null,
        bool riskAcknowledged = false,
        ManagedRequestAction action = ManagedRequestAction.Install,
        bool dryRun = false,
        long revision = 0) =>
        new(ActionRequestRules.CurrentSchemaVersion, RequestId, action, ids ?? ["Vendor.One"], riskAcknowledged, dryRun, revision);

    private static PackageState State(
        string id,
        PackageAction action = PackageAction.Install,
        PackageStatus status = PackageStatus.Missing,
        PackageRisk risk = PackageRisk.None)
    {
        var definition = new PackageDefinition(
            id, id, "Fixture", string.Empty, "Test", ProviderKind.WinGet, CatalogAuthority.ManagedWinGet,
            PackageProfile.Standard, PackagePriority.P2, risk, DeploymentPolicy.Allowlisted, MaintenancePolicy.Allowlisted,
            DeploymentClass.Managed, CatalogMaintenancePolicy.Managed, VersionRule.Latest, VersionCouplingMode.Independent,
            string.Empty, Lifecycle.Current, [], [], [], [LicensingModel.Free], ["PUBLIC-DL"], DistributionPolicy.PackageManagerOnly,
            [InstallationForm.WinGet], [SupportedOperatingSystem.Windows], DeliveryMode.None, ReleaseMode.None, DetectionMode.WinGet,
            DetectionVersionPolicy.None, "Stable", string.Empty, string.Empty, string.Empty, [], null, null, null, null, null,
            risk == PackageRisk.Driver, risk == PackageRisk.Service, risk == PackageRisk.Listener, null, string.Empty, []);
        return new(definition, status != PackageStatus.Missing, string.Empty, [], string.Empty, status == PackageStatus.UpdateAvailable,
            status, status.ToString(), status.ToString(), action, InventoryQuality.Complete);
    }

    private static WorkstationPlan Plan(
        IReadOnlyList<PackageState> packages,
        bool pending = false,
        ProviderQuality updateQuality = ProviderQuality.Complete) =>
        new(packages, new WorkstationPlanSummary(packages.Count, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            pending ? new RebootState(true, [RebootReason.WindowsUpdate], "Windows Update") : RebootState.Clear,
            new ProviderRefreshSummary(ProviderQuality.Complete, updateQuality, ProviderQuality.Complete, ProviderQuality.Complete, []));

    private sealed class SequencePlans(params WorkstationPlan[] plans) : IActionWorkerPlanProvider
    {
        public long ManagedCatalogRevision { get; init; }
        public int ReadCount { get; private set; }

        public ValueTask<WorkstationPlan> ReadFreshPlanAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadCount >= plans.Length) throw new InvalidOperationException("Test plan sequence exhausted.");
            return ValueTask.FromResult(plans[ReadCount++]);
        }
    }

    private sealed class FakeExecutor(params PackageExecutionResult[] results) : IPackageActionExecutor
    {
        public int CallCount { get; private set; }
        public Action<int>? AfterCall { get; init; }

        public ValueTask<PackageExecutionResult> ExecuteAsync(PackageExecutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CallCount >= results.Length) throw new InvalidOperationException("Test executor sequence exhausted.");
            var result = results[CallCount++];
            AfterCall?.Invoke(CallCount);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class MemoryProtocol(ActionRequest request) : IActionWorkerProtocol
    {
        public ActionArtifactPaths Paths { get; } = new(
            request.RequestId,
            $@"C:\fixture\logs\requests\{request.RequestId}.json",
            $@"C:\fixture\logs\requests\{request.RequestId}.progress.jsonl",
            $@"C:\fixture\logs\requests\{request.RequestId}.result.json",
            $@"C:\fixture\logs\requests\{request.RequestId}.cancel",
            $@"C:\fixture\logs\requests\{request.RequestId}.winget.log");
        public List<ActionProgressRecord> Progress { get; } = [];
        public bool CancellationRequested { get; set; }
        public ActionFinalResult? Result { get; private set; }

        public ValueTask InitializeAsync(ActionRequest value, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual(request.RequestId, value.RequestId);
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> IsCancellationRequestedAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CancellationRequested);

        public ValueTask AppendProgressAsync(ActionRequest value, ActionProgressRecord record, CancellationToken cancellationToken = default)
        {
            Progress.Add(record);
            return ValueTask.CompletedTask;
        }

        public ValueTask PersistFinalResultAsync(ActionRequest value, ActionFinalResult result, CancellationToken cancellationToken = default)
        {
            if (Result is not null) throw new InvalidOperationException("Duplicate final result.");
            Result = result;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 29, 21, 0, 0, TimeSpan.Zero);
    }
}
