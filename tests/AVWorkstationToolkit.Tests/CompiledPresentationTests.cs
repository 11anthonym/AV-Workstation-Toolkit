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

        var match = viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Device);
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
        var match = viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Device);
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
    public async Task DeviceSearchShowsEveryCompatibilityMatchAndExplainsUnresolvedModels()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries());
        await viewModel.RefreshAsync();

        viewModel.SearchText = "DSP";
        var expected = CreateCompatibilityQueries().SearchDevices("DSP").Count + CreateCompatibilityQueries().SearchProducts("DSP").Count;
        Assert.HasCount(expected, viewModel.CompatibilityMatches);
        Assert.IsFalse(viewModel.CompatibilitySearchOutcomeVisible);

        viewModel.SearchText = "RMC4";
        var rmc4 = viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Device);
        Assert.AreEqual("RMC4", rmc4.Title);
        StringAssert.Contains(rmc4.Subtitle, "Exact model");
        Assert.IsFalse(rmc4.CanSelect);
        Assert.IsFalse(viewModel.CompatibilitySearchOutcomeVisible);
        Assert.AreEqual(0, viewModel.SelectedCount);
    }

    [TestMethod]
    public async Task HardwareIdentityResultsShowVerifiedAndUnresolvedCoverageWithoutInferringSoftware()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries());
        await viewModel.RefreshAsync();

        viewModel.SearchText = "CP 4N";
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

        viewModel.SearchText = "Core 110f";
        var unresolved = viewModel.CompatibilityMatches.Single(item => item.Kind == CompatibilitySearchResultKind.Device);
        Assert.AreEqual("Core 110f", unresolved.Title);
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
    public async Task DeviceSoftwareSelectionNavigatesWithinCompatibilityDetails()
    {
        using var viewModel = new MainWindowViewModel(new QueueCoordinator(CreatePlan()),
            compatibilityService: CreateCompatibilityQueries());
        await viewModel.RefreshAsync();
        viewModel.SearchText = "CP4N";
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
}
