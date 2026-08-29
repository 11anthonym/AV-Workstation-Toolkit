using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Application.Workers;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class ActionWorkerOrchestratorTests
{
    private const string RequestId = "request-20260829-210000-89abcdef";

    [TestMethod]
    public async Task SuccessfulPackageIsRevalidatedAndVerified()
    {
        var protocol = new MemoryProtocol(Request());
        var plans = new SequencePlans(Plan([State("Vendor.One")]), Plan([State("Vendor.One")]));
        var executor = new FakeExecutor(PackageExecutionResult.Success);

        var result = await Worker(plans, executor, protocol).RunAsync(Request());

        Assert.AreEqual(ActionResultStatus.Succeeded, result.Status);
        Assert.AreEqual(2, plans.ReadCount);
        Assert.AreEqual(1, executor.CallCount);
        Assert.AreEqual(PackageOutcomeStatus.Succeeded, result.Packages.Single().Status);
        Assert.IsTrue(protocol.Progress.Any(item => item.Stage == "Verified"));
    }

    [TestMethod]
    public async Task FailureAndVerificationFailureContinueButProduceFailedResult()
    {
        var request = Request(ids: ["Vendor.One", "Vendor.Two"]);
        var states = new[] { State("Vendor.One"), State("Vendor.Two") };
        var protocol = new MemoryProtocol(request);
        var plans = new SequencePlans(Plan(states), Plan(states), Plan(states));
        var executor = new FakeExecutor(PackageExecutionResult.Failure(17), PackageExecutionResult.VerificationFailure);

        var result = await Worker(plans, executor, protocol).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Failed, result.Status);
        CollectionAssert.AreEqual(
            new[] { PackageOutcomeStatus.Failed, PackageOutcomeStatus.Unverified },
            result.Packages.Select(item => item.Status).ToArray());
        Assert.AreEqual(2, executor.CallCount);
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

        var result = await Worker(new SequencePlans(Plan(states), Plan(states)), executor, protocol).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Cancelled, result.Status);
        Assert.HasCount(1, result.Packages);
        Assert.AreEqual(1, executor.CallCount);
        Assert.IsTrue(protocol.Progress.Any(item => item.Stage == "Cancelled"));
    }

    private static ActionWorkerOrchestrator Worker(
        IActionWorkerPlanProvider plans,
        IPackageActionExecutor executor,
        IActionWorkerProtocol protocol) =>
        new(plans, executor, protocol, "FixtureHost", timeProvider: new FixedTimeProvider());

    private static ActionRequest Request(
        IReadOnlyList<string>? ids = null,
        bool riskAcknowledged = false) =>
        new(1, RequestId, ManagedRequestAction.Install, ids ?? ["Vendor.One"], riskAcknowledged, false);

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

    private static WorkstationPlan Plan(IReadOnlyList<PackageState> packages, bool pending = false) =>
        new(packages, new WorkstationPlanSummary(packages.Count, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            pending ? new RebootState(true, [RebootReason.WindowsUpdate], "Windows Update") : RebootState.Clear,
            new ProviderRefreshSummary(ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, []));

    private sealed class SequencePlans(params WorkstationPlan[] plans) : IActionWorkerPlanProvider
    {
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
