using System.ComponentModel;
using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;
using AVWorkstationToolkit.App.ViewModels;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Application.Providers;
using System.Text.Json;
using AVWorkstationToolkit.Application.Compatibility;
using AVWorkstationToolkit.Application.Details;
using AVWorkstationToolkit.Application.Vendors;
using System.Diagnostics;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class CompiledPresentationTests
{
    [TestMethod]
    public void WindowPlacementFitsOversizedWindowInsideUsableWorkArea()
    {
        var fitted = WindowWorkAreaPlacement.Fit(
            new WindowWorkAreaPlacement.PixelBounds(-128, -90, 1280, 860),
            new WindowWorkAreaPlacement.PixelBounds(0, 40, 1024, 680));

        Assert.AreEqual(new WindowWorkAreaPlacement.PixelBounds(0, 40, 1024, 680), fitted);
    }

    [TestMethod]
    public void WindowPlacementCentersNormalWindowOnMonitorWithNegativeCoordinates()
    {
        var fitted = WindowWorkAreaPlacement.Fit(
            new WindowWorkAreaPlacement.PixelBounds(0, 0, 800, 600),
            new WindowWorkAreaPlacement.PixelBounds(-1920, 0, 1920, 1040));

        Assert.AreEqual(new WindowWorkAreaPlacement.PixelBounds(-1360, 220, 800, 600), fitted);
    }

    [TestMethod]
    public async Task SearchTextSetterIsImmediateAndDebouncesCatalogWork()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries(), searchDebounce: TimeSpan.FromMilliseconds(120));
        await viewModel.RefreshAsync();
        var stopwatch = Stopwatch.StartNew();

        viewModel.SearchText = "DM-NVX-363";

        stopwatch.Stop();
        Assert.IsLessThan(75, stopwatch.ElapsedMilliseconds, "The typing path performed synchronous catalog work.");
        Assert.IsTrue(viewModel.SearchInProgress);
        Assert.AreEqual("Searching...", viewModel.SearchStatusText);
        Assert.IsEmpty(viewModel.CompatibilityMatches);
        await Task.Delay(40);
        Assert.IsEmpty(viewModel.CompatibilityMatches, "The compatibility search ran before the debounce interval.");

        await viewModel.SearchCompletion;
        Assert.IsFalse(viewModel.SearchInProgress);
        Assert.AreEqual("DM-NVX-363", viewModel.CompatibilityMatches.First().Title);
    }

    [TestMethod]
    public async Task FirstCharacterAndClearBothStayOffTheTypingPathUntilDebounceCompletes()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries(), searchDebounce: TimeSpan.FromMilliseconds(120));
        await viewModel.RefreshAsync();

        viewModel.SearchText = "Z";

        Assert.IsTrue(viewModel.SearchInProgress);
        Assert.HasCount(6, viewModel.VisiblePackages);
        await Task.Delay(40);
        Assert.HasCount(6, viewModel.VisiblePackages, "A one-character query rebuilt rows before the debounce interval.");
        await viewModel.SearchCompletion;
        Assert.HasCount(1, viewModel.VisiblePackages);
        Assert.AreEqual("Fixture.Awareness", viewModel.VisiblePackages[0].Id);

        viewModel.SearchText = string.Empty;

        Assert.IsTrue(viewModel.SearchInProgress);
        Assert.HasCount(1, viewModel.VisiblePackages);
        await Task.Delay(40);
        Assert.HasCount(1, viewModel.VisiblePackages, "Clearing search rebuilt rows on the typing path.");
        await viewModel.SearchCompletion;
        Assert.HasCount(6, viewModel.VisiblePackages);
        Assert.AreEqual(string.Empty, viewModel.SearchStatusText);
    }

    [TestMethod]
    public async Task RapidTypingCancelsStaleSearchAndOnlyNewestQueryApplies()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries(), searchDebounce: TimeSpan.FromMilliseconds(80));
        await viewModel.RefreshAsync();

        viewModel.SearchText = "CP4N";
        var staleSearch = viewModel.SearchCompletion;
        await Task.Delay(20);
        viewModel.SearchText = "Core 110f";
        await Task.Delay(20);
        viewModel.SearchText = "CAM520 Pro2";
        await viewModel.SearchCompletion;
        await staleSearch;

        Assert.IsFalse(viewModel.SearchInProgress);
        Assert.IsTrue(viewModel.CompatibilityMatches.Any(item => item.Title == "CAM520 Pro2"));
        Assert.IsFalse(viewModel.CompatibilityMatches.Any(item => item.Title is "CP4N" or "Core 110f"));
    }

    [TestMethod]
    public async Task SearchBoundsResultsAndClearingOrShortQueriesResetState()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries(), searchDebounce: TimeSpan.Zero);
        await viewModel.RefreshAsync();

        viewModel.SearchText = "e";
        await viewModel.SearchCompletion;
        Assert.IsEmpty(viewModel.CompatibilityMatches);
        Assert.AreEqual("Type at least 2 characters to search devices.", viewModel.SearchStatusText);

        viewModel.SearchText = "software";
        await viewModel.SearchCompletion;
        Assert.IsLessThanOrEqualTo(MainWindowViewModel.LiveCompatibilityResultLimit, viewModel.CompatibilityMatches.Count);

        viewModel.SearchText = string.Empty;
        await viewModel.SearchCompletion;
        Assert.IsEmpty(viewModel.CompatibilityMatches);
        Assert.AreEqual(string.Empty, viewModel.SearchStatusText);
        Assert.HasCount(viewModel.Packages.Count, viewModel.VisiblePackages);
    }

    [TestMethod]
    public async Task InFlightSearchCannotOverwriteNewProfileAndFilterState()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries(), searchDebounce: TimeSpan.FromMilliseconds(120));
        await viewModel.RefreshAsync();
        viewModel.SearchText = "Fixture";
        var staleSearch = viewModel.SearchCompletion;

        viewModel.StandardProfile = false;
        viewModel.SelectedManufacturer = viewModel.ManufacturerOptions.Single(item => item.Value == "Vendor B");
        viewModel.SelectedDiscipline = viewModel.DisciplineOptions.Single(item => item.Value == CatalogDiscipline.Control);
        viewModel.SelectedRole = viewModel.RoleOptions.Single(item => item.Value == PackageRole.ControlProgramming);
        viewModel.SelectedCatalogPreset = viewModel.CatalogPresetOptions.Single(item => item.Value == CatalogPreset.P1);
        await viewModel.SearchCompletion;
        await staleSearch;

        Assert.HasCount(1, viewModel.VisiblePackages);
        Assert.AreEqual("Fixture.Update", viewModel.VisiblePackages[0].Id);
    }

    [TestMethod]
    public async Task InFlightSearchCannotOverwriteNewSortOrder()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries(), searchDebounce: TimeSpan.FromMilliseconds(120));
        await viewModel.RefreshAsync();
        viewModel.SearchText = "Fixture";
        var staleSearch = viewModel.SearchCompletion;

        viewModel.SetSort("VendorSortKey", ListSortDirection.Descending);
        await viewModel.SearchCompletion;
        await staleSearch;

        var vendors = viewModel.VisiblePackages.Select(item => item.VendorSortKey).ToArray();
        CollectionAssert.AreEqual(vendors.OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase).ToArray(), vendors);
    }

    [TestMethod]
    public async Task RefreshInvalidatesSearchUsingPreviousPackageSnapshot()
    {
        using var viewModel = new MainWindowViewModel(
            new QueueCoordinator(CreatePlan(), CreatePlan(updateStatus: PackageStatus.Current)),
            compatibilityService: CreateCompatibilityQueries(), searchDebounce: TimeSpan.FromMilliseconds(120));
        await viewModel.RefreshAsync();
        viewModel.SearchText = "update";
        var staleSearch = viewModel.SearchCompletion;

        await viewModel.RefreshAsync();
        await viewModel.SearchCompletion;
        await staleSearch;

        var refreshed = viewModel.Packages.Single(item => item.Id == "Fixture.Update");
        Assert.AreEqual(PackageStatus.Current, refreshed.Status);
        Assert.AreSame(refreshed, viewModel.VisiblePackages.Single());
    }

    [TestMethod]
    public async Task QuickViewInvalidatesInFlightSearchBeforeSelectingCurrentRows()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries(), searchDebounce: TimeSpan.FromMilliseconds(120));
        await viewModel.RefreshAsync();
        viewModel.SearchText = "Fixture";
        var staleSearch = viewModel.SearchCompletion;

        viewModel.QuickViewCommand.Execute("Missing");
        await viewModel.SearchCompletion;
        await staleSearch;

        Assert.IsTrue(viewModel.IsMissingQuickView);
        Assert.IsTrue(viewModel.VisiblePackages.Any(item => item.Id == "Fixture.Missing"));
        Assert.IsFalse(viewModel.VisiblePackages.Any(item => item.Id == "Fixture.Update"));
        Assert.AreEqual("Fixture.Missing", viewModel.Packages.Single(item => item.Selected).Id);
    }

    [TestMethod]
    public async Task RapidTextAndStateChangesApplyOnlyNewestCompleteRequest()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries(), searchDebounce: TimeSpan.FromMilliseconds(80));
        await viewModel.RefreshAsync();
        viewModel.SearchText = "Fixture";
        viewModel.SelectedManufacturer = viewModel.ManufacturerOptions.Single(item => item.Value == "Vendor B");
        viewModel.SearchText = "update";
        viewModel.SelectedPriority = viewModel.PriorityOptions.Single(item => item.Value == PackagePriority.P1);
        viewModel.SelectedDiscipline = viewModel.DisciplineOptions.Single(item => item.Value == CatalogDiscipline.Control);
        viewModel.SelectedRole = viewModel.RoleOptions.Single(item => item.Value == PackageRole.ControlProgramming);
        viewModel.SetSort("ApplicationSortKey", ListSortDirection.Ascending);

        await viewModel.SearchCompletion;

        Assert.HasCount(1, viewModel.VisiblePackages);
        Assert.AreEqual("Fixture.Update", viewModel.VisiblePackages[0].Id);
        Assert.AreEqual("update", viewModel.SearchText);
    }

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
        await viewModel.SearchCompletion;
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
        Assert.IsFalse(viewModel.CanUpdate);
        Assert.IsTrue(viewModel.RiskAcknowledgementRequired);
        viewModel.RiskAcknowledged = true;
        Assert.IsTrue(viewModel.CanUpdate);
        update.Selected = false;
        Assert.AreEqual(0, viewModel.UpdateCount);
        Assert.IsFalse(viewModel.CanUpdate);
        Assert.IsFalse(viewModel.RiskAcknowledged);
    }

    [TestMethod]
    public async Task RiskAcknowledgementUpdatesCommandStateAndIsConsumedPerRun()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()));
        await viewModel.RefreshAsync();
        var update = viewModel.Packages.Single(item => item.Id == "Fixture.Update");
        var commandChanges = 0;
        viewModel.UpdateCommand.CanExecuteChanged += (_, _) => commandChanges++;
        update.Selected = true;

        Assert.IsFalse(viewModel.UpdateCommand.CanExecute(null));
        viewModel.RiskAcknowledged = true;
        Assert.IsTrue(viewModel.UpdateCommand.CanExecute(null));
        Assert.IsGreaterThan(0, commandChanges);

        viewModel.UpdateCommand.Execute(null);
        for (var attempt = 0; attempt < 20 && viewModel.MutationRefusalCount == 0; attempt++) await Task.Delay(10);
        Assert.AreEqual(1, viewModel.MutationRefusalCount);
        Assert.IsFalse(viewModel.RiskAcknowledged);
        Assert.IsFalse(viewModel.UpdateCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task CompiledApplicationMenuCommandsInvokeTypedWorkflowsAndDialogs()
    {
        var menu = new RecordingApplicationMenuWorkflow();
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()), applicationMenuWorkflow: menu);
        var safety = 0;
        var about = 0;
        viewModel.SafetySecurityRequested += () => safety++;
        viewModel.AboutRequested += () => about++;
        await viewModel.RefreshAsync();

        Assert.IsTrue(viewModel.ExportPlanCommand.CanExecute(null));
        Assert.IsTrue(viewModel.OpenLogsCommand.CanExecute(null));
        viewModel.ExportPlanCommand.Execute(null);
        viewModel.OpenLogsCommand.Execute(null);
        viewModel.SafetySecurityCommand.Execute(null);
        viewModel.AboutCommand.Execute(null);

        Assert.AreEqual(1, menu.ExportCalls);
        Assert.AreEqual(1, menu.OpenLogsCalls);
        Assert.AreEqual(1, safety);
        Assert.AreEqual(1, about);
        StringAssert.Contains(viewModel.ActivityText, "Exported application plan");
        StringAssert.Contains(viewModel.ActivityText, "Opened logs");
    }

    [TestMethod]
    public void PlanExportFormatterProducesSchemaThreeSanitizedTypedReport()
    {
        var plan = CreatePlan();
        var compromised = plan.Packages[0] with { StatusDetail = "password=cleartext" };
        plan = plan with { Packages = [compromised, .. plan.Packages.Skip(1)] };

        var json = PlanExportFormatter.Format(plan, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
        using var document = JsonDocument.Parse(json);

        Assert.AreEqual(3, document.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.AreEqual("Fixture.Current", document.RootElement.GetProperty("Packages")[0].GetProperty("Id").GetString());
        Assert.AreEqual("password=[REDACTED]", document.RootElement.GetProperty("Packages")[0].GetProperty("StatusDetail").GetString());
        Assert.DoesNotContain("cleartext", json, StringComparison.Ordinal);
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
    public async Task GetPackageCommandUsesVendorDeliveryWorkflowInsteadOfDetailsCommand()
    {
        var catalog = new RepositoryCatalogLoader().Load(FindRepositoryRoot());
        var package = catalog.GetRequired("Crestron.Toolbox");
        var state = State(package, PackageStatus.ManualUpdate, PackageAction.Manual, true, "3.124.0", "3.125.0");
        var summary = Summarize([state]);
        var providers = new ProviderRefreshSummary(ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, []);
        var release = new ExternalReleaseEvidence(package.Id, "3.125.0", "3.125.0", true, true,
            "https://www.crestron.com/liveupdate/MasterInstallerSFTP.xml", string.Empty, "validated",
            [new("137", "Crestron Toolbox", "3.125.0", "/software/Toolbox/3.125.0/setup.exe", "setup.exe", 1_048_576, false)]);
        var plan = new WorkstationPlan([state], summary, RebootState.Clear, providers,
            new Dictionary<string, ExternalReleaseEvidence>(StringComparer.OrdinalIgnoreCase) { [package.Id] = release });
        var delivery = new RecordingPackageDeliveryWorkflow();
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(plan), packageDeliveryWorkflow: delivery);
        await viewModel.RefreshAsync();
        viewModel.SelectedRow = viewModel.VisiblePackages.Single();

        Assert.IsTrue(viewModel.GetPackageCommand.CanExecute(null));
        viewModel.GetPackageCommand.Execute(null);
        for (var attempt = 0; attempt < 20 && delivery.Calls == 0; attempt++) await Task.Delay(10);

        Assert.AreEqual(1, delivery.Calls);
        Assert.AreEqual(package.Id, delivery.PackageId);
        Assert.IsNull(viewModel.SelectedDetail);
        StringAssert.Contains(viewModel.ActivityText, "PACKAGE READY");
    }

    [TestMethod]
    public async Task FindAppsDeviceMatchOpensApprovedCp4nSoftwareWithoutSelectionAuthority()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries());
        await viewModel.RefreshAsync();

        viewModel.SearchText = "CP4N";
        await viewModel.SearchCompletion;

        var match = viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Device &&
            item.Title.Equals("CP4N", StringComparison.Ordinal));
        Assert.AreEqual("CP4N", match.Title);
        Assert.IsFalse(match.CanSelect);
        Assert.AreEqual(0, viewModel.SelectedCount);
        match.OpenCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedCompatibilityDetail is not null);

        var detail = viewModel.SelectedCompatibilityDetail!;
        var software = detail.Groups.SelectMany(group => group.Fields).Select(field => field.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        CollectionAssert.IsSubsetOf(new[] { "Crestron SIMPL Windows", "Crestron Database", "Crestron Device Database", "Crestron Toolbox" }, software.ToArray());
        Assert.IsTrue(detail.Groups.Any(group => group.Name == "Programming"));
        Assert.IsTrue(detail.Groups.Any(group => group.Name == "Diagnostics"));
        Assert.IsTrue(detail.Groups.Any(group => group.Name == "Firmware"));
        Assert.AreEqual(0, viewModel.SelectedCount);
    }

    [TestMethod]
    [DataRow("CP4N")]
    [DataRow("DM-NVX")]
    [DataRow("Q-SYS Core")]
    [DataRow("Nexia")]
    public async Task FindAppsRecognizesReferenceDeviceModelsAndAliases(string search)
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries());
        await viewModel.RefreshAsync();

        viewModel.SearchText = search;
        await viewModel.SearchCompletion;

        Assert.IsTrue(viewModel.CompatibilityMatches.Any(item => item.Kind == CompatibilitySearchResultKind.Device));
        Assert.IsTrue(viewModel.CompatibilityMatches.All(item => !item.CanSelect));
        Assert.AreEqual(0, viewModel.SelectedCount);
    }

    [TestMethod]
    public async Task DmNvxDeviceViewKeepsToolAndToolboxPurposesDistinct()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries());
        await viewModel.RefreshAsync();
        viewModel.SearchText = "DM-NVX";
        await viewModel.SearchCompletion;
        var match = viewModel.CompatibilityMatches.First(item => item.Kind == CompatibilitySearchResultKind.Device &&
            item.Title.Equals("Crestron DM NVX Endpoints", StringComparison.Ordinal));
        match.OpenCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedCompatibilityDetail is not null);

        var detail = viewModel.SelectedCompatibilityDetail!;
        Assert.IsTrue(detail.Groups.Single(group => group.Name == "Configuration").Fields.Any(field => field.Label == "Crestron DM NVX Tool"));
        Assert.IsTrue(detail.Groups.Single(group => group.Name == "Discovery").Fields.Any(field => field.Label == "Crestron Toolbox"));
        Assert.IsFalse(detail.Groups.Single(group => group.Name == "Discovery").Fields.Any(field => field.Label == "Crestron DM NVX Tool"));
    }

    [TestMethod]
    public async Task AliasDeviceViewPreservesTheMatchedRelationshipScopeInsteadOfUsingDisplayText()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries());
        await viewModel.RefreshAsync();
        viewModel.SearchText = "PTZApp2";
        await viewModel.SearchCompletion;

        var match = viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Device);
        Assert.AreEqual("PTZApp2", match.Title);
        match.OpenCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedCompatibilityDetail is not null);

        var software = viewModel.SelectedCompatibilityDetail!.Groups.SelectMany(group => group.Fields)
            .Select(field => field.Label).ToArray();
        CollectionAssert.Contains(software, "AVer PTZApp 2");
        CollectionAssert.DoesNotContain(software, "AVer Room Management");
    }

    [TestMethod]
    public async Task CameraAndConferencingDeviceResultsRemainReadOnlyAndShowVerifiedOrUnresolvedCoverage()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries());
        await viewModel.RefreshAsync();

        viewModel.SearchText = "CAM520 Pro2";
        await viewModel.SearchCompletion;
        var aver = viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Device && item.Title == "CAM520 Pro2");
        Assert.IsFalse(aver.CanSelect);
        aver.OpenCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedCompatibilityDetail is not null);
        Assert.IsTrue(viewModel.SelectedCompatibilityDetail!.RelatedSoftware.Any(item => item.ProductName == "AVer Room Management"));

        viewModel.SearchText = "Poly Studio X52";
        await viewModel.SearchCompletion;
        var poly = viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Device && item.Title == "Poly Studio X52");
        StringAssert.Contains(poly.Subtitle, "not yet verified");
        Assert.IsFalse(poly.CanSelect);
        poly.OpenCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedCompatibilityDetail is not null && viewModel.SelectedCompatibilityDetail.RelatedSoftware.Count == 0);
        var coverage = viewModel.SelectedCompatibilityDetail!.Groups.Single(group => group.Name == "Software coverage").Fields.Single();
        StringAssert.Contains(coverage.Value, "no verified software relationship");
        Assert.AreEqual(0, viewModel.SelectedCount);
    }

    [TestMethod]
    public async Task CameraAndConferencingBatchBResultsKeepDesktopAndCloudOnlyWorkflowsSeparate()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries());
        await viewModel.RefreshAsync();

        viewModel.SearchText = "Move 4K 20X";
        await viewModel.SearchCompletion;
        var ptzOptics = viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Device && item.Title == "Move 4K 20X");
        Assert.IsFalse(ptzOptics.CanSelect);
        ptzOptics.OpenCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedCompatibilityDetail is not null);
        Assert.IsTrue(viewModel.SelectedCompatibilityDetail!.RelatedSoftware.Any(item => item.ProductName == "PTZOptics Camera Management Platform"));

        viewModel.SearchText = "Neat Board 50";
        await viewModel.SearchCompletion;
        var neat = viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Device && item.Title == "Neat Board 50");
        StringAssert.Contains(neat.Subtitle, "not yet verified");
        Assert.IsFalse(neat.CanSelect);
        neat.OpenCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedCompatibilityDetail is not null && viewModel.SelectedCompatibilityDetail.RelatedSoftware.Count == 0);
        var coverage = viewModel.SelectedCompatibilityDetail!.Groups.Single(group => group.Name == "Software coverage").Fields.Single();
        StringAssert.Contains(coverage.Value, "does not mean no software is required");
        Assert.AreEqual(0, viewModel.SelectedCount);
    }

    [TestMethod]
    public async Task DeviceSearchShowsEveryCompatibilityMatchAndExplainsUnresolvedModels()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries());
        await viewModel.RefreshAsync();

        viewModel.SearchText = "DSP";
        await viewModel.SearchCompletion;
        var expected = CreateCompatibilityQueries().SearchDevices("DSP").Count + CreateCompatibilityQueries().SearchProducts("DSP").Count;
        Assert.HasCount(Math.Min(expected, MainWindowViewModel.LiveCompatibilityResultLimit), viewModel.CompatibilityMatches);
        if (expected > MainWindowViewModel.LiveCompatibilityResultLimit)
            StringAssert.Contains(viewModel.CompatibilityMatchSummary, $"of {expected}");
        Assert.IsFalse(viewModel.CompatibilitySearchOutcomeVisible);

        viewModel.SearchText = "RMC4";
        await viewModel.SearchCompletion;
        var rmc4 = viewModel.CompatibilityMatches.First(item => item.Kind == CompatibilitySearchResultKind.Device);
        Assert.AreEqual("RMC4", rmc4.Title);
        StringAssert.Contains(rmc4.Subtitle, "Exact model");
        Assert.IsFalse(rmc4.CanSelect);
        Assert.IsFalse(viewModel.CompatibilitySearchOutcomeVisible);
        Assert.AreEqual(0, viewModel.SelectedCount);
    }

    [TestMethod]
    public async Task DisplayProjectorDeviceLookupRendersExactModelSoftwareAsReadOnlyCompatibility()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries());
        await viewModel.RefreshAsync();

        viewModel.SearchText = "UDX4K22";
        await viewModel.SearchCompletion;
        var device = viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Device && item.Title == "UDX-4K22");
        StringAssert.Contains(device.Subtitle, "Exact model");
        Assert.IsFalse(device.CanSelect);
        device.OpenCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedCompatibilityDetail is not null);

        var detail = viewModel.SelectedCompatibilityDetail!;
        Assert.IsTrue(detail.RelatedSoftware.Any(item => item.ProductName == "Barco Projector Toolset"));
        Assert.AreEqual(0, viewModel.SelectedCount);
    }

    [TestMethod]
    public async Task HardwareIdentityResultsShowVerifiedAndUnresolvedCoverageWithoutInferringSoftware()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries());
        await viewModel.RefreshAsync();

        viewModel.SearchText = "CP 4N";
        await viewModel.SearchCompletion;
        var verified = viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Device);
        Assert.AreEqual("CP4N", verified.Title);
        StringAssert.Contains(verified.Subtitle, "Exact model");
        verified.OpenCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedCompatibilityDetail is not null);
        var verifiedDetail = viewModel.SelectedCompatibilityDetail!;
        var identity = verifiedDetail.Groups.Single(group => group.Name == "Hardware identity").Fields;
        CollectionAssert.Contains(identity.Select(field => field.Label).ToArray(), "Manufacturer");
        CollectionAssert.Contains(identity.Select(field => field.Label).ToArray(), "Coverage");
        Assert.IsTrue(verifiedDetail.RelatedSoftware.Any(item => item.ProductName == "Crestron SIMPL Windows"));

        viewModel.SearchText = "RLNK-910R";
        await viewModel.SearchCompletion;
        var unresolved = viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Device);
        Assert.AreEqual("RLNK-910R-IEC-NS", unresolved.Title);
        StringAssert.Contains(unresolved.Subtitle, "not yet verified");
        Assert.IsFalse(unresolved.CanSelect);
        unresolved.OpenCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedCompatibilityDetail is not null && viewModel.SelectedCompatibilityDetail != verifiedDetail);
        var unresolvedDetail = viewModel.SelectedCompatibilityDetail!;
        Assert.HasCount(0, unresolvedDetail.RelatedSoftware);
        var coverage = unresolvedDetail.Groups.Single(group => group.Name == "Software coverage").Fields.Single();
        StringAssert.Contains(coverage.Value, "no verified software relationship");
        StringAssert.Contains(coverage.Value, "does not mean no software is required");
    }

    [TestMethod]
    public async Task FinalDeviceLookupPresentationUsesReadableCategoryLabelsAndExplicitUnresolvedLanguage()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries());
        await viewModel.RefreshAsync();

        viewModel.SearchText = "Core 110f";
        await viewModel.SearchCompletion;
        var dsp = viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Device && item.Title == "Core 110f");
        StringAssert.Contains(dsp.Subtitle, "Audio DSP");
        Assert.DoesNotContain("Audio Dsp", dsp.Subtitle, StringComparison.Ordinal);

        viewModel.SearchText = "DM NVX 363";
        await viewModel.SearchCompletion;
        var avoip = viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Device && item.Title == "DM-NVX-363");
        StringAssert.Contains(avoip.Subtitle, "AV-over-IP");

        viewModel.SearchText = "QM65C";
        await viewModel.SearchCompletion;
        var unresolved = viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Device && item.Title == "QM65C");
        StringAssert.Contains(unresolved.Subtitle, "not yet verified");
        StringAssert.Contains(unresolved.MatchDetail, "does not mean no software is required");
        Assert.IsFalse(unresolved.CanSelect);
        unresolved.OpenCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedCompatibilityDetail?.Name == "QM65C");
        Assert.HasCount(0, viewModel.SelectedCompatibilityDetail!.RelatedSoftware);
        Assert.IsTrue(viewModel.SelectedCompatibilityDetail.Groups.SelectMany(group => group.Fields)
            .Any(field => field.Value.Contains("does not mean no software is required", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task ShortModelAndControlInterfaceDeviceResultsRetainExactIdentityAndReadOnlyScopes()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries());
        await viewModel.RefreshAsync();

        viewModel.SearchText = "110F";
        await viewModel.SearchCompletion;
        var core = viewModel.CompatibilityMatches.First(item => item.Kind == CompatibilitySearchResultKind.Device);
        Assert.AreEqual("Core 110f", core.Title);
        StringAssert.Contains(core.Subtitle, "Q-SYS");
        StringAssert.Contains(core.Subtitle, "Exact model");
        Assert.IsFalse(core.CanSelect);
        core.OpenCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedCompatibilityDetail is not null);
        Assert.IsTrue(viewModel.SelectedCompatibilityDetail!.RelatedSoftware.Any(item => item.ProductName == "Q-SYS Designer Software"));
        Assert.IsTrue(viewModel.SelectedCompatibilityDetail.Groups.SelectMany(group => group.Fields)
            .Any(field => field.Value.Contains("2 GB", StringComparison.OrdinalIgnoreCase)));

        viewModel.SearchText = "NBP1200C";
        await viewModel.SearchCompletion;
        var panel = viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Device && item.Title == "NBP 1200C");
        StringAssert.Contains(panel.Subtitle, "Control Panel");
        Assert.IsFalse(panel.CanSelect);
        panel.OpenCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedCompatibilityDetail is not null &&
            viewModel.SelectedCompatibilityDetail.RelatedSoftware.Any(item => item.ProductName == "Extron Toolbelt"));
        Assert.IsTrue(viewModel.SelectedCompatibilityDetail!.RelatedSoftware.Any(item => item.ProductName == "Extron Global Configurator Plus"));
        Assert.IsTrue(viewModel.SelectedCompatibilityDetail.RelatedSoftware.Any(item => item.ProductName == "Extron Global Scripter"));
        Assert.AreEqual(0, viewModel.SelectedCount);
    }

    [TestMethod]
    public async Task SignalDistributionResultsRenderVerifiedAndUnresolvedHardwareWithoutSelectionAuthority()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries());
        await viewModel.RefreshAsync();

        viewModel.SearchText = "IN1808";
        await viewModel.SearchCompletion;
        var verified = viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Device && item.Title == "IN1808");
        StringAssert.Contains(verified.Subtitle, "Signal Distribution");
        Assert.IsFalse(verified.CanSelect);
        verified.OpenCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedCompatibilityDetail is not null);
        Assert.IsTrue(viewModel.SelectedCompatibilityDetail!.RelatedSoftware.Any(item => item.ProductName == "Extron Product Configuration Software"));

        viewModel.SearchText = "KD PS42";
        await viewModel.SearchCompletion;
        var unresolved = viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Device && item.Title == "KD-PS42");
        StringAssert.Contains(unresolved.Subtitle, "not yet verified");
        Assert.IsFalse(unresolved.CanSelect);
        unresolved.OpenCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedCompatibilityDetail is not null &&
            viewModel.SelectedCompatibilityDetail.Name == "KD-PS42");
        Assert.HasCount(0, viewModel.SelectedCompatibilityDetail!.RelatedSoftware);
        Assert.AreEqual(0, viewModel.SelectedCount);
    }

    [TestMethod]
    public async Task DeviceSoftwareSelectionNavigatesWithinCompatibilityDetails()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries());
        await viewModel.RefreshAsync();
        viewModel.SearchText = "CP4N";
        await viewModel.SearchCompletion;
        viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Device).OpenCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedCompatibilityDetail is not null);
        var deviceDetail = viewModel.SelectedCompatibilityDetail!;
        CompatibilityDetailViewModel? navigated = null;
        deviceDetail.NavigationRequested += detail => navigated = detail;

        deviceDetail.RelatedSoftware.Single(item => item.ProductName == "Crestron SIMPL Windows").OpenCommand.Execute(null);
        await WaitForAsync(() => navigated is not null);

        Assert.AreEqual("Crestron.SIMPLWindows", navigated!.ContextId);
        Assert.IsTrue(navigated.Groups.Any(group => group.Name == "CP4N"));
    }

    [TestMethod]
    public async Task QsysProductDetailShowsOneProductFamiliesAndExplicitUnknownEvidence()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries());
        await viewModel.RefreshAsync();
        viewModel.SearchText = "Q-SYS Designer";
        await viewModel.SearchCompletion;

        Assert.HasCount(1, viewModel.CompatibilityMatches.Where(item => item.Kind == CompatibilitySearchResultKind.Software));
        viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Software).OpenCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedCompatibilityDetail is not null);
        var detail = viewModel.SelectedCompatibilityDetail!;
        var families = detail.Groups.Single(group => group.Name == "Release families").Fields;
        CollectionAssert.AreEqual(new[] { "Current — Current", "LTS — LTS", "Archived — Archived" }, families.Select(field => field.Label).ToArray());
        var installed = detail.Groups.Single(group => group.Name == "Installed-version evidence").Fields.Single();
        StringAssert.StartsWith(installed.Value, "Unknown / Not yet verified");
        Assert.DoesNotContain("Not installed", installed.Value, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task SoftwareDetailShowsApplicableDevicesAndUsesReadOnlyEvidenceHandoff()
    {
        var handoff = new RecordingCompatibilityHandoff();
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()), handoffService: handoff,
            compatibilityService: CreateCompatibilityQueries());
        await viewModel.RefreshAsync();
        viewModel.SearchText = "Crestron Toolbox Software";
        await viewModel.SearchCompletion;
        viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Software).OpenCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedCompatibilityDetail is not null);

        var detail = viewModel.SelectedCompatibilityDetail!;
        Assert.IsTrue(detail.Groups.Any(group => group.Name == "CP4N"));
        Assert.IsTrue(detail.Groups.Any(group => group.Name.Contains("DM NVX", StringComparison.OrdinalIgnoreCase)));
        var evidence = detail.Links.First(link => link.Label.Contains("evidence", StringComparison.OrdinalIgnoreCase));
        evidence.Command.Execute(null);

        Assert.IsNotNull(handoff.Intent);
        Assert.AreEqual(OfficialUriKind.Evidence, handoff.Intent.Kind);
        Assert.AreEqual(Uri.UriSchemeHttps, handoff.Intent.Uri.Scheme);
        Assert.IsFalse(viewModel.Packages.Any(item => item.Selected));
    }

    [TestMethod]
    public async Task CompatibilitySearchDoesNotChangeExistingPackageFiltering()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries());
        await viewModel.RefreshAsync();
        viewModel.SearchText = "update";
        await viewModel.SearchCompletion;
        Assert.AreEqual("Fixture.Update", viewModel.VisiblePackages.Single().Id);
        Assert.IsTrue(viewModel.CompatibilityMatches.Any(item => item.Title == "Green-GO Update Connection"));
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

    [TestMethod]
    public async Task CatalogUpdateMenuAndViewModelUseTypedNonExecutingUpdateService()
    {
        var service = new RecordingReferenceCatalogUpdateService();
        using var main = new MainWindowViewModel(new QueueCoordinator(CreatePlan()), referenceCatalogUpdates: service);
        IReferenceCatalogUpdateService? requested = null;
        main.CatalogUpdatesRequested += value => requested = value;
        Assert.IsTrue(main.CatalogUpdatesCommand.CanExecute(null));
        main.CatalogUpdatesCommand.Execute(null);
        Assert.AreSame(service, requested);

        var updates = new CatalogUpdateViewModel(service);
        await updates.CheckAsync();
        Assert.AreEqual(ReferenceCatalogUpdateState.UpdateAvailable, updates.Status.State);
        Assert.IsTrue(updates.InstallCommand.CanExecute(null));
        await updates.InstallAsync();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, updates.Status.State);
        Assert.AreEqual(1, service.InstallCalls);
        Assert.IsTrue(updates.RestoreCommand.CanExecute(null));
        await updates.RestoreAsync();
        Assert.AreEqual(1, service.RestoreCalls);
        Assert.IsFalse(updates.RestoreCommand.CanExecute(null));
        Assert.IsFalse(updates.IsBusy);
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

    private static CompatibilityCatalogQueryService CreateCompatibilityQueries() => new(
        new RepositoryCompatibilityCatalogLoader().Load(FindRepositoryRoot()),
        new UnresolvedInstalledVersionEvidenceProvider(),
        new RepositoryHardwareIdentityCatalogLoader().Load(FindRepositoryRoot()));

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 50 && !condition(); attempt++) await Task.Delay(10);
        Assert.IsTrue(condition(), "The compatibility UI command did not complete in time.");
    }

    private sealed class RecordingCompatibilityHandoff : IValidatedUserHandoffService
    {
        public OpenOfficialUriIntent? Intent { get; private set; }
        public void OpenOfficialUri(OpenOfficialUriIntent intent) => Intent = intent;
        public void RevealVerifiedPayload(VendorDeliveryAuthorization authorization, VendorDownloadResult payload, string explicitDataRoot) =>
            throw new AssertFailedException("Compatibility links must not reveal vendor payloads.");
        public void OpenLogs(string explicitDataRoot) => throw new AssertFailedException("Compatibility links must not open logs.");
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

    private sealed class RecordingPackageDeliveryWorkflow : IPackageDeliveryWorkflow
    {
        public int Calls { get; private set; }
        public string PackageId { get; private set; } = string.Empty;
        public bool CanHandle(PackageState state, WorkstationPlan plan) => state.Package.DeliveryMode == DeliveryMode.ParentProvider;
        public Task<PackageDeliveryOutcome> DeliverAsync(PackageState state, WorkstationPlan plan, CancellationToken cancellationToken = default)
        {
            Calls++;
            PackageId = state.Package.Id;
            return Task.FromResult(new PackageDeliveryOutcome(true, "Vendor workflow invoked; no installer executed."));
        }
    }

    private sealed class RecordingApplicationMenuWorkflow : IApplicationMenuWorkflow
    {
        public int ExportCalls { get; private set; }
        public int OpenLogsCalls { get; private set; }
        public PlanExportOutcome ExportPlan(WorkstationPlan plan)
        {
            ExportCalls++;
            return new(true, "C:\\fixture\\plan.json", "Exported application plan: C:\\fixture\\plan.json");
        }
        public string OpenLogs()
        {
            OpenLogsCalls++;
            return "C:\\fixture\\logs";
        }
    }

    private sealed class RecordingReferenceCatalogUpdateService : IReferenceCatalogUpdateService
    {
        public int InstallCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public ReferenceCatalogUpdateStatus Status { get; private set; } = new(ReferenceCatalogUpdateState.Current, 0, "Embedded", 0, string.Empty, "Current");
        public ReferenceCatalogSet LoadActiveOrEmbedded() => throw new AssertFailedException("Presentation commands must not reload catalog data directly.");
        public Task<ReferenceCatalogUpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
        {
            Status = new(ReferenceCatalogUpdateState.UpdateAvailable, 0, "Embedded", 1, "2026.9.12.1", "Available",
                new(0, 0, 1, 1, 0, 0, 0, "One exact model added."));
            return Task.FromResult(Status);
        }
        public Task<ReferenceCatalogUpdateStatus> InstallAvailableAsync(CancellationToken cancellationToken = default)
        {
            InstallCalls++;
            Status = new(ReferenceCatalogUpdateState.Completed, 1, "2026.9.12.1", 0, string.Empty, "Installed", RestorableRevision: 7);
            return Task.FromResult(Status);
        }
        public Task<ReferenceCatalogUpdateStatus> ImportAsync(string bundlePath, CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("The update command must not use the offline import path.");
        public Task<ReferenceCatalogUpdateStatus> RestorePreviousAsync(CancellationToken cancellationToken = default)
        {
            RestoreCalls++;
            Status = new(ReferenceCatalogUpdateState.Completed, 7, "2026.9.11.7", 0, string.Empty, "Restored");
            return Task.FromResult(Status);
        }
    }
}
