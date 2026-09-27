using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

[TestClass]
public sealed class MigrationPresentationTests
{
    [TestMethod]
    public async Task ImportComparesThisPcImmediatelyAndOpensOnTheRemainingList()
    {
        var target = await WorkstationMigrationTests.Target(
            [new RegistryUninstallRecord(RegistryInventorySource.Hklm64, "Wireshark 4.4.0 x64", "4.4.0", "Wireshark", "Wireshark")],
            MigrationFixtures.WinGetResult(("WiresharkFoundation.Wireshark", "4.4.0")));
        var files = new FakeFiles { OpenPath = @"C:\fixture\old-pc.json" };
        files.Files[files.OpenPath] = WorkstationInventoryDocumentCodec.Serialize(WorkstationMigrationTests.SourceInventory(), "fixture");
        var (viewModel, service, store) = Create(target.Plan, files);

        await viewModel.InitializeAsync(null);
        Assert.IsFalse(viewModel.HasSession);
        await viewModel.ImportInventoryAsync();

        Assert.IsTrue(viewModel.HasSession);
        Assert.IsNotNull(store.Saved);
        Assert.AreEqual(MigrationFilter.Remaining, viewModel.Filter);
        StringAssert.Contains(viewModel.Status, "Imported");
        StringAssert.Contains(viewModel.Status, "SOURCE-PC");
        Assert.IsFalse(viewModel.VisibleItems.Any(item => item.Satisfied), "The default view lists only what is still to do.");
        Assert.IsTrue(viewModel.VisibleItems.Any(item => item.Name == "Crestron Toolbox"));
        Assert.IsTrue(viewModel.VisibleItems.Any(item => item.Name == "Vendor Widget Configuration Tool"));
        viewModel.Filter = MigrationFilter.Completed;
        Assert.AreEqual("Wireshark", viewModel.VisibleItems.Single().Name);
        Assert.AreEqual($"{service.Checklist!.Summary.Included - 1} remaining", viewModel.RemainingHeadline);
        StringAssert.Contains(viewModel.ProgressText, "1 installed");
        viewModel.Filter = MigrationFilter.All;
        viewModel.SearchText = "crestron";
        Assert.AreEqual("Crestron Toolbox", viewModel.VisibleItems.Single().Name);
    }

    [TestMethod]
    public async Task ImportKeepsTheChecklistAndSaysSoWhenTheScanFails()
    {
        var files = new FakeFiles { OpenPath = @"C:\fixture\old-pc.json" };
        files.Files[files.OpenPath] = WorkstationInventoryDocumentCodec.Serialize(WorkstationMigrationTests.SourceInventory(), "fixture");
        var (viewModel, service, store) = Create(new FailingPlanning(), files);

        await viewModel.ImportInventoryAsync();

        Assert.IsNotNull(store.Saved, "The imported checklist is saved even when this PC can't be scanned yet.");
        Assert.IsNull(service.Checklist);
        StringAssert.Contains(viewModel.Status, "Imported");
        StringAssert.Contains(viewModel.Status, "Couldn't scan this PC");
        StringAssert.Contains(viewModel.Status, "Scan this PC again");
        Assert.IsFalse(viewModel.Status.Contains("already installed", StringComparison.Ordinal));
        Assert.IsTrue(viewModel.IsNotBusy);
    }

    [TestMethod]
    public async Task ChecklistEditsPersistAndExcludedItemsCanBeRestored()
    {
        var target = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult());
        var files = new FakeFiles();
        var (viewModel, service, store) = Create(target.Plan, files);
        service.StartFromInventory(WorkstationMigrationTests.Exported(WorkstationMigrationTests.SourceInventory()));
        await viewModel.InitializeAsync(target.Plan);
        viewModel.Filter = MigrationFilter.All;

        var widget = viewModel.VisibleItems.Single(item => item.Name == "Vendor Widget Configuration Tool");
        widget.Included = false;
        Assert.IsFalse(store.Saved!.Items.Single(item => item.ItemId == widget.ItemId).Included);
        viewModel.Filter = MigrationFilter.Excluded;
        viewModel.SelectedItem = viewModel.VisibleItems.Single(item => item.ItemId == widget.ItemId);
        Assert.IsTrue(viewModel.IncludeCommand.CanExecute(null));
        viewModel.IncludeCommand.Execute(null);
        Assert.IsTrue(store.Saved.Items.Single(item => item.ItemId == widget.ItemId).Included);

        viewModel.Filter = MigrationFilter.Remaining;
        var remaining = viewModel.VisibleItems.Single(item => item.ItemId == widget.ItemId);
        viewModel.SelectedItem = remaining;
        // A detectable application is never marked done by a click: only a scan that finds it completes it.
        Assert.IsFalse(remaining.CanConfirm);
        Assert.IsFalse(viewModel.ConfirmCommand.CanExecute(null));
        Assert.AreEqual("Manual · not in catalog", remaining.StatusLabel);

        files.ConfirmAnswer = false;
        viewModel.RemoveCommand.Execute(null);
        Assert.IsTrue(store.Saved.Items.Any(item => item.ItemId == widget.ItemId), "Declining the confirmation keeps the item.");
        files.ConfirmAnswer = true;
        viewModel.RemoveCommand.Execute(null);
        Assert.IsFalse(store.Saved.Items.Any(item => item.ItemId == widget.ItemId));
        Assert.AreEqual(1, store.Saved.RemovedCount);
        StringAssert.Contains(viewModel.SourceDetail, "1 removed");
    }

    [TestMethod]
    public async Task PreviewBuildNeverStartsAnInstallation()
    {
        var target = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult());
        var (viewModel, service, _) = Create(target.Plan, new FakeFiles());
        service.StartFromInventory(WorkstationMigrationTests.Exported(WorkstationMigrationTests.SourceInventory()));
        await viewModel.InitializeAsync(target.Plan);

        Assert.IsFalse(viewModel.AutomaticInstallAvailable);
        Assert.IsFalse(viewModel.CanInstallAll);
        StringAssert.Contains(viewModel.AutomaticInstallNote, "Preview build");
        var ready = service.Checklist!.Items.Where(item => item.Status == ChecklistStatus.ReadyToInstall).ToArray();
        Assert.IsNotEmpty(ready);
        await viewModel.InstallAsync(viewModel.VisibleItems.Where(item => ready.Any(entry => entry.ItemId == item.ItemId)).ToArray());
        StringAssert.Contains(viewModel.Status, "only in the packaged app");
    }

    [TestMethod]
    public async Task RiskBearingInstallsNeedAcknowledgementAndGoThroughTheCoordinator()
    {
        var before = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult());
        var after = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult(("7zip.7zip", "26.03"), ("WiresharkFoundation.Wireshark", "4.4.0")));
        var actions = new WorkstationMigrationTests.RecordingActionStore();
        var files = new FakeFiles();
        var (viewModel, service, _) = Create(new QueuePlanning(after.Plan), files, actions);
        service.StartFromInventory(WorkstationMigrationTests.Exported(WorkstationMigrationTests.SourceInventory()));
        await viewModel.InitializeAsync(before.Plan);

        StringAssert.Contains(viewModel.InstallAllText, "(2)");
        Assert.IsTrue(viewModel.RiskAcknowledgementVisible);
        StringAssert.Contains(viewModel.RiskAcknowledgementText, "Wireshark");
        Assert.IsFalse(viewModel.CanInstallAll, "A driver-bearing install waits for acknowledgement, as in the main window.");
        viewModel.RiskAcknowledged = true;
        Assert.IsTrue(viewModel.CanInstallAll);

        await viewModel.InstallAsync(viewModel.VisibleItems.Where(item => item.CanInstall).ToArray());

        CollectionAssert.AreEquivalent(new[] { "7zip.7zip", "WiresharkFoundation.Wireshark" }, actions.Request!.PackageIds.ToArray());
        Assert.IsTrue(actions.Request.RiskAcknowledged);
        Assert.IsFalse(viewModel.RiskAcknowledged, "An acknowledgement covers one run only.");
        StringAssert.Contains(viewModel.Status, "2 of 2 now detected");
        Assert.IsFalse(viewModel.VisibleItems.Any(item => item.CanInstall));
    }

    [TestMethod]
    public async Task ExportWritesAPortableInventoryOfThisPc()
    {
        var target = await WorkstationMigrationTests.Target(
            [new RegistryUninstallRecord(RegistryInventorySource.Hklm32, "Crestron Toolbox 3.1390.0008.3", "3.1390.0008.3", "Crestron Electronics Inc.", "{1B52}_is1"),
             new RegistryUninstallRecord(RegistryInventorySource.Hklm64, "Vendor Widget Configuration Tool", "4.2", "Vendor Corp", "VendorWidget")],
            MigrationFixtures.WinGetResult(("7zip.7zip", "24.08")));
        var files = new FakeFiles { SavePath = @"C:\fixture\fixture-pc-inventory.json" };
        var (viewModel, _, _) = Create(target.Plan, files);
        await viewModel.InitializeAsync(target.Plan);

        await viewModel.ExportInventoryAsync();

        var document = WorkstationInventoryDocumentCodec.Parse(files.Files[files.SavePath]);
        Assert.AreEqual("FIXTURE-PC", document.Machine.ComputerName);
        CollectionAssert.IsSubsetOf(new[] { "Crestron.Toolbox", "7zip.7zip" }, document.Applications.Select(item => item.CatalogId).ToArray());
        Assert.IsTrue(document.Applications.Any(item => item.DisplayName == "Vendor Widget Configuration Tool" && item.CatalogId.Length == 0));
        StringAssert.Contains(files.SuggestedName!, "fixture-pc-inventory-");
        StringAssert.Contains(viewModel.Status, "Exported");
    }

    [TestMethod]
    public async Task ApplyingANewerProfileRevisionShowsTheDifferenceAndKeepsProgress()
    {
        var target = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult());
        var files = new FakeFiles();
        var (viewModel, service, _) = Create(target.Plan, files);
        await viewModel.InitializeAsync(target.Plan);

        await viewModel.ApplyProfileAsync(WorkstationMigrationTests.JumpPc(3));
        Assert.AreEqual("Deployment profile: Remote Support Jump PC · version 3", viewModel.SourceTitle);
        Assert.IsTrue(viewModel.TasksVisible);
        viewModel.Tasks.Single(task => task.Id == "verify-sleep").Done = true;
        Assert.IsNotNull(service.Session!.Tasks.Single(task => task.Id == "verify-sleep").DoneAtUtc);

        await viewModel.ApplyProfileAsync(WorkstationMigrationTests.JumpPc(4));

        StringAssert.Contains(files.LastConfirmMessage!, "+ Mozilla Firefox");
        StringAssert.Contains(files.LastConfirmMessage!, "- Adobe Acrobat Reader");
        Assert.AreEqual(4, service.Session!.Source.ProfileVersion);
        Assert.IsTrue(viewModel.Tasks.Single(task => task.Id == "verify-sleep").Done);
        StringAssert.Contains(viewModel.TasksSummary, "1 of 5");
    }

    [TestMethod]
    public void ProfileEditorBuildsValidatedProfilesAndRevisions()
    {
        var files = new FakeFiles { SavePath = @"C:\fixture\jump.json" };
        var editor = new ProfileEditorViewModel(MigrationFixtures.Identities, files);
        Assert.AreEqual("1", editor.VersionText);
        editor.Name = "Service Technician Laptop";
        editor.Search = "Wireshark";
        editor.VisibleApplications.Single(item => item.CatalogId == "WiresharkFoundation.Wireshark").Selected = true;
        editor.Search = "Crestron Toolbox";
        editor.VisibleApplications.Single(item => item.CatalogId == "Crestron.Toolbox").Selected = true;
        editor.CustomName = "Proactive Agent";
        editor.CustomPublisher = "Proactive";
        editor.AddCustomCommand.Execute(null);
        editor.ChecksText = "Verify remote connectivity\r\n\r\nVerify sleep is disabled\nVerify sleep is disabled";
        StringAssert.Contains(editor.SelectionSummary, "3 applications");

        editor.SaveCommand.Execute(null);

        var saved = DeploymentProfileCodec.Parse(files.Files[files.SavePath]);
        Assert.AreEqual("service-technician-laptop", saved.ProfileId);
        Assert.AreEqual(1, saved.ProfileVersion);
        CollectionAssert.AreEqual(new[] { "Crestron.Toolbox", "WiresharkFoundation.Wireshark", "" }, saved.Applications.Select(item => item.CatalogId).ToArray());
        Assert.AreEqual("Proactive Agent", saved.Applications[2].DisplayName);
        CollectionAssert.AreEqual(new[] { "verify-remote-connectivity", "verify-sleep-is-disabled", "verify-sleep-is-disabled-2" }, saved.Checks.Select(item => item.Id).ToArray());
        Assert.IsNotNull(editor.SavedProfile);

        var revision = new ProfileEditorViewModel(MigrationFixtures.Identities, files, saved);
        Assert.AreEqual("2", revision.VersionText);
        revision.Name = "Renamed laptop";
        Assert.AreEqual("service-technician-laptop", revision.BuildProfile().ProfileId, "A revision keeps the profile's identity.");
        revision.VersionText = "zero";
        Assert.Throws<WorkstationDocumentException>(() => revision.BuildProfile());
    }

    [TestMethod]
    public async Task MainWindowAdoptsAPlanFromAMigrationScan()
    {
        var target = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult(("7zip.7zip", "24.08")));
        using var viewModel = new MainWindowViewModel(new WorkstationMigrationTests.FixedPlanning(target.Plan), searchDebounce: TimeSpan.Zero);
        Assert.IsEmpty(viewModel.Packages);
        await viewModel.AdoptPlanAsync(target.Plan);
        Assert.HasCount(target.Plan.Packages.Count, viewModel.Packages);
        Assert.IsTrue(viewModel.Packages.Single(item => item.Id == "7zip.7zip").State.Installed);
        Assert.IsFalse(viewModel.MigrationCommand.CanExecute(null), "Without a migration composition the entry point stays disabled.");
    }

    [TestMethod]
    public void MigrationAndProfileWindowsBindAndRender()
    {
        RunOnSta(() =>
        {
            var target = WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult(("WiresharkFoundation.Wireshark", "4.4.0"))).GetAwaiter().GetResult();
            var (empty, _, _) = Create(target.Plan, new FakeFiles());
            empty.InitializeAsync(target.Plan).GetAwaiter().GetResult();
            var emptyWindow = new MigrationWindow(empty);
            Render(emptyWindow, "migration-empty.png");
            emptyWindow.VerifySmokeContract();

            var actions = new WorkstationMigrationTests.RecordingActionStore();
            var (viewModel, service, _) = Create(target.Plan, new FakeFiles(), actions);
            service.StartFromInventory(WorkstationMigrationTests.Exported(WorkstationMigrationTests.SourceInventory()));
            viewModel.InitializeAsync(target.Plan).GetAwaiter().GetResult();
            viewModel.SelectedItem = viewModel.VisibleItems.First(item => item.Name == "Crestron Toolbox");
            var window = new MigrationWindow(viewModel);
            Render(window, "migration-checklist.png");
            window.VerifySmokeContract();
            if (window.ChecklistGrid.Items.Count != viewModel.VisibleItems.Count || !window.DetailPanel.IsVisible && window.DetailPanel.Visibility != Visibility.Visible)
                throw new InvalidOperationException("The checklist grid or detail panel did not bind.");

            var editor = new ProfileEditorWindow(new ProfileEditorViewModel(MigrationFixtures.Identities, new FakeFiles(), WorkstationMigrationTests.JumpPc(3)));
            Render(editor, "profile-editor.png");
        });
    }

    internal static (MigrationViewModel ViewModel, WorkstationMigrationService Service, WorkstationMigrationTests.MemorySessionStore Store) Create(
        WorkstationPlan plan, FakeFiles files, WorkstationMigrationTests.RecordingActionStore? actions = null) =>
        Create(new WorkstationMigrationTests.FixedPlanning(plan), files, actions);

    internal static (MigrationViewModel ViewModel, WorkstationMigrationService Service, WorkstationMigrationTests.MemorySessionStore Store) Create(
        IWorkstationPlanningCoordinator planning, FakeFiles files, WorkstationMigrationTests.RecordingActionStore? actions = null)
    {
        var store = new WorkstationMigrationTests.MemorySessionStore();
        var coordinator = actions is null ? null : new CompiledActionCoordinator(actions, new WorkstationMigrationTests.ImmediateLauncher(), planning,
            pollInterval: TimeSpan.FromMilliseconds(1), resultTimeout: TimeSpan.FromSeconds(10));
        var service = new WorkstationMigrationService(planning, MigrationFixtures.InventoryService(), store, coordinator,
            new WorkstationMigrationTests.FixedTime(MigrationFixtures.Now));
        return (new MigrationViewModel(service, files, "1.1.2"), service, store);
    }

    // Offscreen rendering: layout and binding must succeed; when AVWT_RENDER_DIR is set, the image is saved for review.
    internal static void Render(Window window, string fileName)
    {
        window.Width = 1240;
        window.Height = 820;
        var content = (FrameworkElement)window.Content;
        var size = new Size(1240, 790);
        content.Measure(size);
        content.Arrange(new Rect(size));
        content.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        content.UpdateLayout();
        if (content.ActualWidth <= 0 || content.ActualHeight <= 0) throw new InvalidOperationException($"{fileName} did not lay out.");
        var folder = Environment.GetEnvironmentVariable("AVWT_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(folder)) return;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0A, 0x0F, 0x1C)), null, new Rect(size));
            context.DrawRectangle(new VisualBrush(content), null, new Rect(size));
        }
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(folder);
        using var stream = File.Create(Path.Combine(folder, fileName));
        encoder.Save(stream);
    }

    internal static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    internal sealed class FailingPlanning : IWorkstationPlanningCoordinator
    {
        public Task<WorkstationPlan> RefreshAsync(IProgress<PlanningRefreshStage>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromException<WorkstationPlan>(new InvalidOperationException("WinGet could not be started."));
    }

    internal sealed class QueuePlanning(params WorkstationPlan[] plans) : IWorkstationPlanningCoordinator
    {
        private int index;
        public Task<WorkstationPlan> RefreshAsync(IProgress<PlanningRefreshStage>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(plans[Math.Min(index++, plans.Length - 1)]);
    }

    internal sealed class FakeFiles : IMigrationFileService
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? OpenPath { get; set; }
        public string? SavePath { get; set; }
        public string? SuggestedName { get; private set; }
        public bool ConfirmAnswer { get; set; } = true;
        public string? LastConfirmMessage { get; private set; }

        public string? PickInventoryToSave(string suggestedName) { SuggestedName = suggestedName; return SavePath; }
        public string? PickInventoryToOpen() => OpenPath;
        public string? PickProfileToOpen() => OpenPath;
        public string? PickProfileToSave(string suggestedName) { SuggestedName = suggestedName; return SavePath; }
        public bool Confirm(string title, string message) { LastConfirmMessage = message; return ConfirmAnswer; }
        public byte[] Read(string path, int maximumBytes) => Files[path];
        public void Write(string path, byte[] content) => Files[path] = content;
    }
}
