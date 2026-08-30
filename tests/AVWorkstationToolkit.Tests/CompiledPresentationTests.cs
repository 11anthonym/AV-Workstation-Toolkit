using System.ComponentModel;
using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;
using AVWorkstationToolkit.App.ViewModels;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class CompiledPresentationTests
{
    [TestMethod]
    public async Task SuccessfulRefreshBuildsVisibleRowsAndSummary()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()));
        await viewModel.RefreshAsync();
        Assert.HasCount(6, viewModel.Packages);
        Assert.HasCount(6, viewModel.VisiblePackages);
        Assert.AreEqual("6 shown of 6 | 1 current | 2 managed actions | 1 manual | 1 inventory | 0 warnings | 1 awareness", viewModel.StatusText);
        Assert.IsFalse(viewModel.IsBusy);
    }

    [TestMethod]
    public async Task FiltersComposeAcrossProfilePriorityManufacturerDisciplineRoleAndSearch()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()));
        await viewModel.RefreshAsync();
        viewModel.SelectedManufacturer = viewModel.ManufacturerOptions.Single(item => item.Value == "Vendor B");
        viewModel.SelectedPriority = viewModel.PriorityOptions.Single(item => item.Value == PackagePriority.P1);
        viewModel.SelectedDiscipline = viewModel.DisciplineOptions.Single(item => item.Value == CatalogDiscipline.Control);
        viewModel.SelectedRole = viewModel.RoleOptions.Single(item => item.Value == PackageRole.ControlProgramming);
        viewModel.SearchText = "update";
        Assert.HasCount(1, viewModel.VisiblePackages);
        Assert.AreEqual("Fixture.Update", viewModel.VisiblePackages[0].Id);
        viewModel.FieldProfile = false;
        Assert.IsEmpty(viewModel.VisiblePackages);
    }

    [TestMethod]
    public async Task QuickViewsSelectOnlyEligibleManagedActions()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()));
        await viewModel.RefreshAsync();
        viewModel.QuickViewCommand.Execute("Missing");
        Assert.AreEqual(QuickView.Missing, viewModel.QuickView);
        Assert.AreEqual(1, viewModel.InstallCount);
        Assert.AreEqual("Fixture.Missing", viewModel.Packages.Single(item => item.Selected).Id);
        viewModel.QuickViewCommand.Execute("Updates");
        Assert.AreEqual(1, viewModel.UpdateCount);
        Assert.AreEqual("Fixture.Update", viewModel.Packages.Single(item => item.Selected).Id);
        Assert.IsFalse(viewModel.Packages.Single(item => item.Id == "Fixture.Awareness").Selected);
    }

    [TestMethod]
    public async Task SortingIsStableAndDoesNotChangeSelection()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()));
        await viewModel.RefreshAsync();
        var selected = viewModel.Packages.Single(item => item.Id == "Fixture.Missing");
        selected.Selected = true;
        viewModel.SetSort("VendorSortKey", ListSortDirection.Descending);
        Assert.AreEqual("Vendor C", viewModel.VisiblePackages[0].Vendor);
        Assert.IsTrue(selected.Selected);
        viewModel.SetSort("ApplicationSortKey", ListSortDirection.Ascending);
        CollectionAssert.AreEqual(viewModel.VisiblePackages.Select(item => item.Name).Order(StringComparer.Ordinal).ToArray(), viewModel.VisiblePackages.Select(item => item.Name).ToArray());
    }

    [TestMethod]
    public async Task CheckboxSelectionOwnsCountsButtonsAndDeselection()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()));
        await viewModel.RefreshAsync();
        var install = viewModel.Packages.Single(item => item.Id == "Fixture.Missing");
        var update = viewModel.Packages.Single(item => item.Id == "Fixture.Update");
        install.Selected = true;
        update.Selected = true;
        Assert.AreEqual("2 selected | 1 install | 1 update", viewModel.SelectionSummary);
        Assert.AreEqual("Install selected (1)", viewModel.InstallButtonText);
        Assert.AreEqual("Update selected (1)", viewModel.UpdateButtonText);
        Assert.IsTrue(viewModel.CanInstall);
        Assert.IsTrue(viewModel.CanUpdate);
        update.Selected = false;
        Assert.AreEqual(0, viewModel.UpdateCount);
        Assert.IsFalse(viewModel.CanUpdate);
    }

    [TestMethod]
    public async Task RefreshRetainsOnlyStillEligibleSelections()
    {
        var first = CreatePlan();
        var second = CreatePlan(updateStatus: PackageStatus.Current);
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(first, second));
        await viewModel.RefreshAsync();
        viewModel.Packages.Single(item => item.Id == "Fixture.Missing").Selected = true;
        viewModel.Packages.Single(item => item.Id == "Fixture.Update").Selected = true;
        await viewModel.RefreshAsync();
        Assert.IsTrue(viewModel.Packages.Single(item => item.Id == "Fixture.Missing").Selected);
        Assert.IsFalse(viewModel.Packages.Single(item => item.Id == "Fixture.Update").Selected);
        Assert.AreEqual(1, viewModel.SelectedCount);
    }

    [TestMethod]
    public async Task BusyStateDisablesSelectionAndRefreshCompletes()
    {
        var completion = new TaskCompletionSource<WorkstationPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new MainWindowViewModel(new DelegateCoordinator((_, _) => completion.Task));
        var refresh = viewModel.RefreshAsync();
        Assert.IsTrue(viewModel.IsBusy);
        Assert.IsFalse(viewModel.RefreshCommand.CanExecute(null));
        completion.SetResult(CreatePlan());
        await refresh;
        Assert.IsFalse(viewModel.IsBusy);
    }

    [TestMethod]
    public async Task StaleRefreshCannotOverwriteNewerPlan()
    {
        var first = new TaskCompletionSource<WorkstationPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<WorkstationPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var viewModel = new MainWindowViewModel(new DelegateCoordinator((_, _) => ++calls == 1 ? first.Task : second.Task));
        var oldRefresh = viewModel.RefreshAsync();
        var newRefresh = viewModel.RefreshAsync();
        second.SetResult(CreatePlan());
        await newRefresh;
        first.SetResult(CreatePlan(updateStatus: PackageStatus.Current));
        await oldRefresh;
        Assert.AreEqual(PackageStatus.UpdateAvailable, viewModel.Packages.Single(item => item.Id == "Fixture.Update").Status);
    }

    [TestMethod]
    public async Task RebootWarningBlocksRiskBearingButtonButNotLowRiskButton()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan(rebootPending: true)));
        await viewModel.RefreshAsync();
        viewModel.Packages.Single(item => item.Id == "Fixture.Missing").Selected = true;
        viewModel.Packages.Single(item => item.Id == "Fixture.Update").Selected = true;
        Assert.IsTrue(viewModel.WarningVisible);
        Assert.IsTrue(viewModel.CanInstall);
        Assert.IsFalse(viewModel.CanUpdate);
    }

    [TestMethod]
    public async Task ProviderFailureIsPresentedWithoutReplacingExistingPlan()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan(), new InvalidOperationException("provider failed\u001b[31m")));
        await viewModel.RefreshAsync();
        await viewModel.RefreshAsync();
        Assert.HasCount(6, viewModel.Packages);
        StringAssert.Contains(viewModel.StatusText, "Refresh failed");
        Assert.DoesNotContain('\u001b', viewModel.ActivityText);
    }

    [TestMethod]
    public async Task InventoryWarningAndAwarenessRemainNeutralAndNonActionable()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan(inventoryWarning: true)));
        await viewModel.RefreshAsync();
        var inventory = viewModel.Packages.Single(item => item.Id == "Fixture.Inventory");
        var awareness = viewModel.Packages.Single(item => item.Id == "Fixture.Awareness");
        Assert.AreEqual(PackageStatus.InventoryIncomplete, inventory.Status);
        Assert.IsFalse(inventory.SelectionEnabled);
        Assert.AreEqual(PackageStatus.Awareness, awareness.Status);
        Assert.IsFalse(awareness.SelectionEnabled);
        Assert.IsTrue(viewModel.WarningVisible);
    }

    [TestMethod]
    public async Task CompiledActionButtonsRefuseMutation()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()));
        await viewModel.RefreshAsync();
        viewModel.Packages.Single(item => item.Id == "Fixture.Missing").Selected = true;
        viewModel.InstallCommand.Execute(null);
        Assert.AreEqual(1, viewModel.MutationRefusalCount);
        StringAssert.Contains(viewModel.ActivityText, "READ-ONLY");
        Assert.AreEqual(1, viewModel.InstallCount);
    }

    [TestMethod]
    public void RepositoryCatalogLoaderReadsAllThreeCurrentCatalogClasses()
    {
        var root = FindRepositoryRoot();
        var catalog = new RepositoryCatalogLoader().Load(root);
        Assert.HasCount(29, catalog.Items.Where(item => item.Authority == CatalogAuthority.ManagedWinGet));
        Assert.IsTrue(catalog.Items.Any(item => item.Authority == CatalogAuthority.OperationalExternal));
        Assert.IsTrue(catalog.Items.Any(item => item.Authority == CatalogAuthority.AwarenessOnly));
        Assert.AreEqual(catalog.Items.Count, catalog.Items.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static WorkstationPlan CreatePlan(
        PackageStatus updateStatus = PackageStatus.UpdateAvailable,
        bool rebootPending = false,
        bool inventoryWarning = false)
    {
        var parser = new CatalogParser(new DateOnly(2026, 8, 28));
        var definitions = parser.NormalizeManagedCatalog(
        [
            new("Standard", "Alpha current", "Fixture.Current", "Vendor A", "None", "current", null, null),
            new("Standard", "Beta missing", "Fixture.Missing", "Vendor A", "None", "missing", null, null),
            new("Field", "Gamma update", "Fixture.Update", "Vendor B", "Driver", "update control", null, null),
            new("Field", "Delta inventory", "Fixture.Inventory", "Vendor B", "None", "inventory", null, null)
        ], "NeverMatchThisFixture").Items;
        var external = definitions[3] with
        {
            Provider = ProviderKind.External,
            Authority = CatalogAuthority.OperationalExternal,
            Deployment = DeploymentPolicy.ManualHold,
            Maintenance = MaintenancePolicy.Hold,
            DeploymentClass = DeploymentClass.InventoryOnly,
            CatalogMaintenancePolicy = CatalogMaintenancePolicy.Manual,
            DetectionMode = DetectionMode.Registry,
            ReleaseMode = ReleaseMode.InventoryOnly,
            Roles = [PackageRole.FieldService],
            ApplicationTypes = [ApplicationType.FieldUtility]
        };
        var updateDefinition = definitions[2] with
        {
            Priority = PackagePriority.P1,
            ApplicationTypes = [ApplicationType.ControlSystem],
            Roles = [PackageRole.ControlProgramming]
        };
        var awareness = external with
        {
            Id = "Fixture.Awareness",
            Name = "Zeta awareness",
            Vendor = "Vendor C",
            Authority = CatalogAuthority.AwarenessOnly,
            DeploymentClass = DeploymentClass.AwarenessOnly,
            DetectionMode = DetectionMode.None,
            DeliveryMode = DeliveryMode.Awareness
        };
        var manual = external with { Id = "Fixture.Manual", Name = "Epsilon manual", DeploymentClass = DeploymentClass.ManualHandoff, ReleaseMode = ReleaseMode.VendorPage, KnownVersion = "2.0" };
        var states = new[]
        {
            State(definitions[0], PackageStatus.Current, PackageAction.None, true, "1.0"),
            State(definitions[1], PackageStatus.Missing, PackageAction.Install, false, string.Empty),
            State(updateDefinition, updateStatus, updateStatus == PackageStatus.UpdateAvailable ? PackageAction.Update : PackageAction.None, true, "1.0", updateStatus == PackageStatus.UpdateAvailable ? "1.1" : string.Empty),
            State(external, inventoryWarning ? PackageStatus.InventoryIncomplete : PackageStatus.Inventory, PackageAction.None, true, "3.0", quality: inventoryWarning ? InventoryQuality.Partial : InventoryQuality.Complete),
            State(manual, PackageStatus.Manual, PackageAction.Manual, false, string.Empty, "2.0"),
            State(awareness, PackageStatus.Awareness, PackageAction.None, false, string.Empty, quality: InventoryQuality.NotApplicable)
        };
        var summary = Summarize(states);
        var warnings = inventoryWarning ? new[] { "External inventory: one source unavailable." } : [];
        var providers = new ProviderRefreshSummary(ProviderQuality.Complete, ProviderQuality.Complete, inventoryWarning ? ProviderQuality.Partial : ProviderQuality.Complete, ProviderQuality.Complete, warnings);
        var reboot = rebootPending ? new RebootState(true, [RebootReason.WindowsUpdate], "Windows Update") : RebootState.Clear;
        return new WorkstationPlan(states, summary, reboot, providers);
    }

    private static PackageState State(PackageDefinition package, PackageStatus status, PackageAction action, bool installed, string installedVersion, string availableVersion = "", InventoryQuality quality = InventoryQuality.Complete) =>
        new(package, installed, installedVersion, installed ? [installedVersion] : [], availableVersion, status is PackageStatus.UpdateAvailable or PackageStatus.ManualUpdate,
            status, status.ToString(), status.ToString(), action, quality);

    private static WorkstationPlanSummary Summarize(IReadOnlyList<PackageState> states) => new(
        states.Count,
        states.Count(item => item.Status == PackageStatus.Current),
        states.Count(item => item.Status == PackageStatus.Missing),
        states.Count(item => item.Status == PackageStatus.UpdateAvailable),
        states.Count(item => item.Status == PackageStatus.Manual),
        states.Count(item => item.Status == PackageStatus.ManualUpdate),
        states.Count(item => item.Status == PackageStatus.Held),
        states.Count(item => item.Status == PackageStatus.Inventory),
        states.Count(item => item.Status == PackageStatus.NotDetected),
        states.Count(item => item.Status == PackageStatus.InventoryIncomplete),
        states.Count(item => item.Status == PackageStatus.InventoryUnavailable),
        states.Count(item => item.Status == PackageStatus.CheckUnavailable),
        states.Count(item => item.Status == PackageStatus.Awareness),
        states.Count(item => item.Status == PackageStatus.Error));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "VERSION"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class QueueCoordinator : IWorkstationPlanningCoordinator
    {
        private readonly Queue<object> results;
        public QueueCoordinator(params object[] results) => this.results = new Queue<object>(results);
        public Task<WorkstationPlan> RefreshAsync(IProgress<PlanningRefreshStage>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = results.Dequeue();
            return result is Exception exception ? Task.FromException<WorkstationPlan>(exception) : Task.FromResult((WorkstationPlan)result);
        }
    }

    private sealed class DelegateCoordinator(Func<IProgress<PlanningRefreshStage>?, CancellationToken, Task<WorkstationPlan>> callback) : IWorkstationPlanningCoordinator
    {
        public Task<WorkstationPlan> RefreshAsync(IProgress<PlanningRefreshStage>? progress = null, CancellationToken cancellationToken = default) => callback(progress, cancellationToken);
    }
}
