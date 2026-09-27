using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AVWorkstationToolkit.App;
using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.App.ViewModels;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Application.Workstation;
using AVWorkstationToolkit.Domain.Workstation;

namespace AVWorkstationToolkit.Tests;

/// <summary>
/// Hosts the real migration and main windows on a running WPF dispatcher and walks them through every checklist state,
/// failing on any binding error. It does not replace looking at the window; it catches broken bindings and converters,
/// controls left enabled while busy, updates from the wrong thread, and filters that break while their rows change.
/// </summary>
[TestClass]
public sealed class MigrationWindowStateTests
{
    [TestMethod]
    public void MigrationWindowBindsEveryChecklistStateWithoutBindingErrors()
    {
        RunOnDispatcher(async dispatcher =>
        {
            using var bindingErrors = BindingErrorCollector.Start();
            // The collector must see a real binding error, or an empty result below would prove nothing.
            var probe = new TextBlock { DataContext = new object() };
            probe.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("NoSuchProperty"));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.IsNotEmpty(bindingErrors.Messages, "The binding error collector did not observe a known binding error.");
            bindingErrors.Messages.Clear();

            var thisPc = await WorkstationMigrationTests.Target(
                [new RegistryUninstallRecord(RegistryInventorySource.Hklm32, "Crestron Toolbox 3.1390.0008.3", "3.1390.0008.3", "Crestron Electronics Inc.", "{1B52}_is1"),
                 new RegistryUninstallRecord(RegistryInventorySource.Hklm32, "Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.44.35211", "14.44.35211.0",
                     "Microsoft Corporation", "{0b5169e3-39da-4313-808e-1f9c0407f3bf}")],
                MigrationFixtures.WinGetResult(("WiresharkFoundation.Wireshark", "4.4.0")));
            var partlyScanned = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult(("7zip.7zip", "26.03")), registryQuality: ProviderQuality.Partial);
            var firstScan = new TaskCompletionSource<WorkstationPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
            var planning = new ControlledPlanning { Next = () => firstScan.Task };
            var actions = new WorkstationMigrationTests.RecordingActionStore();
            var coordinator = new CompiledActionCoordinator(actions, new WorkstationMigrationTests.ImmediateLauncher(), planning,
                pollInterval: TimeSpan.FromMilliseconds(5), resultTimeout: TimeSpan.FromSeconds(30));
            var service = new WorkstationMigrationService(planning, MigrationFixtures.InventoryService(), new WorkstationMigrationTests.MemorySessionStore(),
                coordinator, new WorkstationMigrationTests.FixedTime(MigrationFixtures.Now));
            var files = new MigrationPresentationTests.FakeFiles { OpenPath = @"C:\fixture\old-pc.json" };
            files.Files[files.OpenPath] = WorkstationInventoryDocumentCodec.Serialize(WorkstationMigrationTests.SourceInventory(), "fixture");
            var viewModel = new MigrationViewModel(service, files, "1.1.3", dispatcher);
            var window = new MigrationWindow(viewModel);

            // No checklist, first scan running: nothing that changes state can start.
            var initializing = viewModel.InitializeAsync(null);
            await Settle(window);
            Assert.IsTrue(viewModel.IsBusy);
            AssertEnabled(window, false, "ScanButton", "ExportInventoryButton", "ImportInventoryButton", "ApplyProfileButton", "NewProfileButton", "InstallAllButton");
            Assert.IsTrue(IsShown(window.EmptyState));
            StringAssert.Contains(window.MigrationStatus.Text, "Scanning this PC");

            // Scan complete, still no checklist: this PC's applications are listed for review before export.
            firstScan.SetResult(thisPc.Plan);
            await initializing;
            await Settle(window);
            AssertEnabled(window, true, "ScanButton", "ExportInventoryButton", "ImportInventoryButton", "ApplyProfileButton");
            Assert.IsTrue(IsShown(window.ReviewGrid) && window.ReviewGrid.Items.Count > 0);
            var applicationsOnly = window.ReviewGrid.Items.Count;
            viewModel.ShowSupportingComponents = true;
            await Settle(window);
            Assert.IsGreaterThan(applicationsOnly, window.ReviewGrid.Items.Count, "Supporting components can be reviewed too.");

            // An imported checklist: every filter binds its rows.
            planning.Next = () => Task.FromResult(thisPc.Plan);
            await viewModel.ImportInventoryAsync();
            await Settle(window);
            Assert.IsFalse(IsShown(window.EmptyState));
            foreach (var filter in new[] { MigrationFilter.Remaining, MigrationFilter.ReadyToInstall, MigrationFilter.Manual, MigrationFilter.Completed, MigrationFilter.Supporting })
            {
                viewModel.Filter = filter;
                await Settle(window);
                Assert.IsNotEmpty(viewModel.VisibleItems, $"{filter} has no rows in this fixture.");
                Assert.AreEqual(viewModel.VisibleItems.Count, window.ChecklistGrid.Items.Count, filter.ToString());
            }

            // A managed install is offered; a manual one is not, and neither can be confirmed by hand.
            viewModel.Filter = MigrationFilter.All;
            viewModel.SelectedItem = viewModel.VisibleItems.Single(row => row.Item.CatalogState?.Package.Id == "7zip.7zip");
            await Settle(window);
            Assert.IsTrue(IsShown(window.DetailPanel));
            AssertEnabled(window, true, "InstallSelectedButton");
            Assert.IsFalse(IsShown(window.ConfirmButton));
            viewModel.SelectedItem = viewModel.VisibleItems.Single(row => row.Name.Contains("Toolbelt", StringComparison.Ordinal));
            await Settle(window);
            AssertEnabled(window, false, "InstallSelectedButton");
            Assert.IsFalse(IsShown(window.ConfirmButton));

            // Installing: the window waits for the worker, and every control that changes the checklist is disabled.
            actions.Running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            planning.Next = () => Task.FromResult(partlyScanned.Plan);
            var installing = viewModel.InstallAsync([viewModel.VisibleItems.Single(row => row.Item.CatalogState?.Package.Id == "7zip.7zip")]);
            await Settle(window);
            Assert.IsTrue(viewModel.IsBusy);
            Assert.IsTrue(service.Checklist!.Items.Any(item => item.Status == ChecklistStatus.Installing));
            AssertEnabled(window, false, "ScanButton", "ImportInventoryButton", "InstallAllButton", "InstallSelectedButton", "RescanButton", "FinishButton");
            Assert.IsTrue(RealizedCheckBoxes(window.ChecklistGrid).All(checkBox => !checkBox.IsEnabled), "Checklist check boxes stay enabled while installing.");
            actions.Running.SetResult();
            await installing;
            await Settle(window);
            Assert.IsFalse(viewModel.IsBusy);
            Assert.IsTrue(RealizedCheckBoxes(window.ChecklistGrid).Any(checkBox => checkBox.IsEnabled));

            // The rescan afterward was partial: what it couldn't see needs attention.
            viewModel.Filter = MigrationFilter.NeedsAttention;
            await Settle(window);
            Assert.IsGreaterThan(0, window.ChecklistGrid.Items.Count);

            // Scan results arriving on worker threads while the filter changes.
            var filters = Enum.GetValues<MigrationFilter>();
            for (var round = 0; round < 24; round++)
            {
                var plan = round % 2 == 0 ? thisPc.Plan : partlyScanned.Plan;
                var background = Task.Run(() => service.Accept(plan));
                viewModel.Filter = filters[round % filters.Length];
                await background;
                await Settle(window);
                Assert.AreEqual(viewModel.VisibleItems.Count, window.ChecklistGrid.Items.Count);
            }

            // A failed scan is explained and leaves the window usable.
            planning.Next = () => Task.FromException<WorkstationPlan>(new InvalidOperationException("WinGet could not be started."));
            await viewModel.ScanAsync();
            await Settle(window);
            StringAssert.Contains(window.MigrationStatus.Text, "Couldn't scan this PC");
            AssertEnabled(window, true, "ScanButton", "ImportInventoryButton");

            // A deployment profile with an application this PC can never detect: only it can be confirmed by hand.
            planning.Next = () => Task.FromResult(thisPc.Plan);
            await viewModel.ApplyProfileAsync(new DeploymentProfile("fixture-lab", "Fixture lab", 1, "Fixture", MigrationFixtures.Now,
                [new("AllenHeath.AHMSystemManager", "AHM System Manager"), new("7zip.7zip", "7-Zip")], [new("verify-audio", "Verify audio routing")]));
            await Settle(window);
            Assert.IsTrue(IsShown(window.TasksPanel));
            viewModel.Filter = MigrationFilter.All;
            viewModel.SelectedItem = viewModel.VisibleItems.Single(row => row.Undetectable);
            await Settle(window);
            Assert.IsTrue(IsShown(window.ConfirmButton));
            AssertEnabled(window, true, "ConfirmButton");
            viewModel.ConfirmCommand.Execute(null);
            await Settle(window);
            viewModel.SelectedItem = viewModel.VisibleItems.Single(row => row.Undetectable);
            await Settle(window);
            Assert.IsTrue(IsShown(window.ClearConfirmationButton));
            Assert.IsFalse(IsShown(window.ConfirmButton));

            window.Close();
            Assert.IsEmpty(bindingErrors.Messages, string.Join(Environment.NewLine, bindingErrors.Messages));
        });
    }

    [TestMethod]
    public void AMigrationScanFinishingOnAWorkerThreadUpdatesTheMainWindowOnItsDispatcher()
    {
        RunOnDispatcher(async dispatcher =>
        {
            var target = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult(("7zip.7zip", "24.08")));
            var migration = new WorkstationMigrationService(new WorkstationMigrationTests.FixedPlanning(target.Plan), MigrationFixtures.InventoryService(),
                new WorkstationMigrationTests.MemorySessionStore(), timeProvider: new WorkstationMigrationTests.FixedTime(MigrationFixtures.Now));
            var viewModel = new MainWindowViewModel(new WorkstationMigrationTests.FixedPlanning(target.Plan), searchDebounce: TimeSpan.Zero,
                presentationDispatcher: dispatcher, migration: new MigrationComposition(migration, Path.GetTempPath(), "1.1.3"));
            var window = new MainWindow(viewModel, autoRefresh: false, allowDialogs: false);
            try
            {
                Assert.IsEmpty(viewModel.Packages);
                var raisedOn = Environment.CurrentManagedThreadId;
                migration.PlanRefreshed += (_, _) => raisedOn = Environment.CurrentManagedThreadId;

                // The service raises PlanRefreshed on the worker thread its scan finished on; the window must not read itself there.
                await Task.Run(() => migration.ScanAsync());
                Assert.AreNotEqual(Environment.CurrentManagedThreadId, raisedOn, "The scan did not finish on a worker thread.");
                for (var attempt = 0; attempt < 300 && viewModel.Packages.Count == 0; attempt++)
                    await Task.Delay(10);

                Assert.HasCount(target.Plan.Packages.Count, viewModel.Packages);
                Assert.IsTrue(viewModel.Packages.Single(item => item.Id == "7zip.7zip").State.Installed);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static async Task Settle(Window window)
    {
        var content = (FrameworkElement)window.Content;
        var size = new Size(1240, 790);
        for (var pass = 0; pass < 2; pass++)
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            content.Measure(size);
            content.Arrange(new Rect(size));
            content.UpdateLayout();
        }
    }

    private static void AssertEnabled(Window window, bool enabled, params string[] names)
    {
        foreach (var name in names)
        {
            var element = window.FindName(name) as UIElement ?? throw new AssertFailedException($"The window has no control '{name}'.");
            Assert.AreEqual(enabled, element.IsEnabled, $"{name} should be {(enabled ? "enabled" : "disabled")}.");
        }
    }

    // Visible on screen when shown: every element up to the window is Visible, so bound visibility converters ran.
    private static bool IsShown(FrameworkElement element)
    {
        for (DependencyObject? current = element; current is not null and not Window;
             current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        }
        return true;
    }

    private static IEnumerable<CheckBox> RealizedCheckBoxes(DataGrid grid)
    {
        for (var index = 0; index < grid.Items.Count; index++)
        {
            if (grid.ItemContainerGenerator.ContainerFromIndex(index) is not DataGridRow row) continue;
            foreach (var checkBox in Descendants<CheckBox>(row)) yield return checkBox;
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void RunOnDispatcher(Func<Dispatcher, Task> scenario)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { await scenario(dispatcher); }
                catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(2))) Assert.Fail("The WPF scenario did not finish within two minutes.");
        failure?.Throw();
    }

    private sealed class ControlledPlanning : IWorkstationPlanningCoordinator
    {
        public Func<Task<WorkstationPlan>> Next { get; set; } = () => throw new InvalidOperationException("No scan was scripted.");

        public Task<WorkstationPlan> RefreshAsync(IProgress<PlanningRefreshStage>? progress = null, CancellationToken cancellationToken = default) => Next();
    }

    /// <summary>Collects WPF binding errors raised on the creating thread only, so parallel tests can't interfere.</summary>
    private sealed class BindingErrorCollector : TraceListener
    {
        private readonly int thread = Environment.CurrentManagedThreadId;
        private readonly SourceLevels previous = PresentationTraceSources.DataBindingSource.Switch.Level;

        public List<string> Messages { get; } = [];

        public static BindingErrorCollector Start()
        {
            // WPF emits binding traces only once tracing is refreshed on (or a debugger is attached).
            PresentationTraceSources.Refresh();
            var collector = new BindingErrorCollector();
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            PresentationTraceSources.DataBindingSource.Listeners.Add(collector);
            return collector;
        }

        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            if (message is null || Environment.CurrentManagedThreadId != thread) return;
            lock (Messages) Messages.Add(message);
        }

        protected override void Dispose(bool disposing)
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
            PresentationTraceSources.DataBindingSource.Switch.Level = previous;
            base.Dispose(disposing);
        }
    }
}
