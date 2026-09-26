using System.Text;
using System.Text.Json;
using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.App.ViewModels;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Application.Workstation;
using AVWorkstationToolkit.Domain.Workstation;

namespace AVWorkstationToolkit.Tests;

/// <summary>
/// End-to-end migration scenarios through the migration window's view model: what an old workstation exports, what the
/// replacement shows as remaining, and what makes an item leave or return to the remaining list.
/// </summary>
[TestClass]
public sealed class MigrationLifecycleTests
{
    private static RegistryUninstallRecord Toolbox(string version = "3.1390.0008.3") =>
        new(RegistryInventorySource.Hklm32, $"Crestron Toolbox {version}", version, "Crestron Electronics Inc.", "{1B52BC01-2F6E-4FAE-BB09-1F28D2BF1D63}_is1");

    [TestMethod]
    public async Task RemainingHidesWhatIsCurrentlyDetectedAndBringsItBackWhenItDisappears()
    {
        // Scenario G and the remaining-list lifecycle: satisfied items are hidden, not deleted.
        var missing = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult());
        var installed = await WorkstationMigrationTests.Target([Toolbox()], MigrationFixtures.WinGetResult());
        var (viewModel, service, store, files) = Create(new QueuePlanning(missing.Plan, installed.Plan, missing.Plan));
        files.Open(SourceFile(Registration("Crestron Toolbox 3.1390.0008.3", "3.1390.0008.3", "Crestron Electronics Inc.", UninstallHive.Machine32, "{1B52BC01-2F6E-4FAE-BB09-1F28D2BF1D63}_is1")));

        await viewModel.ImportInventoryAsync();
        Assert.AreEqual(MigrationFilter.Remaining, viewModel.Filter);
        Assert.IsTrue(Names(viewModel).Contains("Crestron Toolbox"), "Missing on the target: listed as remaining.");
        Assert.AreEqual("1 remaining", viewModel.RemainingHeadline);

        await viewModel.ScanAsync();
        Assert.IsFalse(Names(viewModel).Contains("Crestron Toolbox"), "Detected: it leaves Remaining.");
        Assert.AreEqual("Nothing remaining", viewModel.RemainingHeadline);
        viewModel.Filter = MigrationFilter.Completed;
        Assert.AreEqual("Installed", viewModel.VisibleItems.Single(item => item.Name == "Crestron Toolbox").StatusLabel);
        viewModel.Filter = MigrationFilter.All;
        Assert.IsTrue(Names(viewModel).Contains("Crestron Toolbox"));
        Assert.HasCount(1, store.Saved!.Items, "The session keeps the satisfied item.");

        await viewModel.ScanAsync();
        viewModel.Filter = MigrationFilter.Remaining;
        Assert.IsTrue(Names(viewModel).Contains("Crestron Toolbox"), "No longer detected: it returns to Remaining.");
        Assert.AreEqual("1 remaining", viewModel.RemainingHeadline);
    }

    [TestMethod]
    public async Task ScenarioA_RegistryOnlyCrestronAppIsExportedThenLeavesRemainingAfterManualInstallAndRescan()
    {
        var sourceBytes = SourceFile(
            Registration("Crestron Toolbox 3.1390.0008.3", "3.1390.0008.3", "Crestron Electronics Inc.", UninstallHive.Machine32, "{1B52BC01-2F6E-4FAE-BB09-1F28D2BF1D63}_is1"),
            Registration("7-Zip 24.08 (x64)", "24.08", "Igor Pavlov", key: "7-Zip"));
        var exported = WorkstationInventoryDocumentCodec.Parse(sourceBytes);
        var toolbox = exported.Applications.Single(item => item.CatalogId == "Crestron.Toolbox");
        Assert.IsNull(toolbox.WinGet, "Not in WinGet, still exported.");
        Assert.AreEqual(MigrationRelevance.Application, toolbox.Relevance);

        var missing = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult(("7zip.7zip", "24.08")));
        var afterManualInstall = await WorkstationMigrationTests.Target([Toolbox("3.1400.0001.0")], MigrationFixtures.WinGetResult(("7zip.7zip", "24.08")));
        var (viewModel, _, _, files) = Create(new QueuePlanning(missing.Plan, afterManualInstall.Plan));
        files.Open(sourceBytes);

        await viewModel.ImportInventoryAsync();
        var row = viewModel.VisibleItems.Single(item => item.Name == "Crestron Toolbox");
        Assert.AreEqual("Manual · not yet detected", row.StatusLabel);
        Assert.IsFalse(row.CanInstall);
        Assert.IsFalse(row.CanConfirm, "No manual 'done' for an application the workstation can detect.");

        await viewModel.ScanAsync();
        Assert.IsFalse(Names(viewModel).Contains("Crestron Toolbox"));
        viewModel.Filter = MigrationFilter.Completed;
        StringAssert.Contains(viewModel.VisibleItems.Single(item => item.Name == "Crestron Toolbox").VersionNote, "Newer");
    }

    [TestMethod]
    public async Task ScenarioB_ManagedAppInstalledThroughTheWorkerLeavesRemainingOnlyAfterTheRescanDetectsIt()
    {
        var missing = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult());
        var installed = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult(("7zip.7zip", "26.03")));
        var actions = new WorkstationMigrationTests.RecordingActionStore();
        var (viewModel, _, store, files) = Create(new QueuePlanning(missing.Plan, installed.Plan), actions);
        files.Open(SourceFile([Registration("7-Zip 26.03 (x64 edition)", "26.03.00.0", "Igor Pavlov", key: "{23170F69-40C1-2702-2603-000001000000}")],
            [new WinGetPackageEvidence("7zip.7zip", "26.03.00.0")]));
        await viewModel.ImportInventoryAsync();
        var row = viewModel.VisibleItems.Single(item => item.Name == "7-Zip");
        Assert.AreEqual("Install available", row.StatusLabel);
        Assert.IsTrue(viewModel.InstallItemCommand.CanExecute(row));

        // The per-row Install command, not a direct service call; no Rescan is needed afterward.
        viewModel.InstallItemCommand.Execute(row);
        await WaitUntilAsync(() => !viewModel.IsBusy && actions.Request is not null);

        CollectionAssert.AreEqual(new[] { "7zip.7zip" }, actions.Request!.PackageIds.ToArray());
        Assert.IsFalse(Names(viewModel).Contains("7-Zip"), "The post-install scan detected it, so it left Remaining.");
        Assert.IsTrue(store.Saved!.Items.Single().LastAttempt!.Succeeded);
    }

    [TestMethod]
    public async Task InstallAllEndsInARescanAndAnUndetectedSuccessStaysRemaining()
    {
        var missing = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult());
        // The worker reports success for both, but the post-install scan detects only 7-Zip.
        var partlyInstalled = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult(("7zip.7zip", "26.03")));
        var actions = new WorkstationMigrationTests.RecordingActionStore();
        var (viewModel, _, _, files) = Create(new QueuePlanning(missing.Plan, partlyInstalled.Plan), actions);
        files.Open(SourceFile([], [new WinGetPackageEvidence("7zip.7zip", "26.03"), new WinGetPackageEvidence("Git.Git", "2.55.0")]));
        await viewModel.ImportInventoryAsync();
        Assert.AreEqual("Install all available (2)", viewModel.InstallAllText);

        viewModel.InstallAllCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsBusy && actions.Request is not null);

        CollectionAssert.AreEquivalent(new[] { "7zip.7zip", "Git.Git" }, actions.Request!.PackageIds.ToArray());
        CollectionAssert.AreEqual(new[] { "Git" }, Names(viewModel).ToArray(), "An exit code alone never completes an item.");
        Assert.AreEqual("Install failed", viewModel.VisibleItems.Single().StatusLabel);
        StringAssert.Contains(viewModel.VisibleItems.Single().Detail, "isn't detected");
        StringAssert.Contains(viewModel.Status, "1 of 2 now detected");
    }

    [TestMethod]
    public async Task ScenarioC_UnknownDesktopAppIsExportedIncludedAndSatisfiedByAConservativeLocalMatch()
    {
        var sourceBytes = SourceFile(Registration("Fixture AV Configuration Utility", "4.2", "Fixture AV", key: "FixtureAvConfig"));
        var exported = WorkstationInventoryDocumentCodec.Parse(sourceBytes).Applications.Single();
        Assert.AreEqual("Fixture AV Configuration Utility", exported.DisplayName);
        Assert.AreEqual(string.Empty, exported.CatalogId);
        Assert.IsNull(exported.WinGet);
        Assert.AreEqual(MigrationRelevance.Application, exported.Relevance);
        Assert.AreEqual("FixtureAvConfig", exported.Registrations.Single().KeyName, "The raw evidence is exported.");

        var missing = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult());
        var reinstalled = await WorkstationMigrationTests.Target(
            [new RegistryUninstallRecord(RegistryInventorySource.Hklm64, "Fixture AV Configuration Utility", "4.3", "Fixture AV, Inc.", "{7D5E0C3A-0000-4000-8000-000000000042}")],
            MigrationFixtures.WinGetResult());
        var (viewModel, _, store, files) = Create(new QueuePlanning(missing.Plan, reinstalled.Plan));
        files.Open(sourceBytes);

        await viewModel.ImportInventoryAsync();
        Assert.IsTrue(store.Saved!.Items.Single().Included, "A normal unknown application is part of the migration by default.");
        var row = viewModel.VisibleItems.Single();
        Assert.AreEqual("Manual · not in catalog", row.StatusLabel);
        Assert.IsFalse(row.CanConfirm);

        await viewModel.ScanAsync();
        Assert.IsEmpty(viewModel.VisibleItems, "A name-and-publisher match on this PC satisfies it.");
        viewModel.Filter = MigrationFilter.Completed;
        StringAssert.Contains(viewModel.VisibleItems.Single().EvidenceText, "name and publisher");
    }

    [TestMethod]
    public async Task ScenarioD_SupportingComponentsStayAsEvidenceButOutOfRemainingAndOutOfUserExclusions()
    {
        var sourceBytes = SourceFile(
            [
                Registration("Extron Electronics - Toolbelt", "2.35.0.14", "Extron"),
                Registration("Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.44.35211", "14.44.35211.0", "Microsoft Corporation", UninstallHive.Machine32, "{0b5169e3-39da-4313-808e-1f9c0407f3bf}"),
                Registration("Microsoft .NET Runtime - 10.0.11 (x64)", "80.44.56884", "Microsoft Corporation", key: "{0B4F3EF1-06F1-46E4-B662-A33DECC02141}", systemComponent: true),
                Registration("Mozilla Maintenance Service", "130.0", "Mozilla", key: "MozillaMaintenanceService")
            ],
            [new WinGetPackageEvidence("Microsoft.VCRedist.2015+.x86", "14.44.35211.0"), new WinGetPackageEvidence("Microsoft.UI.Xaml.2.8", "8.2511.26001.0")]);
        using (var json = JsonDocument.Parse(sourceBytes))
        {
            var entries = json.RootElement.GetProperty("applications").EnumerateArray()
                .ToDictionary(item => item.GetProperty("displayName").GetString()!, item => item);
            Assert.AreEqual("supportComponent", entries["Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.44.35211"].GetProperty("relevance").GetString());
            Assert.AreEqual(ApplicationComponentRules.RuntimeReason, entries["Microsoft.UI.Xaml.2.8"].GetProperty("relevanceReason").GetString());
            Assert.AreEqual("systemComponent", entries["Microsoft .NET Runtime - 10.0.11 (x64)"].GetProperty("relevance").GetString());
            Assert.AreEqual(ApplicationComponentRules.HelperReason, entries["Mozilla Maintenance Service"].GetProperty("relevanceReason").GetString());
            Assert.IsFalse(entries["Extron Electronics - Toolbelt"].TryGetProperty("relevanceReason", out _));
        }

        var target = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult());
        var (viewModel, service, _, files) = Create(new QueuePlanning(target.Plan));
        files.Open(sourceBytes);
        await viewModel.ImportInventoryAsync();

        CollectionAssert.AreEqual(new[] { "Extron Toolbelt" }, Names(viewModel).ToArray());
        var summary = service.Checklist!.Summary;
        Assert.AreEqual(1, summary.Imported);
        Assert.AreEqual(1, summary.Remaining);
        Assert.AreEqual(0, summary.Excluded, "Supporting components are not user exclusions.");
        Assert.AreEqual(3, summary.Supporting);
        Assert.AreEqual(1, service.Session!.SkippedComponentCount);
        viewModel.Filter = MigrationFilter.Supporting;
        Assert.HasCount(3, viewModel.VisibleItems);
        Assert.IsTrue(viewModel.VisibleItems.All(item => item.StatusLabel == "Supporting component"));
        viewModel.Filter = MigrationFilter.Excluded;
        Assert.IsEmpty(viewModel.VisibleItems);
        StringAssert.Contains(viewModel.SourceDetail, "3 supporting components left out");
    }

    [TestMethod]
    public void RepresentativeAvAppsSurviveExportWithoutWinGetAndStartSelected()
    {
        // Names observed in a real workstation registry: none has a WinGet identity.
        var bytes = SourceFile(
            [
                Registration("Crestron Toolbox 3.1390.0008.3", "3.1390.0008.3", "Crestron Electronics Inc.", UninstallHive.Machine32),
                Registration("Crestron Database 228.55.001.00", "228.55.001.00", "Crestron Electronics Inc.", UninstallHive.Machine32),
                Registration("Crestron Device Database200.460.001.00", "200.460.001.00", "Crestron Electronics Inc.", UninstallHive.Machine32),
                Registration("Crestron MasterInstaller", "4.00.11", "Crestron Electronics Inc.", UninstallHive.Machine32),
                Registration("Extron - Toolbelt", "2.35.0.14", "Extron", UninstallHive.Machine32),
                Registration("BiampCanvas", "3.2.0", "Biamp Systems")
            ],
            [new WinGetPackageEvidence("7zip.7zip", "26.03"), new WinGetPackageEvidence("Proton.ProtonVPN", "5.1.8")]);
        var document = WorkstationInventoryDocumentCodec.Parse(bytes);
        var session = MigrationSession.FromInventory(document, "fixture", MigrationFixtures.Now);
        foreach (var id in new[] { "Crestron.Toolbox", "Crestron.Database", "Crestron.DeviceDatabase", "Crestron.MasterInstaller", "Extron.Toolbelt", "Biamp.Canvas" })
        {
            var application = document.Applications.Single(item => item.CatalogId == id);
            Assert.IsNull(application.WinGet, id);
            Assert.AreEqual(MigrationRelevance.Application, application.Relevance, id);
            Assert.IsTrue(session.Items.Single(item => item.Application.CatalogId == id).Included, id);
        }
    }

    [TestMethod]
    public async Task ScenarioE_ImportedArbitraryWinGetIdStaysDescriptiveInTheWindow()
    {
        var target = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult());
        var actions = new WorkstationMigrationTests.RecordingActionStore();
        var (viewModel, _, _, files) = Create(new QueuePlanning(target.Plan), actions);
        files.Open(SourceFile([], [new WinGetPackageEvidence("Some.Arbitrary.Package", "1.0")]));
        await viewModel.ImportInventoryAsync();

        var row = viewModel.VisibleItems.Single();
        StringAssert.Contains(row.IdentityText, "Some.Arbitrary.Package");
        Assert.IsFalse(row.CanInstall);
        Assert.IsFalse(viewModel.InstallItemCommand.CanExecute(row));
        Assert.IsFalse(viewModel.CanInstallAll);
        await viewModel.InstallAsync([row]);
        Assert.IsNull(actions.Request);
    }

    [TestMethod]
    public async Task ScenarioF_SavedProfileWithArbitraryIdsNeverReachesTheWorker()
    {
        var profile = new DeploymentProfile("field-laptop", "Field laptop", 2, string.Empty, MigrationFixtures.Now,
        [
            new ProfileApplication(string.Empty, "Arbitrary Tool", "Somebody", "Some.Arbitrary.Package"),
            new ProfileApplication("Some.Arbitrary.Package", string.Empty),
            new ProfileApplication("RealVNC.VNCViewer", "RealVNC Viewer"),
            new ProfileApplication("7zip.7zip", "7-Zip")
        ], []);
        var reloaded = DeploymentProfileCodec.Parse(DeploymentProfileCodec.Serialize(profile));
        var missing = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult());
        var actions = new WorkstationMigrationTests.RecordingActionStore();
        var store = new WorkstationMigrationTests.MemorySessionStore();
        var service = WorkstationMigrationTests.Service(missing.Plan, actions, store);
        service.StartFromProfile(reloaded);
        service.Accept(missing.Plan);

        var items = service.Checklist!.Items.ToDictionary(item => item.Desired.Spec.DisplayName);
        Assert.AreEqual(ChecklistStatus.UnknownApplication, items["Arbitrary Tool"].Status);
        Assert.AreEqual(ChecklistStatus.UnknownApplication, items["Some.Arbitrary.Package"].Status);
        Assert.AreEqual(ChecklistStatus.ManualInstall, items["RealVNC Viewer"].Status, "A managed record on hold has no execution authority.");
        Assert.IsFalse(items["RealVNC Viewer"].CatalogState!.Package.HasManagedExecutionAuthority);
        CollectionAssert.AreEqual(new[] { "7-Zip" }, items.Values.Where(item => item.CanInstallAutomatically).Select(item => item.Desired.Spec.DisplayName).ToArray());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InstallAsync([items["Arbitrary Tool"].ItemId, items["Some.Arbitrary.Package"].ItemId, items["RealVNC Viewer"].ItemId], riskAcknowledged: true));
        Assert.IsNull(actions.Request);
        await service.InstallAsync(items.Values.Select(item => item.ItemId).ToArray(), riskAcknowledged: true);
        CollectionAssert.AreEqual(new[] { "7zip.7zip" }, actions.Request!.PackageIds.ToArray(), "Only the trusted managed entry reaches the coordinator.");
    }

    [TestMethod]
    public async Task OldPcDeselectionTravelsWithTheExportWithoutDroppingEvidence()
    {
        var source = await WorkstationMigrationTests.Target(
            [
                Toolbox(),
                new RegistryUninstallRecord(RegistryInventorySource.Hklm64, "Old Utility", "1.0", "Old Vendor", "OldUtility"),
                new RegistryUninstallRecord(RegistryInventorySource.Hklm32, "Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.44.35211", "14.44.35211.0", "Microsoft Corporation", "{0b5169e3-39da-4313-808e-1f9c0407f3bf}")
            ],
            MigrationFixtures.WinGetResult());
        var (oldPc, _, _, oldFiles) = Create(new QueuePlanning(source.Plan));
        await oldPc.InitializeAsync(source.Plan);
        Assert.IsTrue(oldPc.ReviewVisible);
        CollectionAssert.AreEquivalent(new[] { "Crestron Toolbox 3.1390.0008.3", "Old Utility" }, oldPc.ReviewItems.Select(item => item.Name).ToArray());
        oldPc.ReviewItems.Single(item => item.Name == "Old Utility").Migrate = false;
        oldPc.ShowSupportingComponents = true;
        oldPc.ReviewItems.Single(item => item.Name.StartsWith("Microsoft Visual C++", StringComparison.Ordinal)).Migrate = true;
        StringAssert.Contains(oldPc.ReviewSummary, "1 left out");
        oldFiles.SavePath = @"C:\fixture\old.json";
        await oldPc.ExportInventoryAsync();

        var exported = WorkstationInventoryDocumentCodec.Parse(oldFiles.Files[oldFiles.SavePath]);
        Assert.IsFalse(exported.Applications.Single(item => item.DisplayName == "Old Utility").SelectedForMigration);
        Assert.IsTrue(exported.Applications.Single(item => item.DisplayName.StartsWith("Microsoft Visual C++", StringComparison.Ordinal)).SelectedForMigration);
        Assert.IsTrue(exported.Applications.Single(item => item.CatalogId == "Crestron.Toolbox").SelectedForMigration);

        var target = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult());
        var (newPc, service, _, newFiles) = Create(new QueuePlanning(target.Plan));
        newFiles.Open(oldFiles.Files[oldFiles.SavePath]);
        await newPc.ImportInventoryAsync();
        CollectionAssert.AreEquivalent(new[] { "Crestron Toolbox", "Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.44.35211" }, Names(newPc).ToArray());
        Assert.AreEqual(1, service.Checklist!.Summary.Excluded, "A deselected application arrives as a user exclusion.");
        newPc.Filter = MigrationFilter.Excluded;
        Assert.AreEqual("Old Utility", newPc.VisibleItems.Single().Name);
    }

    [TestMethod]
    public void SummaryLeadsWithRemainingAndSeparatesInstalledExcludedAndReview()
    {
        var identities = MigrationFixtures.Identities;
        DesiredApplication Desired(string id, string name, bool included, MigrationRelevance relevance = MigrationRelevance.Application) =>
            new(id, new DesiredApplicationSpec(name, "1.0", "Vendor", string.Empty, string.Empty, string.Empty, string.Empty, [], relevance),
                identities.ResolveDisplayName(name), included);
        ReconciledApplication Item(DesiredApplication desired, ChecklistStatus status) =>
            new(desired, status, string.Empty, string.Empty, VersionDifference.Unknown, string.Empty, null, false);

        var summary = ChecklistSummary.From(
        [
            Item(Desired("a", "A", true), ChecklistStatus.Installed),
            Item(Desired("b", "B", true), ChecklistStatus.Installed),
            Item(Desired("c", "C", true), ChecklistStatus.UnknownApplication),
            Item(Desired("d", "D", true), ChecklistStatus.NeedsReview),
            Item(Desired("e", "E", false), ChecklistStatus.Excluded),
            Item(Desired("f", "F runtime", false, MigrationRelevance.SupportComponent), ChecklistStatus.Excluded)
        ]);

        Assert.AreEqual(5, summary.Imported);
        Assert.AreEqual(2, summary.Satisfied);
        Assert.AreEqual(2, summary.Remaining);
        Assert.AreEqual(1, summary.Excluded);
        Assert.AreEqual(1, summary.NeedsReview);
        Assert.AreEqual(1, summary.Supporting);
    }

    [TestMethod]
    public void UpgradeCodesGroupOneVendorsProductFamilyButNeverMergeVendors()
    {
        const string sharedCode = "{5A6B7C8D-0000-4000-8000-000000000123}";
        var inventory = MigrationFixtures.Inventory(
        [
            Registration("Vendor Suite 1.0", "1.0", "Vendor Corp", key: "{11111111-0000-4000-8000-000000000001}", windowsInstaller: true, upgradeCode: sharedCode),
            Registration("Vendor Suite 2.0", "2.0", "Vendor Corp", key: "{22222222-0000-4000-8000-000000000002}", windowsInstaller: true, upgradeCode: sharedCode),
            Registration("Different Product", "5.0", "Other Company", key: "{33333333-0000-4000-8000-000000000003}", windowsInstaller: true, upgradeCode: sharedCode)
        ], []);

        var suite = inventory.Applications.Single(item => item.Publisher == "Vendor Corp");
        Assert.HasCount(2, suite.Registrations);
        CollectionAssert.AreEquivalent(new[] { "1.0", "2.0" }, suite.Versions.ToArray());
        Assert.AreEqual("2.0", suite.DisplayVersion);
        Assert.AreEqual(sharedCode, suite.MsiUpgradeCode);
        Assert.AreEqual("Different Product", inventory.Applications.Single(item => item.Publisher == "Other Company").DisplayName);
    }

    [TestMethod]
    public void WindowsInstallerMetadataIsReadFromTheRegistryOnly()
    {
        var root = MigrationFixtures.RepositoryRoot();
        var sources = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        foreach (var path in sources)
        {
            var text = File.ReadAllText(path);
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(text, @"Win32_Product|\bMsi(?:Enum|Query|GetProductInfo|OpenProduct|Reinstall|ConfigureProduct)|msi\.dll"),
                $"Windows Installer API or WMI enumeration appeared in {Path.GetFileName(path)}.");
        }
        var provider = File.ReadAllText(Path.Combine(root, "src", "AVWorkstationToolkit.Infrastructure.Windows", "Registry", "WindowsUninstallRegistryInventory.cs"));
        Assert.IsFalse(provider.Contains("writable: true", StringComparison.Ordinal));
        Assert.IsFalse(AVWorkstationToolkit.Infrastructure.Windows.Registry.MsiPackedGuid.TryUnpack(new string('Z', 32), out _));
        Assert.IsFalse(AVWorkstationToolkit.Infrastructure.Windows.Registry.MsiPackedGuid.TryUnpack("96F071321C04207200000000400000", out _));
    }

    [TestMethod]
    public void ClassificationKeepsVendorToolsAndSuppressesOnlyClearSupportEntries()
    {
        var inventory = MigrationFixtures.Inventory(
        [
            Registration("Crestron Xpanel Uninstall", "1.0.8", "Crestron Electronics, Inc.", UninstallHive.Machine32),
            Registration("Crestron CCS-UC-SB-1 DFU Driver v1.11.1", "1.11.1", "Crestron", UninstallHive.Machine32),
            Registration("Windows Driver Package - Crestron Electronics Inc. (WinUSB) Crestron  (01/23/2018 3.0.0.0)", "01/23/2018 3.0.0.0", "Crestron Electronics Inc."),
            Registration("Microsoft Edge", "153.0", "Microsoft Corporation", UninstallHive.Machine32),
            Registration("Google Update Helper", "1.3", "Google LLC", UninstallHive.Machine32),
            Registration("Vendor Product Update", "1.0", "Vendor Corp", key: "KB1", parentKey: "VendorProduct")
        ], []);
        MigrationRelevance Relevance(string prefix) => inventory.Applications.Single(item => item.DisplayName.StartsWith(prefix, StringComparison.Ordinal)).Relevance;

        Assert.AreEqual(MigrationRelevance.Application, Relevance("Crestron Xpanel Uninstall"), "A product registered with an odd name is still the product.");
        Assert.AreEqual(MigrationRelevance.Application, Relevance("Crestron CCS-UC-SB-1 DFU Driver"), "A driver a technician installs on purpose stays in the migration.");
        Assert.AreEqual(MigrationRelevance.SupportComponent, Relevance("Windows Driver Package"));
        Assert.AreEqual(MigrationRelevance.SupportComponent, Relevance("Microsoft Edge"));
        Assert.AreEqual(MigrationRelevance.SupportComponent, Relevance("Google Update Helper"));
        Assert.AreEqual(MigrationRelevance.Update, Relevance("Vendor Product Update"));
        Assert.IsTrue(inventory.Applications.Where(item => item.Relevance != MigrationRelevance.Application).All(item => item.RelevanceReason.Length > 0));
    }

    private static UninstallRegistration Registration(
        string name, string version, string publisher, UninstallHive hive = UninstallHive.Machine64, string key = "",
        bool systemComponent = false, bool windowsInstaller = false, string parentKey = "", string upgradeCode = "") =>
        MigrationFixtures.Registration(name, version, publisher, hive, key, systemComponent, windowsInstaller, parentKey, upgradeCode);

    private static byte[] SourceFile(params UninstallRegistration[] registrations) => SourceFile(registrations, []);

    private static byte[] SourceFile(UninstallRegistration[] registrations, WinGetPackageEvidence[] winGet) =>
        WorkstationInventoryDocumentCodec.Serialize(MigrationFixtures.Inventory(registrations, winGet), "fixture");

    private static IReadOnlyList<string> Names(MigrationViewModel viewModel) => viewModel.VisibleItems.Select(item => item.Name).ToArray();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++) await Task.Delay(10);
        Assert.IsTrue(condition(), "The operation did not finish in time.");
    }

    private static (MigrationViewModel ViewModel, WorkstationMigrationService Service, WorkstationMigrationTests.MemorySessionStore Store, FakeFiles Files) Create(
        IWorkstationPlanningCoordinator planning, WorkstationMigrationTests.RecordingActionStore? actions = null)
    {
        var store = new WorkstationMigrationTests.MemorySessionStore();
        var coordinator = actions is null ? null : new CompiledActionCoordinator(actions, new WorkstationMigrationTests.ImmediateLauncher(), planning,
            pollInterval: TimeSpan.FromMilliseconds(1), resultTimeout: TimeSpan.FromSeconds(10));
        var service = new WorkstationMigrationService(planning, MigrationFixtures.InventoryService(), store, coordinator,
            new WorkstationMigrationTests.FixedTime(MigrationFixtures.Now));
        var files = new FakeFiles();
        return (new MigrationViewModel(service, files, "1.1.2"), service, store, files);
    }

    private sealed class QueuePlanning(params WorkstationPlan[] plans) : IWorkstationPlanningCoordinator
    {
        private int index;
        public Task<WorkstationPlan> RefreshAsync(IProgress<PlanningRefreshStage>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(plans[Math.Min(index++, plans.Length - 1)]);
    }

    private sealed class FakeFiles : IMigrationFileService
    {
        private const string OpenPath = @"C:\fixture\import.json";
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? SavePath { get; set; }
        public void Open(byte[] content) => Files[OpenPath] = content;
        public string? PickInventoryToSave(string suggestedName) => SavePath;
        public string? PickInventoryToOpen() => Files.ContainsKey(OpenPath) ? OpenPath : null;
        public string? PickProfileToOpen() => null;
        public string? PickProfileToSave(string suggestedName) => SavePath;
        public bool Confirm(string title, string message) => true;
        public byte[] Read(string path, int maximumBytes) => Files[path];
        public void Write(string path, byte[] content) => Files[path] = content;
    }
}
