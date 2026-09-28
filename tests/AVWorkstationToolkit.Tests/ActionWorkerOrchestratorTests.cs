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
        Assert.Contains("uninstall command", failure.Message);
        Assert.DoesNotContain("administrator approval", failure.Message);
        Assert.Contains("Uninstall failed with exit code: 1603", failure.Message);
        Assert.Contains("Starting package uninstall...", failure.Message);
    }

    [TestMethod]
    public async Task RebootRequiredToFinishIsReportedSeparatelyAfterAFreshCheck()
    {
        var request = Request(action: ManagedRequestAction.Update);
        var updatable = State("Vendor.One", PackageAction.Update, PackageStatus.UpdateAvailable);
        var current = State("Vendor.One", PackageAction.None, PackageStatus.Current);
        var protocol = new MemoryProtocol(request);
        var plans = new SequencePlans(Plan([updatable]), Plan([updatable]), Plan([current], pending: true));
        var executor = new FakeExecutor(PackageExecutionResult.Failure(Code(0x8A150109)));

        var result = await Worker(plans, executor, protocol).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Blocked, result.Status);
        Assert.AreEqual(PackageOutcomeStatus.RestartRequired, result.Packages.Single().Status);
        Assert.IsTrue(result.Packages.Single().Verified, "The worker should still reconcile the changed package before it stops.");
        Assert.AreEqual(3, plans.ReadCount);
        Assert.AreEqual(1, executor.CallCount);
        StringAssert.Contains(protocol.Progress.Single(item => item.Stage == "RestartRequired").Message,
            "Windows must restart before the installation is complete");
        Assert.AreEqual("1 app needs Windows restarted to finish.", protocol.Result!.Message);
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
    public async Task APackageThatBecameCurrentIsSettledWithoutRunningAnything()
    {
        foreach (var (action, changed) in new[]
        {
            (ManagedRequestAction.Install, State("Vendor.One", PackageAction.None, PackageStatus.Current)),
            (ManagedRequestAction.Install, State("Vendor.One", PackageAction.Update, PackageStatus.UpdateAvailable)),
            (ManagedRequestAction.Update, State("Vendor.One", PackageAction.None, PackageStatus.Current))
        })
        {
            var request = Request(action: action);
            var protocol = new MemoryProtocol(request);
            var executor = new FakeExecutor();
            var planned = action == ManagedRequestAction.Install ? State("Vendor.One") : State("Vendor.One", PackageAction.Update, PackageStatus.UpdateAvailable);
            var result = await Worker(new SequencePlans(Plan([planned]), Plan([changed])), executor, protocol).RunAsync(request);

            Assert.AreEqual(ActionResultStatus.Succeeded, result.Status, $"{action}: {changed.Status}");
            Assert.AreEqual(PackageOutcomeStatus.AlreadyCurrent, result.Packages.Single().Status);
            Assert.IsTrue(result.Packages.Single().Verified);
            Assert.AreEqual(0, executor.CallCount);
            StringAssert.Contains(protocol.Progress.Single(item => item.Stage == "AlreadyCurrent").Message, "nothing needed changing");
        }
    }

    [TestMethod]
    public async Task APackageThatIsHeldOrWasRemovedIsBlockedWithoutExecution()
    {
        foreach (var changed in new[]
        {
            State("Vendor.One", PackageAction.Update, PackageStatus.Held, upgradeAvailable: true),
            State("Vendor.One", PackageAction.Install, PackageStatus.Missing)
        })
        {
            var request = Request(action: ManagedRequestAction.Update);
            var protocol = new MemoryProtocol(request);
            var executor = new FakeExecutor();
            var result = await Worker(new SequencePlans(Plan([State("Vendor.One", PackageAction.Update, PackageStatus.UpdateAvailable)]), Plan([changed])),
                executor, protocol).RunAsync(request);

            Assert.AreEqual(ActionResultStatus.Blocked, result.Status, changed.Status.ToString());
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

        // Installing the first also installed the second, so the second is settled by its recheck instead of blocking.
        Assert.AreEqual(ActionResultStatus.Succeeded, result.Status);
        CollectionAssert.AreEqual(
            new[] { PackageOutcomeStatus.Succeeded, PackageOutcomeStatus.AlreadyCurrent },
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
        CollectionAssert.AreEqual(new[] { PackageOutcomeStatus.Succeeded, PackageOutcomeStatus.NotStarted },
            result.Packages.Select(item => item.Status).ToArray(), "The package the run never reached stays in the result.");
        Assert.AreEqual(1, executor.CallCount);
        Assert.IsTrue(protocol.Progress.Any(item => item.Stage == "Cancelled"));
        StringAssert.Contains(protocol.Result!.Message, "1 app wasn't started.");
    }

    [TestMethod]
    public async Task ARestartBecomingPendingSkipsOnlyTheRiskBearingPackageAndTheRunContinues()
    {
        var risky = State("Vendor.A", risk: PackageRisk.Service);
        var plain = State("Vendor.B");
        var request = Request(ids: ["Vendor.A", "Vendor.B"], riskAcknowledged: true);
        var protocol = new MemoryProtocol(request);
        var executor = new FakeExecutor(PackageExecutionResult.Success);

        var result = await Worker(new SequencePlans(
            Plan([risky, plain]),
            Plan([risky, plain], pending: true),
            Plan([risky, plain], pending: true),
            Plan([risky, State("Vendor.B", PackageAction.None, PackageStatus.Current)], pending: true)), executor, protocol).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Blocked, result.Status);
        CollectionAssert.AreEqual(new[] { PackageOutcomeStatus.Blocked, PackageOutcomeStatus.Succeeded },
            result.Packages.Select(item => item.Status).ToArray(), "The low-risk package after the blocked one still ran.");
        Assert.AreEqual(1, executor.CallCount);
        var blocked = protocol.Progress.Single(item => item.Stage == "Blocked");
        StringAssert.Contains(blocked.Message, "Skipped Vendor.A. Windows is waiting for a restart");
        Assert.AreEqual("1 app was skipped when it was rechecked.", protocol.Result!.Message);
    }

    [TestMethod]
    public async Task AnOpenAppIsNotClosedWithoutConsentAndItsPackageIsSkipped()
    {
        var request = Request(ids: ["Vendor.One", "Vendor.Two"]);
        var protocol = new MemoryProtocol(request);
        var executor = new FakeExecutor(PackageExecutionResult.Success);
        var open = new FakeOpenApplications(new() { ["Vendor.One"] = ["Vendor App"] });

        var result = await Worker(new SequencePlans(
            Plan([State("Vendor.One"), State("Vendor.Two")]),
            Plan([State("Vendor.One"), State("Vendor.Two")]),
            Plan([State("Vendor.One"), State("Vendor.Two")]),
            Plan([State("Vendor.One"), State("Vendor.Two", PackageAction.None, PackageStatus.Current)])), executor, protocol, open).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Blocked, result.Status);
        CollectionAssert.AreEqual(new[] { PackageOutcomeStatus.InUse, PackageOutcomeStatus.Succeeded },
            result.Packages.Select(item => item.Status).ToArray());
        Assert.IsEmpty(open.Closed, "No consent means no app is asked to close.");
        Assert.AreEqual(1, executor.CallCount);
        Assert.AreEqual("Vendor App is open, so Vendor.One wasn't installed. Close it and install it again.",
            protocol.Progress.Single(item => item.Stage == "InUse").Message);
        Assert.AreEqual("1 app was open, so it wasn't changed.", protocol.Result!.Message);
    }

    [TestMethod]
    public async Task WithConsentAnOpenAppIsAskedToCloseJustBeforeItsInstallerAndReopenedAfter()
    {
        var request = new ActionRequest(ActionRequestRules.CurrentSchemaVersion, RequestId, ManagedRequestAction.Update, ["Vendor.One"],
            false, false, closeOpenAppsFor: ["Vendor.One"]);
        var protocol = new MemoryProtocol(request);
        var open = new FakeOpenApplications(new() { ["Vendor.One"] = ["Vendor App", "Vendor Helper"] }, reopens: ["Vendor App"]);
        var executor = new FakeExecutor(PackageExecutionResult.Success)
        {
            AfterCall = _ => Assert.AreEqual(0, open.LastClosure!.ReopenCount, "Apps stay closed while the installer runs.")
        };
        var updatable = State("Vendor.One", PackageAction.Update, PackageStatus.UpdateAvailable);

        var result = await Worker(new SequencePlans(Plan([updatable]), Plan([updatable]),
            Plan([State("Vendor.One", PackageAction.None, PackageStatus.Current)])), executor, protocol, open).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Succeeded, result.Status);
        CollectionAssert.AreEqual(new[] { "Vendor.One" }, open.Closed);
        Assert.AreEqual(1, open.LastClosure!.ReopenCount);
        Assert.IsTrue(open.LastClosure.Disposed);
        CollectionAssert.AreEqual(new[] { "Preflight", "Closing", "Starting", "Reopened", "Verified", "Complete" },
            protocol.Progress.Select(item => item.Stage).ToArray());
        Assert.AreEqual("Reopened Vendor App. Vendor Helper was closed and can't reopen by itself; open it again when you need it.",
            protocol.Progress.Single(item => item.Stage == "Reopened").Message);
    }

    [TestMethod]
    public async Task AnAppThatRefusesToCloseIsSkippedWithoutRunningItsInstaller()
    {
        var request = new ActionRequest(ActionRequestRules.CurrentSchemaVersion, RequestId, ManagedRequestAction.Install,
            ["Vendor.One", "Vendor.Two"], false, false, closeOpenAppsFor: ["Vendor.One"]);
        var protocol = new MemoryProtocol(request);
        var open = new FakeOpenApplications(new() { ["Vendor.One"] = ["Vendor App"] }, refuse: true);
        var executor = new FakeExecutor(PackageExecutionResult.Success);

        var result = await Worker(new SequencePlans(
            Plan([State("Vendor.One"), State("Vendor.Two")]),
            Plan([State("Vendor.One"), State("Vendor.Two")]),
            Plan([State("Vendor.One"), State("Vendor.Two")]),
            Plan([State("Vendor.One"), State("Vendor.Two", PackageAction.None, PackageStatus.Current)])), executor, protocol, open).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Blocked, result.Status);
        CollectionAssert.AreEqual(new[] { PackageOutcomeStatus.InUse, PackageOutcomeStatus.Succeeded },
            result.Packages.Select(item => item.Status).ToArray());
        Assert.AreEqual(1, executor.CallCount, "Only the second package's installer ran.");
        Assert.IsTrue(open.LastClosure!.Disposed);
        StringAssert.StartsWith(protocol.Progress.Single(item => item.Stage == "InUse").Message,
            "Vendor App didn't close when asked, so Vendor.One wasn't installed. It may have unsaved work");
    }

    [TestMethod]
    public async Task ClosedAppsAreOfferedBackEvenWhenTheInstallerThrows()
    {
        var request = new ActionRequest(ActionRequestRules.CurrentSchemaVersion, RequestId, ManagedRequestAction.Install, ["Vendor.One"],
            false, false, closeOpenAppsFor: ["Vendor.One"]);
        var open = new FakeOpenApplications(new() { ["Vendor.One"] = ["Vendor App"] }, reopens: ["Vendor App"]);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Worker(
            new SequencePlans(Plan([State("Vendor.One")]), Plan([State("Vendor.One")])), new FakeExecutor(), new MemoryProtocol(request), open)
            .RunAsync(request));

        Assert.AreEqual(1, open.LastClosure!.ReopenCount);
        Assert.IsTrue(open.LastClosure.Disposed);
    }

    [TestMethod]
    public async Task AConditionThatUsuallyClearsIsRetriedAfterAFreshRecheck()
    {
        var request = Request();
        var protocol = new MemoryProtocol(request);
        var executor = new FakeExecutor(PackageExecutionResult.Failure(Code(0x8A150102)), PackageExecutionResult.Success);

        var result = await Worker(new SequencePlans(Plan([State("Vendor.One")]), Plan([State("Vendor.One")]), Plan([State("Vendor.One")]),
            Plan([State("Vendor.One", PackageAction.None, PackageStatus.Current)])), executor, protocol).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Succeeded, result.Status);
        Assert.AreEqual(2, executor.CallCount);
        Assert.AreEqual("Vendor.One: another installation was already running on this PC, often Windows Update. Trying again in 0 seconds (try 2 of 3).",
            protocol.Progress.Single(item => item.Stage == "Retrying").Message);
    }

    [TestMethod]
    public async Task RetriesAreBoundedAndTheLastFailureIsExplained()
    {
        var request = Request();
        var protocol = new MemoryProtocol(request);
        var download = PackageExecutionResult.Failure(Code(0x8A150008));
        var executor = new FakeExecutor(download, download, download);

        var result = await Worker(new SequencePlans(Plan([State("Vendor.One")]), Plan([State("Vendor.One")]), Plan([State("Vendor.One")]),
            Plan([State("Vendor.One")])), executor, protocol).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Failed, result.Status);
        Assert.AreEqual(3, executor.CallCount, "The first try and two retries, no more.");
        StringAssert.StartsWith(protocol.Progress.Single(item => item.Stage == "Failed").Message,
            "Vendor.One couldn't be installed: WinGet couldn't download the installer. WinGet couldn't complete the change. Exit code: -1978335224 (0x8A150008).");
    }

    [TestMethod]
    public async Task ARecheckDuringARetrySettlesAPackageThatBecameCurrent()
    {
        var request = Request();
        var protocol = new MemoryProtocol(request);
        var executor = new FakeExecutor(PackageExecutionResult.Failure(Code(0x8A150008)));

        var result = await Worker(new SequencePlans(Plan([State("Vendor.One")]), Plan([State("Vendor.One")]),
            Plan([State("Vendor.One", PackageAction.None, PackageStatus.Current)])), executor, protocol).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Succeeded, result.Status);
        Assert.AreEqual(PackageOutcomeStatus.AlreadyCurrent, result.Packages.Single().Status);
        Assert.AreEqual(1, executor.CallCount);
    }

    [TestMethod]
    public async Task WinGetFindingNothingToDoIsSettledOnlyWhenAFreshCheckAgrees()
    {
        foreach (var (verification, expected) in new[]
        {
            (State("Vendor.One", PackageAction.None, PackageStatus.Current), PackageOutcomeStatus.AlreadyCurrent),
            (State("Vendor.One", PackageAction.Update, PackageStatus.UpdateAvailable), PackageOutcomeStatus.Failed)
        })
        {
            var request = Request(action: ManagedRequestAction.Update);
            var protocol = new MemoryProtocol(request);
            var updatable = State("Vendor.One", PackageAction.Update, PackageStatus.UpdateAvailable);
            var executor = new FakeExecutor(PackageExecutionResult.Failure(Code(0x8A15002B)));

            var result = await Worker(new SequencePlans(Plan([updatable]), Plan([updatable]), Plan([verification])), executor, protocol).RunAsync(request);

            Assert.AreEqual(expected, result.Packages.Single().Status);
            Assert.AreEqual(Code(0x8A15002B), result.Packages.Single().ExitCode);
            Assert.AreEqual(expected == PackageOutcomeStatus.AlreadyCurrent ? ActionResultStatus.Succeeded : ActionResultStatus.Failed, result.Status);
        }
    }

    [TestMethod]
    public async Task AFailureWhileTheAppIsOpenIsRecoveredByClosingItOnlyWithConsent()
    {
        foreach (var consent in new[] { true, false })
        {
            var request = new ActionRequest(ActionRequestRules.CurrentSchemaVersion, RequestId, ManagedRequestAction.Install, ["Vendor.One"],
                false, false, closeOpenAppsFor: consent ? ["Vendor.One"] : []);
            var protocol = new MemoryProtocol(request);
            // Not open when the package started; open by the time its installer failed (reopened, or not locatable earlier).
            var open = new SequencedOpenApplications([], ["Vendor App"]);
            var executor = new FakeExecutor(PackageExecutionResult.Failure(Code(0x8A150006)), PackageExecutionResult.Success);

            var result = await Worker(new SequencePlans(Plan([State("Vendor.One")]), Plan([State("Vendor.One")]),
                Plan([State("Vendor.One", PackageAction.None, PackageStatus.Current)])), executor, protocol, open).RunAsync(request);

            if (consent)
            {
                Assert.AreEqual(ActionResultStatus.Succeeded, result.Status);
                Assert.AreEqual(2, executor.CallCount);
                Assert.AreEqual(1, open.Closures, "Asked to close once, then tried once more.");
                Assert.AreEqual(1, open.LastClosure!.ReopenCount);
                StringAssert.Contains(protocol.Progress.Last(item => item.Stage == "Closing").Message, "which can stop the installer");
            }
            else
            {
                Assert.AreEqual(ActionResultStatus.Failed, result.Status);
                Assert.AreEqual(1, executor.CallCount);
                Assert.AreEqual(0, open.Closures);
                StringAssert.Contains(protocol.Progress.Single(item => item.Stage == "Failed").Message,
                    "Vendor App is open, which can stop its installer: close it and install it again.");
            }
        }
    }

    [TestMethod]
    public async Task AFailureThatWontClearIsExplainedAndNotRetried()
    {
        var request = Request();
        var protocol = new MemoryProtocol(request);
        var executor = new FakeExecutor(PackageExecutionResult.Failure(Code(0x8A150011)));

        var result = await Worker(new SequencePlans(Plan([State("Vendor.One")]), Plan([State("Vendor.One")])), executor, protocol).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Failed, result.Status);
        Assert.AreEqual(1, executor.CallCount);
        StringAssert.StartsWith(protocol.Progress.Single(item => item.Stage == "Failed").Message,
            "Vendor.One couldn't be installed: the download didn't match the hash published for it, so it wasn't run.");
    }

    [TestMethod]
    public async Task StoppingDuringARetryPauseEndsThePackageWithoutAnotherTry()
    {
        var request = Request(ids: ["Vendor.One", "Vendor.Two"]);
        var protocol = new MemoryProtocol(request);
        var executor = new FakeExecutor(PackageExecutionResult.Failure(Code(0x8A150008))) { AfterCall = _ => protocol.CancellationRequested = true };

        var result = await Worker(new SequencePlans(Plan([State("Vendor.One"), State("Vendor.Two")]), Plan([State("Vendor.One"), State("Vendor.Two")])),
            executor, protocol).RunAsync(request);

        Assert.AreEqual(1, executor.CallCount);
        Assert.AreEqual(ActionResultStatus.Failed, result.Status);
        CollectionAssert.AreEqual(new[] { PackageOutcomeStatus.Failed, PackageOutcomeStatus.NotStarted }, result.Packages.Select(item => item.Status).ToArray());
    }

    [TestMethod]
    public async Task APackageHeldAtTheStartIsSkippedAndTheOthersStillRun()
    {
        var request = Request(ids: ["Vendor.A", "Vendor.B"], action: ManagedRequestAction.Update);
        var held = State("Vendor.A", PackageAction.Update, PackageStatus.Held, upgradeAvailable: true);
        var updatable = State("Vendor.B", PackageAction.Update, PackageStatus.UpdateAvailable);
        var protocol = new MemoryProtocol(request);
        var executor = new FakeExecutor(PackageExecutionResult.Success);

        var result = await Worker(new SequencePlans(Plan([held, updatable]), Plan([held, updatable]), Plan([held, updatable]),
            Plan([held, State("Vendor.B", PackageAction.None, PackageStatus.Current)])), executor, protocol).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Blocked, result.Status, "A hold at the start no longer refuses the whole request.");
        CollectionAssert.AreEqual(new[] { PackageOutcomeStatus.Blocked, PackageOutcomeStatus.Succeeded }, result.Packages.Select(item => item.Status).ToArray());
        Assert.AreEqual(1, executor.CallCount);
    }

    [TestMethod]
    public void EveryRecognizedWinGetCodeIsAWinGetCodeWithAPlainReason()
    {
        Assert.IsNull(WinGetOutcomes.For(1603));
        Assert.IsNull(WinGetOutcomes.For(Code(0x8A150006)), "A generic installer failure keeps WinGet's own output.");
        foreach (var code in new uint[] { 0x8A150008, 0x8A150102, 0x8A150107, 0x8A150045 })
            Assert.AreEqual(WinGetRecovery.RetryLater, WinGetOutcomes.For(Code(code))!.Recovery, $"0x{code:X8}");
        foreach (var code in new uint[] { 0x8A150101, 0x8A150103, 0x8A150111 })
            Assert.AreEqual(WinGetRecovery.CloseOpenApps, WinGetOutcomes.For(Code(code))!.Recovery, $"0x{code:X8}");
        foreach (var code in new uint[] { 0x8A15002B, 0x8A150061, 0x8A15010D })
            Assert.AreEqual(WinGetRecovery.CheckIfCurrent, WinGetOutcomes.For(Code(code))!.Recovery, $"0x{code:X8}");
        Assert.AreEqual(WinGetRecovery.RestartRequired, WinGetOutcomes.For(Code(0x8A150109))!.Recovery);
        Assert.AreEqual(WinGetRecovery.None, WinGetOutcomes.For(Code(0x8A150011))!.Recovery, "A hash mismatch is never retried.");
    }

    private static int Code(uint value) => unchecked((int)value);

    [TestMethod]
    public async Task APlanReadAfterTheLastInstallerIsReusedAndNeverOneFromBeforeIt()
    {
        var ids = new[] { "Vendor.A", "Vendor.B", "Vendor.C" };
        var workstation = new LiveWorkstation(ids);
        var request = Request(ids: ids);
        var protocol = new MemoryProtocol(request);

        var result = await new ActionWorkerOrchestrator(new AVWorkstationToolkit.Infrastructure.Windows.Processes.ReusingWorkerPlanProvider(workstation, 0),
            workstation, protocol, "FixtureHost", timeProvider: new FixedTimeProvider()).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Succeeded, result.Status);
        Assert.IsTrue(result.Packages.All(item => item.Status == PackageOutcomeStatus.Succeeded && item.Verified),
            "Each verification saw its own installer's effect, so no plan from before an installer was reused.");
        Assert.AreEqual(3, workstation.Installs);
        // One read to start and one after each installer; each recheck reuses the read taken after the previous change.
        Assert.AreEqual(1 + ids.Length, workstation.Refreshes, "Before reuse this run read the plan seven times.");
    }

    [TestMethod]
    public async Task EveryInstallerRunInvalidatesThePlanFirst()
    {
        var request = Request();
        var protocol = new MemoryProtocol(request);
        var plans = new SequencePlans(Plan([State("Vendor.One")]), Plan([State("Vendor.One")]), Plan([State("Vendor.One")]),
            Plan([State("Vendor.One", PackageAction.None, PackageStatus.Current)]));
        var executor = new FakeExecutor(PackageExecutionResult.Failure(Code(0x8A150102)), PackageExecutionResult.Success)
        {
            AfterCall = call => Assert.AreEqual(call, plans.ChangeSignals, "The plan was invalidated before this installer ran.")
        };

        var result = await Worker(plans, executor, protocol).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Succeeded, result.Status);
        Assert.AreEqual(2, plans.ChangeSignals);
    }

    [TestMethod]
    public async Task WinGetsRestartNoticeAfterASuccessfulInstallIsReportedAsRestartRequired()
    {
        var request = Request();
        var protocol = new MemoryProtocol(request);
        var plans = new SequencePlans(Plan([State("Vendor.One")]), Plan([State("Vendor.One")]),
            Plan([State("Vendor.One", PackageAction.None, PackageStatus.Current)]));
        // WinGet exits 0 for an installer's "restart required to finish" (MSI 3010) and only prints this notice.
        var executor = new FakeExecutor(PackageExecutionResult.Success with
        {
            StandardOutput = "Successfully installed. Restart your PC to finish installation."
        });

        var result = await Worker(plans, executor, protocol).RunAsync(request);

        var package = result.Packages.Single();
        Assert.AreEqual(PackageOutcomeStatus.RestartRequired, package.Status);
        Assert.AreEqual(0, package.ExitCode);
        Assert.IsTrue(package.Verified);
        Assert.AreEqual(ActionResultStatus.Blocked, result.Status, "Not complete until Windows restarts.");
        StringAssert.Contains(protocol.Progress.Single(item => item.Stage == "RestartRequired").Message, "Windows must restart");
        Assert.AreEqual("1 app needs Windows restarted to finish.", protocol.Result!.Message);
        Assert.IsFalse(WinGetOutcomes.ReportsRestartToFinish(PackageExecutionResult.Success with { StandardOutput = "Successfully installed" }));
    }

    [TestMethod]
    public async Task ATestRunNeverLooksForOrClosesApps()
    {
        var request = Request(dryRun: true);
        var open = new FakeOpenApplications(new() { ["Vendor.One"] = ["Vendor App"] });

        var result = await Worker(new SequencePlans(Plan([State("Vendor.One")]), Plan([State("Vendor.One")])), new FakeExecutor(),
            new MemoryProtocol(request), open).RunAsync(request);

        Assert.AreEqual(ActionResultStatus.Succeeded, result.Status);
        Assert.IsEmpty(open.Found);
        Assert.IsEmpty(open.Closed);
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
        IActionWorkerProtocol protocol,
        IOpenApplicationService? openApplications = null) =>
        new(plans, executor, protocol, "FixtureHost", timeProvider: new FixedTimeProvider(), openApplications: openApplications,
            retryPolicy: new WorkerRetryPolicy([TimeSpan.Zero, TimeSpan.Zero]));

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
        PackageRisk risk = PackageRisk.None,
        bool? upgradeAvailable = null)
    {
        var definition = new PackageDefinition(
            id, id, "Fixture", string.Empty, "Test", ProviderKind.WinGet, CatalogAuthority.ManagedWinGet,
            PackageProfile.Standard, PackagePriority.P2, risk, DeploymentPolicy.Allowlisted, MaintenancePolicy.Allowlisted,
            DeploymentClass.Managed, CatalogMaintenancePolicy.Managed, VersionRule.Latest, VersionCouplingMode.Independent,
            string.Empty, Lifecycle.Current, [], [], [], [LicensingModel.Free], ["PUBLIC-DL"], DistributionPolicy.PackageManagerOnly,
            [InstallationForm.WinGet], [SupportedOperatingSystem.Windows], DeliveryMode.None, ReleaseMode.None, DetectionMode.WinGet,
            DetectionVersionPolicy.None, "Stable", string.Empty, string.Empty, string.Empty, [], null, null, null, null, null,
            risk == PackageRisk.Driver, risk == PackageRisk.Service, risk == PackageRisk.Listener, null, string.Empty, []);
        return new(definition, status != PackageStatus.Missing, string.Empty, [], string.Empty, upgradeAvailable ?? status == PackageStatus.UpdateAvailable,
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
        public int ChangeSignals { get; private set; }

        public ValueTask<WorkstationPlan> ReadFreshPlanAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadCount >= plans.Length) throw new InvalidOperationException("Test plan sequence exhausted.");
            return ValueTask.FromResult(plans[ReadCount++]);
        }

        public void StateMayChange() => ChangeSignals++;
    }

    // A workstation whose plan follows what has been installed on it, for runs through the production plan provider.
    private sealed class LiveWorkstation(params string[] ids) : IWorkstationPlanningCoordinator, IPackageActionExecutor
    {
        private readonly HashSet<string> installed = new(StringComparer.OrdinalIgnoreCase);
        public int Refreshes { get; private set; }
        public int Installs { get; private set; }

        public Task<WorkstationPlan> RefreshAsync(IProgress<PlanningRefreshStage>? progress = null, CancellationToken cancellationToken = default)
        {
            Refreshes++;
            return Task.FromResult(Plan(ids.Select(id => installed.Contains(id)
                ? State(id, PackageAction.None, PackageStatus.Current)
                : State(id)).ToArray()));
        }

        public ValueTask<PackageExecutionResult> ExecuteAsync(PackageExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Installs++;
            installed.Add(request.Id);
            return ValueTask.FromResult(PackageExecutionResult.Success);
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
            // Every result the worker produces must also pass the strict result codec the app reads it with.
            _ = new ActionResultCodec().Serialize(result, request, Paths);
            Result = result;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeOpenApplications(Dictionary<string, string[]> open, bool refuse = false, string[]? reopens = null) : IOpenApplicationService
    {
        public List<string> Found { get; } = [];
        public List<string> Closed { get; } = [];
        public FakeClosure? LastClosure { get; private set; }

        public IReadOnlyList<string> FindOpen(string packageId)
        {
            Found.Add(packageId);
            return open.TryGetValue(packageId, out var names) ? names : [];
        }

        public IOpenApplicationClosure Close(string packageId)
        {
            Closed.Add(packageId);
            var names = open[packageId];
            LastClosure = refuse ? new FakeClosure([], names, []) : new FakeClosure(names, [], reopens ?? []);
            return LastClosure;
        }
    }

    // Each FindOpen returns the next answer (repeating the last); every Close closes everything and reopens nothing.
    private sealed class SequencedOpenApplications(params string[][] answers) : IOpenApplicationService
    {
        private int calls;
        public int Closures { get; private set; }
        public FakeClosure? LastClosure { get; private set; }

        public IReadOnlyList<string> FindOpen(string packageId) => answers[Math.Min(calls++, answers.Length - 1)];

        public IOpenApplicationClosure Close(string packageId)
        {
            Closures++;
            LastClosure = new FakeClosure(answers[^1], [], []);
            return LastClosure;
        }
    }

    private sealed class FakeClosure(IReadOnlyList<string> closed, IReadOnlyList<string> stillOpen, IReadOnlyList<string> reopens) : IOpenApplicationClosure
    {
        public IReadOnlyList<string> Closed { get; } = closed;
        public IReadOnlyList<string> StillOpen { get; } = stillOpen;
        public int ReopenCount { get; private set; }
        public bool Disposed { get; private set; }

        public IReadOnlyList<string> Reopen()
        {
            ReopenCount++;
            return reopens;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 29, 21, 0, 0, TimeSpan.Zero);
    }
}
