using System.Text;
using AVWorkstationToolkit.App.ViewModels;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Application.Workstation;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Workstation;

namespace AVWorkstationToolkit.Tests;

/// <summary>
/// Every asynchronous scan and install path can fail. Afterward the window is never left busy or installing, its
/// controls work again, the status says what happened, and nothing is counted complete without a scan that found it.
/// </summary>
[TestClass]
public sealed class MigrationFailureStateTests
{
    [TestMethod]
    public async Task AFailedScanReleasesTheWindowAndKeepsTheChecklistUncompared()
    {
        var (viewModel, service, _) = Create(new MigrationPresentationTests.FailingPlanning());
        service.StartFromInventory(WorkstationMigrationTests.Exported(WorkstationMigrationTests.SourceInventory()));

        await viewModel.InitializeAsync(null);

        AssertUsable(viewModel, service, "Couldn't scan this PC");
        Assert.IsTrue(viewModel.HasSession, "The saved checklist is kept.");
        Assert.IsNull(service.Checklist);
        Assert.AreEqual("Scan this PC to compare", viewModel.RemainingHeadline);
        viewModel.Filter = MigrationFilter.Completed;
        Assert.IsEmpty(viewModel.VisibleItems);
    }

    [TestMethod]
    public async Task AnIncompleteInventoryCompletesNothingItCouldNotSee()
    {
        var partial = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult(), registryQuality: ProviderQuality.Partial);
        var (viewModel, service, _) = Create(new WorkstationMigrationTests.FixedPlanning(partial.Plan));
        service.StartFromInventory(WorkstationMigrationTests.Exported(WorkstationMigrationTests.SourceInventory()));

        await viewModel.InitializeAsync(null);

        AssertUsable(viewModel, service, "Scan finished with gaps");
        Assert.IsGreaterThan(0, service.Checklist!.Summary.CheckUnavailable, "What the partial scan couldn't see is shown as unchecked.");
        Assert.IsFalse(viewModel.CompleteVisible);
        viewModel.Filter = MigrationFilter.NeedsAttention;
        Assert.IsNotEmpty(viewModel.VisibleItems);
    }


    [TestMethod]
    [DataRow("worker success, detected")]
    [DataRow("worker success, not detected")]
    [DataRow("worker success, rescan failed")]
    [DataRow("worker success, refresh failed")]
    [DataRow("worker failure")]
    [DataRow("worker refusal")]
    [DataRow("attempt not saved")]
    public async Task EveryInstallOutcomeKeepsTheWorkerResultApartFromTheScan(string path)
    {
        var before = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult());
        var after = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult(("7zip.7zip", "26.03")));
        IWorkstationPlanningCoordinator planning = path switch
        {
            "worker success, detected" or "attempt not saved" => new WorkstationMigrationTests.FixedPlanning(after.Plan),
            // The coordinator's refresh carries no inventory, and this PC then can't be scanned.
            "worker success, rescan failed" => new ScriptedPlanning(() => after.Plan with { Evidence = null }, () => throw new InvalidOperationException("WinGet could not be started.")),
            // The coordinator's own refresh after the worker fails.
            "worker success, refresh failed" => new ScriptedPlanning(() => throw new InvalidOperationException("WinGet could not be started.")),
            _ => new WorkstationMigrationTests.FixedPlanning(before.Plan)
        };
        var workerOutcome = path switch { "worker failure" => ActionResultStatus.Failed, "worker refusal" => ActionResultStatus.Blocked, _ => ActionResultStatus.Succeeded };
        var store = new FailingStore();
        var (viewModel, service, _) = Create(planning, new WorkstationMigrationTests.RecordingActionStore(workerOutcome), store);
        service.StartFromInventory(WorkstationMigrationTests.Exported(WorkstationMigrationTests.SourceInventory()));
        await viewModel.InitializeAsync(before.Plan);
        store.FailSaves = path == "attempt not saved";

        await viewModel.InstallAsync([SevenZip(viewModel)]);

        var (status, statusText, attemptSucceeded, attemptText) = path switch
        {
            "worker success, detected" => (ChecklistStatus.Installed, "1 of 1 now detected", true, "verified by the worker"),
            "worker success, not detected" => (ChecklistStatus.InstallUnverified, "0 of 1 now detected", true, "verified by the worker"),
            "worker success, rescan failed" or "worker success, refresh failed" =>
                (ChecklistStatus.InstallUnverified, "Install finished; verification failed", true, "verified by the worker"),
            "worker failure" => (ChecklistStatus.InstallFailed, "0 of 1 now detected", false, "exit code 1603"),
            "worker refusal" => (ChecklistStatus.InstallFailed, "0 of 1 now detected", false, "Blocked by policy"),
            _ => (ChecklistStatus.Installed, "The checklist couldn't be saved", true, "verified by the worker")
        };
        AssertUsable(viewModel, service, statusText);
        StringAssert.Contains(viewModel.Status, "Installer result:", "The worker's own result is always reported.");
        var item = service.Checklist!.Items.Single(entry => entry.CatalogState?.Package.Id == "7zip.7zip");
        Assert.AreEqual(status, item.Status, path);
        Assert.AreEqual(status == ChecklistStatus.Installed, item.Satisfied, "Only a scan that detects the app completes it.");
        // The attempt history keeps the worker's result whatever the scan found, even when it couldn't be saved yet.
        var attempt = service.Session!.Items.Single(entry => entry.ItemId == item.ItemId).LastAttempt!;
        Assert.AreEqual(attemptSucceeded, attempt.Succeeded);
        StringAssert.Contains(attempt.Message, attemptText);
        viewModel.Filter = MigrationFilter.All;
        var row = viewModel.VisibleItems.Single(entry => entry.ItemId == item.ItemId);
        if (status == ChecklistStatus.InstallUnverified)
        {
            Assert.AreEqual("Installed · not verified", row.StatusLabel);
            StringAssert.Contains(item.Detail, "no scan has detected");
            Assert.AreEqual(1, service.Checklist.Summary.Unverified);
            Assert.IsTrue(row.Matches(MigrationFilter.Remaining) && row.Matches(MigrationFilter.NeedsAttention));
        }
        if (status == ChecklistStatus.InstallFailed) Assert.AreEqual("Install failed", row.StatusLabel);
    }

    [TestMethod]
    public async Task TheCoordinatorKeepsTheWorkerResultWhenItsRefreshFails()
    {
        var before = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult());
        var coordinator = new CompiledActionCoordinator(new WorkstationMigrationTests.RecordingActionStore(), new WorkstationMigrationTests.ImmediateLauncher(),
            new ScriptedPlanning(() => throw new InvalidOperationException("WinGet could not be started.")),
            pollInterval: TimeSpan.FromMilliseconds(1), resultTimeout: TimeSpan.FromSeconds(10));
        var sevenZip = before.Plan.Packages.Single(state => state.Package.Id == "7zip.7zip");

        var unverified = await Assert.ThrowsAsync<CompiledActionVerificationException>(() =>
            coordinator.StartAsync(ManagedRequestAction.Install, [sevenZip], before.Plan, riskAcknowledged: false, dryRun: false));

        Assert.AreEqual(ActionResultStatus.Succeeded, unverified.Result.Status);
        Assert.IsInstanceOfType<InvalidOperationException>(unverified.InnerException);
        Assert.AreSame(unverified.Result, coordinator.Snapshot.Result, "The snapshot keeps the worker's result.");
        Assert.AreEqual(CompiledActionState.Completed, coordinator.Snapshot.State);
        StringAssert.Contains(coordinator.Snapshot.Status, "couldn't be checked afterward");
    }
    [TestMethod]
    public async Task SaveFailuresAreReportedAndChangeNothingSilently()
    {
        var before = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult());
        var after = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult(("7zip.7zip", "26.03")));
        var store = new FailingStore();
        var (viewModel, service, _) = Create(new WorkstationMigrationTests.FixedPlanning(after.Plan), new WorkstationMigrationTests.RecordingActionStore(), store);
        var started = service.StartFromInventory(WorkstationMigrationTests.Exported(WorkstationMigrationTests.SourceInventory()));
        await viewModel.InitializeAsync(before.Plan);
        store.FailSaves = true;

        // A checklist edit that can't be saved is undone on screen and reported.
        viewModel.Filter = MigrationFilter.All;
        var widget = viewModel.VisibleItems.Single(row => row.Name == "Vendor Widget Configuration Tool");
        widget.Included = false;
        AssertUsable(viewModel, service, "Couldn't update the checklist");
        Assert.IsTrue(viewModel.VisibleItems.Single(row => row.ItemId == widget.ItemId).Included);
        Assert.AreSame(started, store.Saved);

        // Applying a profile straight from the editor, outside any other error handling, keeps the current checklist.
        await viewModel.ApplyProfileAsync(WorkstationMigrationTests.JumpPc(3));
        AssertUsable(viewModel, service, "Couldn't apply the deployment profile");
        Assert.AreEqual(started.SessionId, service.Session!.SessionId);

        // An install whose attempts can't be saved still rescans, and reports the save problem.
        await viewModel.InstallAsync([SevenZip(viewModel)]);
        AssertUsable(viewModel, service, "The checklist couldn't be saved");
        StringAssert.Contains(viewModel.Status, "1 of 1 now detected");
        Assert.AreEqual(ChecklistStatus.Installed, service.Checklist!.Items.Single(entry => entry.CatalogState?.Package.Id == "7zip.7zip").Status);
        Assert.AreSame(started, store.Saved, "The file keeps its last valid checklist.");
    }

    [TestMethod]
    public async Task DamagedProfilesAreRejectedWithoutChangingTheChecklist()
    {
        var target = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult());
        var files = new MigrationPresentationTests.FakeFiles { OpenPath = @"C:\fixture\jump-pc.json" };
        var (viewModel, service, _) = Create(new WorkstationMigrationTests.FixedPlanning(target.Plan), files: files);
        var started = service.StartFromInventory(WorkstationMigrationTests.Exported(WorkstationMigrationTests.SourceInventory()));
        await viewModel.InitializeAsync(target.Plan);
        var valid = Encoding.UTF8.GetString(DeploymentProfileCodec.Serialize(WorkstationMigrationTests.JumpPc(3)));

        foreach (var damaged in new[] { valid[..(valid.Length / 2)], valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal), "{}" })
        {
            files.Files[files.OpenPath] = Encoding.UTF8.GetBytes(damaged);
            await viewModel.ApplyProfileFromFileAsync();
            AssertUsable(viewModel, service, "Couldn't apply the deployment profile");
            await viewModel.EditProfileFromFileAsync();
            AssertUsable(viewModel, service, "Couldn't open the deployment profile");
            Assert.AreEqual(started.SessionId, service.Session!.SessionId);
        }
    }

    [TestMethod]
    public async Task ConcurrentScansAndChecklistEditsLeaveOneConsistentChecklist()
    {
        var target = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult(("7zip.7zip", "26.03")));
        var store = new WorkstationMigrationTests.MemorySessionStore();
        var service = new WorkstationMigrationService(new WorkstationMigrationTests.FixedPlanning(target.Plan), MigrationFixtures.InventoryService(), store,
            timeProvider: new WorkstationMigrationTests.FixedTime(MigrationFixtures.Now));
        service.StartFromInventory(WorkstationMigrationTests.Exported(WorkstationMigrationTests.SourceInventory()));
        var ids = service.Session!.Items.Where(item => item.Included).Select(item => item.ItemId).ToArray();
        var expected = new Dictionary<string, bool>(StringComparer.Ordinal);
        const int rounds = 300;

        // Scan results land on worker threads while the technician edits the checklist and the window reads it.
        var scans = Task.Run(async () => { for (var round = 0; round < rounds; round++) await service.ScanAsync(); });
        var edits = Task.Run(() =>
        {
            for (var round = 0; round < rounds; round++)
            {
                var id = ids[round % ids.Length];
                var included = round % 3 != 0;
                service.SetIncluded(id, included);
                expected[id] = included;
            }
        });
        var reads = Task.Run(() => { for (var round = 0; round < rounds; round++) _ = service.Checklist?.Summary.Remaining; });
        await Task.WhenAll(scans, edits, reads);

        var session = service.Session!;
        Assert.AreSame(session, store.Saved, "The saved checklist is the one in use.");
        Assert.AreSame(session, service.Checklist!.Session, "The checklist reflects the latest edits.");
        foreach (var (id, included) in expected)
        {
            Assert.AreEqual(included, session.Items.Single(item => item.ItemId == id).Included, id);
            Assert.AreEqual(included, service.Checklist.Items.Single(item => item.ItemId == id).Desired.Included, id);
        }
    }

    private static MigrationItemViewModel SevenZip(MigrationViewModel viewModel)
    {
        viewModel.Filter = MigrationFilter.All;
        return viewModel.VisibleItems.Single(row => row.Item.CatalogState?.Package.Id == "7zip.7zip" && row.CanInstall);
    }

    private static void AssertUsable(MigrationViewModel viewModel, WorkstationMigrationService service, string status)
    {
        Assert.IsFalse(viewModel.IsBusy, "The window was left busy.");
        Assert.IsTrue(viewModel.IsNotBusy);
        Assert.IsTrue(viewModel.ScanCommand.CanExecute(null), "Scanning is available again.");
        Assert.IsTrue(viewModel.ImportInventoryCommand.CanExecute(null));
        Assert.IsTrue(viewModel.ApplyProfileCommand.CanExecute(null));
        StringAssert.Contains(viewModel.Status, status);
        Assert.IsFalse(service.Checklist?.Items.Any(item => item.Status == ChecklistStatus.Installing) ?? false, "An item was left installing.");
    }

    private static (MigrationViewModel ViewModel, WorkstationMigrationService Service, IMigrationSessionStore Store) Create(
        IWorkstationPlanningCoordinator planning,
        WorkstationMigrationTests.RecordingActionStore? actions = null,
        IMigrationSessionStore? store = null,
        MigrationPresentationTests.FakeFiles? files = null)
    {
        store ??= new WorkstationMigrationTests.MemorySessionStore();
        var coordinator = actions is null ? null : new CompiledActionCoordinator(actions, new WorkstationMigrationTests.ImmediateLauncher(), planning,
            pollInterval: TimeSpan.FromMilliseconds(1), resultTimeout: TimeSpan.FromSeconds(10));
        var service = new WorkstationMigrationService(planning, MigrationFixtures.InventoryService(), store, coordinator,
            new WorkstationMigrationTests.FixedTime(MigrationFixtures.Now));
        return (new MigrationViewModel(service, files ?? new MigrationPresentationTests.FakeFiles(), "1.1.3"), service, store);
    }

    /// <summary>Answers each refresh from the next step; the last step repeats.</summary>
    internal sealed class ScriptedPlanning(params Func<WorkstationPlan>[] steps) : IWorkstationPlanningCoordinator
    {
        private int calls;

        public Task<WorkstationPlan> RefreshAsync(IProgress<PlanningRefreshStage>? progress = null, CancellationToken cancellationToken = default)
        {
            var step = steps[Math.Min(Interlocked.Increment(ref calls) - 1, steps.Length - 1)];
            try { return Task.FromResult(step()); }
            catch (Exception exception) { return Task.FromException<WorkstationPlan>(exception); }
        }
    }

    /// <summary>Saves normally until told to fail, then refuses every save as a locked or read-only folder would.</summary>
    private sealed class FailingStore : IMigrationSessionStore
    {
        public bool FailSaves { get; set; }
        public MigrationSession? Saved { get; private set; }
        public MigrationSession? Load() => Saved;
        public void Save(MigrationSession session)
        {
            if (FailSaves) throw new UnauthorizedAccessException("Access to the path is denied.");
            Saved = session;
        }
        public void Delete() => Saved = null;
    }
}
