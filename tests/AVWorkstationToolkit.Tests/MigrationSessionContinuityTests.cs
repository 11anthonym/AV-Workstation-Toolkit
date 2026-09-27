using AVWorkstationToolkit.App;
using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.App.ViewModels;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Application.Workstation;
using AVWorkstationToolkit.Domain.Workstation;
using AVWorkstationToolkit.Infrastructure.Windows.Migration;

namespace AVWorkstationToolkit.Tests;

/// <summary>
/// The active migration outlives its window and the app. The saved checklist is what the migration is trying to
/// accomplish; a scan of this PC says what it has; reconciling the two says what remains. Closing the window or the app
/// changes none of it, a failed scan keeps it, and only Finish migration clears it. Every write stays in a temporary root.
/// </summary>
[TestClass]
public sealed class MigrationSessionContinuityTests
{
    private const string Widget = "Vendor Widget Configuration Tool";

    [TestMethod]
    public void ClosingAndReopeningTheWindowRestoresTheSameMigration()
    {
        var root = NewRoot();
        try
        {
            MigrationWindowStateTests.RunOnDispatcher(async dispatcher =>
            {
                var target = await ThisPcWithCrestron();
                var store = new MigrationSessionFileStore(root);
                var service = NewService(new WorkstationMigrationTests.FixedPlanning(target.Plan), store);
                var files = ImportFiles();

                // A: import, change the checklist, close the window.
                var first = new MigrationViewModel(service, files, "1.1.3", dispatcher);
                var window = new MigrationWindow(first);
                await first.InitializeAsync(target.Plan);
                await first.ImportInventoryAsync();
                await MigrationWindowStateTests.Settle(window);
                first.Filter = MigrationFilter.All;
                first.VisibleItems.Single(row => row.Name == Widget).Included = false;
                await MigrationWindowStateTests.Settle(window);
                var sessionId = service.Session!.SessionId;
                var remaining = service.Checklist!.Summary.Remaining;
                var closed = false;
                window.Closed += (_, _) => closed = true;
                window.Close();
                Assert.IsTrue(closed);

                // Closing the window finished nothing: the saved migration is exactly as it was left.
                Assert.IsTrue(File.Exists(store.SessionPath));
                var saved = store.Load()!;
                Assert.AreEqual(sessionId, saved.SessionId);
                Assert.IsFalse(saved.Items.Single(item => item.Application.DisplayName == Widget).Included);

                // Reopening shows the same migration without importing again.
                var second = new MigrationViewModel(service, files, "1.1.3", dispatcher);
                var reopened = new MigrationWindow(second);
                await second.InitializeAsync(target.Plan);
                await MigrationWindowStateTests.Settle(reopened);
                Assert.IsTrue(second.HasSession);
                Assert.AreEqual("Migrating from SOURCE-PC", second.SourceTitle);
                Assert.AreEqual(sessionId, service.Session!.SessionId);
                Assert.AreEqual(remaining == 1 ? "1 remaining" : $"{remaining} remaining", second.RemainingHeadline);
                Assert.IsFalse(MigrationWindowStateTests.IsShown(reopened.EmptyState), "The reopened window shows the checklist, not the landing page.");
                second.Filter = MigrationFilter.Excluded;
                Assert.AreEqual(Widget, second.VisibleItems.Single().Name);
                Assert.IsNull(files.LastChoiceMessage, "Reopening never asks to import or replace anything.");
                reopened.Close();
                Assert.AreEqual(sessionId, store.Load()!.SessionId);
            });
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task TheMigrationSurvivesAFullAppRestart()
    {
        var root = NewRoot();
        try
        {
            var target = await ThisPcWithCrestron();
            string sessionId;
            // B: the first app run imports and changes the checklist, then its whole composition is dropped.
            {
                var service = CompiledAppComposition.Create(MigrationFixtures.RepositoryRoot(), root).Migration.Service;
                var session = service.StartFromInventory(WorkstationMigrationTests.Exported(WorkstationMigrationTests.SourceInventory()));
                sessionId = session.SessionId;
                service.SetIncluded(session.Items.Single(item => item.Application.DisplayName == Widget).ItemId, false);
            }

            // The next run builds a new composition over the same data root and restores the checklist without an import.
            var restarted = CompiledAppComposition.Create(MigrationFixtures.RepositoryRoot(), root).Migration.Service;
            var files = ImportFiles();
            var viewModel = new MigrationViewModel(restarted, files, "1.1.3");
            await viewModel.InitializeAsync(target.Plan);

            Assert.IsTrue(viewModel.HasSession);
            Assert.AreEqual(sessionId, restarted.Session!.SessionId);
            Assert.AreEqual("Migrating from SOURCE-PC", viewModel.SourceTitle);
            Assert.IsNotNull(restarted.Checklist, "The restored checklist is compared with this PC.");
            viewModel.Filter = MigrationFilter.Excluded;
            Assert.AreEqual(Widget, viewModel.VisibleItems.Single().Name);
            viewModel.Filter = MigrationFilter.Completed;
            Assert.IsTrue(viewModel.VisibleItems.Any(row => row.Name.StartsWith("Crestron Toolbox", StringComparison.Ordinal)));
            Assert.IsNull(files.LastChoiceMessage);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AWorkstationTemplateChecklistSurvivesARestartWithItsRevisionAndManualChecks()
    {
        var root = NewRoot();
        try
        {
            var target = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult());
            {
                var service = CompiledAppComposition.Create(MigrationFixtures.RepositoryRoot(), root).Migration.Service;
                service.StartFromProfile(WorkstationMigrationTests.JumpPc(3));
                service.SetTaskDone("verify-sleep", true);
            }

            var viewModel = new MigrationViewModel(CompiledAppComposition.Create(MigrationFixtures.RepositoryRoot(), root).Migration.Service, ImportFiles(), "1.1.3");
            await viewModel.InitializeAsync(target.Plan);

            Assert.AreEqual("Workstation template: Remote Support Jump PC · revision 3", viewModel.SourceTitle);
            Assert.IsTrue(viewModel.Tasks.Single(task => task.Id == "verify-sleep").Done);
            Assert.IsFalse(viewModel.Tasks.Single(task => task.Id == "verify-proactive").Done);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AnAppInstalledWhileTheToolkitWasClosedCompletesWhenTheMigrationReopens()
    {
        var root = NewRoot();
        try
        {
            // C: Crestron Toolbox is missing when the migration is saved and closed.
            var before = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult());
            {
                var service = CompiledAppComposition.Create(MigrationFixtures.RepositoryRoot(), root).Migration.Service;
                service.StartFromInventory(WorkstationMigrationTests.Exported(WorkstationMigrationTests.SourceInventory()));
                Assert.IsTrue(service.Accept(before.Plan));
                Assert.IsFalse(service.Checklist!.Items.Single(item => item.DisplayName.StartsWith("Crestron Toolbox", StringComparison.Ordinal)).Satisfied);
            }

            // It is installed before the next run, whose fresh scan finds it.
            var after = await ThisPcWithCrestron();
            var viewModel = new MigrationViewModel(CompiledAppComposition.Create(MigrationFixtures.RepositoryRoot(), root).Migration.Service, ImportFiles(), "1.1.3");
            await viewModel.InitializeAsync(after.Plan);

            Assert.IsFalse(viewModel.VisibleItems.Any(row => row.Name.StartsWith("Crestron Toolbox", StringComparison.Ordinal)), "It left Remaining.");
            viewModel.Filter = MigrationFilter.Completed;
            Assert.IsTrue(viewModel.VisibleItems.Any(row => row.Name.StartsWith("Crestron Toolbox", StringComparison.Ordinal)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ReopeningShowsTheSavedChecklistWhileThisPcIsChecked()
    {
        var root = NewRoot();
        try
        {
            var store = new MigrationSessionFileStore(root);
            var target = await ThisPcWithCrestron();
            NewService(new WorkstationMigrationTests.FixedPlanning(target.Plan), store)
                .StartFromInventory(WorkstationMigrationTests.Exported(WorkstationMigrationTests.SourceInventory()));

            var scan = new TaskCompletionSource<WorkstationPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
            var viewModel = new MigrationViewModel(NewService(new GatedPlanning(scan.Task), store), ImportFiles(), "1.1.3");
            var opening = viewModel.InitializeAsync(null);

            // The saved checklist is on screen at once, every item not checked yet, while this PC is scanned.
            Assert.IsTrue(viewModel.IsBusy);
            Assert.AreEqual("Checking this PC…", viewModel.RemainingHeadline);
            Assert.IsNotEmpty(viewModel.VisibleItems);
            Assert.IsTrue(viewModel.VisibleItems.All(row => row.Item.Status == ChecklistStatus.NotChecked && row.StatusLabel == "Not checked yet" && !row.CanInstall));

            scan.SetResult(target.Plan);
            await opening;
            Assert.IsFalse(viewModel.IsBusy);
            Assert.IsFalse(viewModel.VisibleItems.Any(row => row.Item.Status == ChecklistStatus.NotChecked));
            StringAssert.Contains(viewModel.Status, "Continuing the migration from SOURCE-PC");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AFailedScanAfterReopeningKeepsTheMigrationAndCanBeRetried()
    {
        var root = NewRoot();
        try
        {
            var store = new MigrationSessionFileStore(root);
            var target = await ThisPcWithCrestron();
            var first = NewService(new WorkstationMigrationTests.FixedPlanning(target.Plan), store);
            var session = first.StartFromInventory(WorkstationMigrationTests.Exported(WorkstationMigrationTests.SourceInventory()));
            first.SetIncluded(session.Items.Single(item => item.Application.DisplayName == Widget).ItemId, false);
            var savedBytes = File.ReadAllBytes(store.SessionPath);

            // D: the next run can't scan this PC.
            var planning = new GatedPlanning(Task.FromException<WorkstationPlan>(new InvalidOperationException("WinGet could not be started.")));
            var service = NewService(planning, store);
            var viewModel = new MigrationViewModel(service, ImportFiles(), "1.1.3");
            await viewModel.InitializeAsync(null);

            Assert.IsTrue(viewModel.HasSession, "The migration is still open.");
            Assert.IsFalse(viewModel.NoSessionVisible);
            StringAssert.Contains(viewModel.Status, "installation status isn't known yet");
            StringAssert.Contains(viewModel.Status, "The migration is kept; choose Rescan");
            Assert.IsNotEmpty(viewModel.VisibleItems, "The saved checklist stays visible.");
            CollectionAssert.AreEqual(savedBytes, File.ReadAllBytes(store.SessionPath), "A failed scan changed the saved migration.");
            Assert.IsTrue(viewModel.ScanCommand.CanExecute(null));

            // Rescan works once this PC can be scanned again, and the checklist edits are all still there.
            planning.Next = Task.FromResult(target.Plan);
            await viewModel.ScanAsync();
            Assert.IsNotNull(service.Checklist);
            viewModel.Filter = MigrationFilter.Excluded;
            Assert.AreEqual(Widget, viewModel.VisibleItems.Single().Name);
            Assert.AreEqual(session.SessionId, service.Session!.SessionId);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task FinishMigrationIsTheOneActionThatClearsTheMigration()
    {
        var root = NewRoot();
        try
        {
            var store = new MigrationSessionFileStore(root);
            var target = await ThisPcWithCrestron();
            var service = NewService(new WorkstationMigrationTests.FixedPlanning(target.Plan), store);
            service.StartFromInventory(WorkstationMigrationTests.Exported(WorkstationMigrationTests.SourceInventory()));
            var files = ImportFiles();
            var viewModel = new MigrationViewModel(service, files, "1.1.3");
            await viewModel.InitializeAsync(target.Plan);

            // Declining keeps everything.
            files.ConfirmAnswer = false;
            viewModel.FinishCommand.Execute(null);
            Assert.IsTrue(File.Exists(store.SessionPath));
            Assert.IsTrue(viewModel.HasSession);
            StringAssert.Contains(files.LastConfirmMessage!, "Finish this migration?");
            StringAssert.Contains(files.LastConfirmMessage!, "The active checklist for SOURCE-PC will be cleared.");
            StringAssert.Contains(files.LastConfirmMessage!, "This does not uninstall or remove any software.");

            // E: confirming clears it, and the next open shows the landing page.
            files.ConfirmAnswer = true;
            viewModel.FinishCommand.Execute(null);
            Assert.IsFalse(File.Exists(store.SessionPath));
            StringAssert.Contains(viewModel.Status, "no software was changed");
            var next = new MigrationViewModel(NewService(new WorkstationMigrationTests.FixedPlanning(target.Plan), store), ImportFiles(), "1.1.3");
            await next.InitializeAsync(target.Plan);
            Assert.IsFalse(next.HasSession);
            Assert.IsTrue(next.NoSessionVisible);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AnActiveMigrationIsNeverReplacedWithoutAnExplicitChoice()
    {
        var target = await ThisPcWithCrestron();
        var service = NewService(new WorkstationMigrationTests.FixedPlanning(target.Plan), new WorkstationMigrationTests.MemorySessionStore());
        var original = service.StartFromInventory(WorkstationMigrationTests.Exported(WorkstationMigrationTests.SourceInventory()));
        var files = new MigrationPresentationTests.FakeFiles { OpenPath = @"C:\fixture\golden.json" };
        files.Files[files.OpenPath] = File.ReadAllBytes(Path.Combine(MigrationFixtures.RepositoryRoot(), "tests", "fixtures", "inventory", "golden-av-workstation-inventory.json"));
        var viewModel = new MigrationViewModel(service, files, "1.1.3");
        await viewModel.InitializeAsync(target.Plan);

        // F: continuing is the default.
        await viewModel.ImportInventoryAsync();
        Assert.AreEqual(original.SessionId, service.Session!.SessionId);
        StringAssert.Contains(files.LastChoiceMessage!, "A migration from SOURCE-PC is already in progress.");
        Assert.AreEqual(("Continue current migration", "Replace with new migration"), files.LastChoiceLabels);
        StringAssert.Contains(viewModel.Status, "Nothing was imported");

        await viewModel.ApplyProfileAsync(WorkstationMigrationTests.JumpPc(3));
        Assert.AreEqual(original.SessionId, service.Session!.SessionId);
        Assert.AreEqual(("Continue current migration", "Replace with template"), files.LastChoiceLabels);
        StringAssert.Contains(viewModel.Status, "The template wasn't applied");

        // Replacing happens only when chosen.
        files.ReplaceAnswer = true;
        await viewModel.ImportInventoryAsync();
        Assert.AreNotEqual(original.SessionId, service.Session!.SessionId);
        Assert.AreEqual("Migrating from AV-LAB-PC-01", viewModel.SourceTitle);
    }

    [TestMethod]
    public async Task ClosingTheWindowSavesAChangeAnEarlierSaveCouldNotWrite()
    {
        var before = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult());
        var after = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult(("7zip.7zip", "26.03")));
        var store = new MigrationFailureStateTests.FailingStore();
        var planning = new WorkstationMigrationTests.FixedPlanning(after.Plan);
        var coordinator = new CompiledActionCoordinator(new WorkstationMigrationTests.RecordingActionStore(), new WorkstationMigrationTests.ImmediateLauncher(),
            planning, pollInterval: TimeSpan.FromMilliseconds(1), resultTimeout: TimeSpan.FromSeconds(10));
        var service = new WorkstationMigrationService(planning, MigrationFixtures.InventoryService(), store, coordinator, new WorkstationMigrationTests.FixedTime(MigrationFixtures.Now));
        service.StartFromInventory(WorkstationMigrationTests.Exported(WorkstationMigrationTests.SourceInventory()));
        var viewModel = new MigrationViewModel(service, ImportFiles(), "1.1.3");
        await viewModel.InitializeAsync(before.Plan);
        store.FailSaves = true;
        viewModel.Filter = MigrationFilter.All;
        await viewModel.InstallAsync([viewModel.VisibleItems.Single(row => row.Item.CatalogState?.Package.Id == "7zip.7zip")]);
        Assert.IsTrue(service.HasUnsavedChanges);
        Assert.IsNull(store.Saved!.Items.Single(item => item.Application.CatalogId == "7zip.7zip").LastAttempt);

        // The window closes once the store can be written again: the pending attempt is saved, and nothing is cleared.
        store.FailSaves = false;
        viewModel.Dispose();
        Assert.IsFalse(service.HasUnsavedChanges);
        Assert.IsTrue(store.Saved!.Items.Single(item => item.Application.CatalogId == "7zip.7zip").LastAttempt!.Succeeded);
        Assert.IsNotNull(service.Session);
    }

    private static Task<WorkstationMigrationTests.TargetState> ThisPcWithCrestron() => WorkstationMigrationTests.Target(
        [new RegistryUninstallRecord(RegistryInventorySource.Hklm32, "Crestron Toolbox 3.1390.0008.3", "3.1390.0008.3", "Crestron Electronics Inc.", "{1B52}_is1")],
        MigrationFixtures.WinGetResult(("WiresharkFoundation.Wireshark", "4.4.0")));

    private static WorkstationMigrationService NewService(IWorkstationPlanningCoordinator planning, IMigrationSessionStore store) =>
        new(planning, MigrationFixtures.InventoryService(), store, timeProvider: new WorkstationMigrationTests.FixedTime(MigrationFixtures.Now));

    private static MigrationPresentationTests.FakeFiles ImportFiles()
    {
        var files = new MigrationPresentationTests.FakeFiles { OpenPath = @"C:\fixture\old-pc.json" };
        files.Files[files.OpenPath] = WorkstationInventoryDocumentCodec.Serialize(WorkstationMigrationTests.SourceInventory(), "fixture");
        return files;
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), $"avwt-migration-continuity-{Guid.NewGuid():N}");

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    /// <summary>Answers each scan with whatever <see cref="Next"/> holds when the scan starts.</summary>
    private sealed class GatedPlanning(Task<WorkstationPlan> first) : IWorkstationPlanningCoordinator
    {
        public Task<WorkstationPlan> Next { get; set; } = first;

        public Task<WorkstationPlan> RefreshAsync(IProgress<PlanningRefreshStage>? progress = null, CancellationToken cancellationToken = default) => Next;
    }
}
