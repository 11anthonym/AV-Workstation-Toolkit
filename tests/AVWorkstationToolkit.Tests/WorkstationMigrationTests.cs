using System.Text;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Application.Workstation;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Domain.Workstation;
using AVWorkstationToolkit.Infrastructure.Windows.Migration;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class WorkstationMigrationTests
{
    private const string Generator = "AV Workstation Toolkit 1.1.2";

    [TestMethod]
    public void InventoryRoundTripKeepsIdentitiesAndRawEvidence()
    {
        var inventory = SourceInventory();
        var parsed = WorkstationInventoryDocumentCodec.Parse(WorkstationInventoryDocumentCodec.Serialize(inventory, Generator));

        Assert.AreEqual(Generator, parsed.Generator);
        Assert.AreEqual(inventory.CapturedAtUtc, parsed.CapturedAtUtc);
        Assert.AreEqual(inventory.Machine, parsed.Machine);
        Assert.AreEqual(EvidenceQuality.Complete, parsed.Sources.Registry);
        Assert.HasCount(inventory.Applications.Count, parsed.Applications);
        foreach (var (original, read) in inventory.Applications.Zip(parsed.Applications))
        {
            Assert.AreEqual(original.DisplayName, read.DisplayName);
            Assert.AreEqual(original.DisplayVersion, read.DisplayVersion);
            Assert.AreEqual(original.Publisher, read.Publisher);
            Assert.AreEqual(original.Scope, read.Scope);
            Assert.AreEqual(original.Architecture, read.Architecture);
            Assert.AreEqual(original.Relevance, read.Relevance);
            Assert.AreEqual(original.CatalogId, read.CatalogId);
            Assert.AreEqual(original.WinGetId, read.WinGet?.Id ?? string.Empty);
            Assert.AreEqual(original.MsiUpgradeCode, read.MsiUpgradeCode);
            CollectionAssert.AreEqual(original.Registrations.ToArray(), read.Registrations.ToArray(), original.DisplayName);
        }
        var toolbox = parsed.Applications.Single(item => item.CatalogId == "Crestron.Toolbox");
        Assert.IsNull(toolbox.WinGet);
        Assert.AreEqual(WinGetCorrelation.CatalogIdentity, parsed.Applications.Single(item => item.CatalogId == "WiresharkFoundation.Wireshark").WinGetCorrelation);
    }

    [TestMethod]
    public void InventoryParserRejectsInvalidDocumentsClearly()
    {
        var valid = Encoding.UTF8.GetString(WorkstationInventoryDocumentCodec.Serialize(SourceInventory(), Generator));
        AssertRejected("{ not json", "isn't valid JSON");
        AssertRejected(valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal), "newer than this version");
        AssertRejected(valid.Replace("\"schemaVersion\": 1,", string.Empty, StringComparison.Ordinal), "no valid schemaVersion");
        AssertRejected(valid.Replace("\"documentType\": \"workstation-inventory\"", "\"documentType\": \"workstation-profile\"", StringComparison.Ordinal),
            "is a deployment profile, not a workstation inventory");
        AssertRejected(valid.Replace("\"documentType\": \"workstation-inventory\"", "\"documentType\": \"shopping-list\"", StringComparison.Ordinal),
            "isn't a workstation inventory");
        AssertRejected(valid.Replace("\"generator\"", "\"installCommand\": \"cmd /c calc\", \"generator\"", StringComparison.Ordinal), "unsupported field 'installCommand'");
        AssertRejected(valid.Replace("\"generator\"", "\"capturedAtUtc\": \"2026-01-01T00:00:00Z\", \"generator\"", StringComparison.Ordinal), "repeats the field");
        AssertRejected(valid.Replace("\"id\": \"WiresharkFoundation.Wireshark\"", "\"id\": \"Bad Id; rm -rf\"", StringComparison.Ordinal), "isn't a valid package ID");
        AssertRejected(valid.Replace("\"displayName\": \"Vendor Widget Configuration Tool\"", "\"displayName\": \"Vendor\\u0007Tool\"", StringComparison.Ordinal), "control characters");
        Assert.Throws<WorkstationDocumentException>(() => WorkstationInventoryDocumentCodec.Parse(new byte[WorkstationInventoryDocumentCodec.MaximumBytes + 1]));
        Assert.Throws<WorkstationDocumentException>(() => WorkstationInventoryDocumentCodec.Parse(ReadOnlySpan<byte>.Empty));

        static void AssertRejected(string json, string expected)
        {
            var exception = Assert.Throws<WorkstationDocumentException>(() => WorkstationInventoryDocumentCodec.Parse(Encoding.UTF8.GetBytes(json)));
            StringAssert.Contains(exception.Message, expected);
        }
    }

    [TestMethod]
    public async Task ImportedWinGetIdOutsideTheManagedCatalogCannotCauseInstallation()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "documentType": "workstation-inventory",
          "capturedAtUtc": "2026-09-25T12:00:00Z",
          "applications": [
            { "displayName": "Totally Legit Tool", "relevance": "application", "registrations": [], "winget": { "id": "Some.Arbitrary.Package", "version": "1.0" } },
            { "displayName": "Another Tool", "catalogId": "Some.Arbitrary.Package", "relevance": "application", "registrations": [] },
            { "displayName": "TeamViewer", "relevance": "application", "registrations": [], "winget": { "id": "TeamViewer.TeamViewer" } },
            { "displayName": "7-Zip 24.08 (x64)", "relevance": "application", "registrations": [], "winget": { "id": "7zip.7zip", "version": "24.08" } }
          ]
        }
        """;
        var document = WorkstationInventoryDocumentCodec.Parse(Encoding.UTF8.GetBytes(json));
        var target = await Target([], MigrationFixtures.WinGetResult());
        var actions = new RecordingActionStore();
        var service = Service(target.Plan, actions);
        service.StartFromInventory(document);
        service.Accept(target.Plan);
        var items = service.Checklist!.Items;

        foreach (var arbitrary in items.Where(item => item.Desired.Spec.DisplayName != "7-Zip 24.08 (x64)"))
        {
            Assert.AreEqual(ChecklistStatus.UnknownApplication, arbitrary.Status, arbitrary.DisplayName);
            Assert.IsFalse(arbitrary.CanInstallAutomatically, arbitrary.DisplayName);
            Assert.IsNull(arbitrary.CatalogState, arbitrary.DisplayName);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync([arbitrary.ItemId], riskAcknowledged: true));
        }
        Assert.IsNull(actions.Request, "No request may be written for an app outside the managed catalog.");

        // The worker-facing authorization rejects the arbitrary ID even if a caller bypasses the checklist.
        var request = new ActionRequestFactory().Create(ManagedRequestAction.Install, ["Some.Arbitrary.Package"], true, false);
        var rejection = Assert.Throws<ActionRequestValidationException>(() => new ActionRequestAuthorizationService().Authorize(request, target.Plan));
        Assert.AreEqual(ActionRequestFailure.PackageNotInPlan, rejection.Failure);

        var sevenZip = items.Single(item => item.Desired.Spec.DisplayName == "7-Zip 24.08 (x64)");
        Assert.AreEqual(ChecklistStatus.ReadyToInstall, sevenZip.Status);
        Assert.IsTrue(sevenZip.CanInstallAutomatically);
        await service.InstallAsync([sevenZip.ItemId], riskAcknowledged: false);
        CollectionAssert.AreEqual(new[] { "7zip.7zip" }, actions.Request!.PackageIds.ToArray());
    }

    [TestMethod]
    public async Task CrestronToolboxStaysManualUntilTheRegistryShowsItThenLeavesRemaining()
    {
        var session = MigrationSession.FromInventory(Exported(SourceInventory()), "fixture", MigrationFixtures.Now);
        var before = await Target([], MigrationFixtures.WinGetResult());
        var toolbox = Reconcile(session, before).Single(item => item.Desired.Identity.Application?.Id == "Crestron.Toolbox");
        Assert.AreEqual(ChecklistStatus.ManualInstall, toolbox.Status);
        Assert.IsFalse(toolbox.CanInstallAutomatically);
        StringAssert.Contains(toolbox.Detail, "Get package");

        var after = await Target([new RegistryUninstallRecord(RegistryInventorySource.Hklm32, "Crestron Toolbox 3.1400.0001.0", "3.1400.0001.0", "Crestron Electronics Inc.", "{1B52}_is1")],
            MigrationFixtures.WinGetResult());
        var reconciled = Reconcile(session, after);
        var installed = reconciled.Single(item => item.Desired.Identity.Application?.Id == "Crestron.Toolbox");
        Assert.AreEqual(ChecklistStatus.Installed, installed.Status);
        Assert.AreEqual(VersionDifference.Newer, installed.VersionDifference);
        var summary = ChecklistSummary.From(reconciled);
        Assert.AreEqual(ChecklistSummary.From(Reconcile(session, before)).Remaining - 1, summary.Remaining);
    }

    [TestMethod]
    public async Task ReconciliationClassifiesEachDesiredApplication()
    {
        var session = MigrationSession.FromInventory(Exported(SourceInventory()), "fixture", MigrationFixtures.Now);
        var target = await Target(
            [
                new RegistryUninstallRecord(RegistryInventorySource.Hklm64, "Wireshark 4.2.0 x64", "4.2.0", "Wireshark", "Wireshark"),
                new RegistryUninstallRecord(RegistryInventorySource.Hklm64, "Vendor Widget Configuration Tool", "4.3", "Vendor Corp.", "VendorWidgetTool2")
            ],
            MigrationFixtures.WinGetResult(("WiresharkFoundation.Wireshark", "4.2.0")));
        var items = Reconcile(session, target).ToDictionary(item => item.Desired.Spec.DisplayName);

        var wireshark = items["Wireshark 4.4.0 x64"];
        Assert.AreEqual(ChecklistStatus.Installed, wireshark.Status);
        Assert.AreEqual(VersionDifference.Older, wireshark.VersionDifference);
        StringAssert.Contains(wireshark.Detail, "older");

        var unknown = items["Vendor Widget Configuration Tool"];
        Assert.AreEqual(ChecklistStatus.Installed, unknown.Status, "A conservative name and publisher match satisfies an unknown app.");
        StringAssert.Contains(unknown.MatchEvidence, "name and publisher");

        Assert.AreEqual(ChecklistStatus.ReadyToInstall, items["7-Zip 26.03 (x64 edition)"].Status);
        Assert.AreEqual(ChecklistStatus.ManualInstall, items["Extron Electronics - Toolbelt"].Status);
        Assert.AreEqual(ChecklistStatus.UnknownApplication, items["Proton VPN"].Status);
        StringAssert.Contains(items["Proton VPN"].Detail, "Proton.ProtonVPN");
        Assert.AreEqual(ChecklistStatus.Excluded, items.Values.Single(item => item.Desired.Spec.DisplayName.StartsWith("Microsoft Visual C++", StringComparison.Ordinal)).Status);
    }

    [TestMethod]
    public async Task RiskAndRebootAndInventoryGapsAreRespected()
    {
        var spec = Spec("Wireshark 4.4.0 x64", catalogId: "WiresharkFoundation.Wireshark");
        var desired = new[] { new DesiredApplication("item-1", spec, MigrationFixtures.Identities.Resolve(spec.CatalogId, "", spec.DisplayName), true) };
        var service = new ApplicationReconciliationService();

        var clear = await Target([], MigrationFixtures.WinGetResult());
        var ready = service.Reconcile(desired, clear.Reconciliation).Single();
        Assert.AreEqual(ChecklistStatus.ReadyToInstall, ready.Status);
        Assert.IsTrue(ready.CanInstallAutomatically);
        StringAssert.Contains(ready.Detail, "installs a driver");

        var rebootPending = await Target([], MigrationFixtures.WinGetResult(), rebootPending: true);
        var blocked = service.Reconcile(desired, rebootPending.Reconciliation).Single();
        Assert.AreEqual(ChecklistStatus.ReadyToInstall, blocked.Status);
        Assert.IsFalse(blocked.CanInstallAutomatically);
        StringAssert.Contains(blocked.Detail, "Restart Windows");

        var winGetDown = await Target([], new InstalledPackageInventoryResult(ProviderQuality.Unavailable, ProviderFailureKind.ProviderUnavailable, [], "unavailable", string.Empty));
        Assert.AreEqual(ChecklistStatus.CheckUnavailable, service.Reconcile(desired, winGetDown.Reconciliation).Single().Status);

        var unknownSpec = Spec("Vendor Widget Configuration Tool", publisher: "Vendor Corp");
        var unknown = new[] { new DesiredApplication("item-2", unknownSpec, IdentityResolution.Unidentified, true) };
        var partial = await Target([], MigrationFixtures.WinGetResult(), registryQuality: ProviderQuality.Partial);
        Assert.AreEqual(ChecklistStatus.CheckUnavailable, service.Reconcile(unknown, partial.Reconciliation).Single().Status);
        Assert.AreEqual(ChecklistStatus.UnknownApplication, service.Reconcile(unknown, clear.Reconciliation).Single().Status);
    }

    [TestMethod]
    public async Task InstallerIdentitiesMatchConservativelyAndOnlyUndetectableAppsAcceptConfirmation()
    {
        var service = new ApplicationReconciliationService();
        var target = await Target(
            [
                new RegistryUninstallRecord(RegistryInventorySource.Hklm64, "Renamed Suite 2026", "26.0", "Another Name Ltd", "{AAAAAAAA-0000-0000-0000-000000000001}", WindowsInstaller: true, MsiUpgradeCode: "{BBBBBBBB-0000-0000-0000-000000000002}"),
                new RegistryUninstallRecord(RegistryInventorySource.Hklm64, "Inno Tool (new name)", "3.0", "Inno", "InnoTool_is1")
            ],
            MigrationFixtures.WinGetResult(("Proton.ProtonVPN", "5.2.0")));
        DesiredApplication Desired(DesiredApplicationSpec spec, DateTimeOffset? confirmed = null) =>
            new(spec.DisplayName, spec, MigrationFixtures.Identities.Resolve(spec.CatalogId, spec.WinGetId, spec.DisplayName), true, confirmed);

        var results = service.Reconcile(
        [
            Desired(Spec("Old Suite 2024", upgradeCode: "{BBBBBBBB-0000-0000-0000-000000000002}")),
            Desired(Spec("Unrelated Suite", publisher: "Different Vendor", upgradeCode: "{BBBBBBBB-0000-0000-0000-000000000002}")),
            Desired(Spec("Inno Tool", uninstallKeys: ["InnoTool_is1"])),
            Desired(Spec("Other Inno Tool", publisher: "Someone Else", uninstallKeys: ["InnoTool_is1"])),
            Desired(Spec("Proton VPN", winGetId: "Proton.ProtonVPN")),
            Desired(Spec("Symetrix Composer", catalogId: "Symetrix.Composer")),
            Desired(Spec("Dealer Portal Tool"), MigrationFixtures.Now),
            Desired(Spec("Allen & Heath AHM System Manager", catalogId: "AllenHeath.AHMSystemManager"), MigrationFixtures.Now),
            Desired(Spec("AHM System Manager (unconfirmed)", catalogId: "AllenHeath.AHMSystemManager")),
            Desired(Spec("AHM System Manager (registered)", catalogId: "AllenHeath.AHMSystemManager", uninstallKeys: ["AHMSystemManager_is1"]), MigrationFixtures.Now)
        ], target.Reconciliation).ToDictionary(item => item.Desired.Spec.DisplayName);

        StringAssert.Contains(results["Old Suite 2024"].MatchEvidence, "upgrade code");
        StringAssert.Contains(results["Inno Tool"].MatchEvidence, "uninstall registration");
        StringAssert.Contains(results["Proton VPN"].MatchEvidence, "WinGet package ID");
        Assert.IsTrue(new[] { "Old Suite 2024", "Inno Tool", "Proton VPN" }.All(name => results[name].Status == ChecklistStatus.Installed));
        // Installer identity never overrides disagreeing publishers.
        Assert.AreEqual(ChecklistStatus.UnknownApplication, results["Unrelated Suite"].Status);
        Assert.AreEqual(ChecklistStatus.UnknownApplication, results["Other Inno Tool"].Status);
        Assert.AreEqual(ChecklistStatus.ManualInstall, results["Symetrix Composer"].Status);
        Assert.IsFalse(results["Symetrix Composer"].CanConfirmManually, "A catalog app with a detector is completed only by detection.");

        // A stored confirmation never hides an app the workstation could detect.
        Assert.AreEqual(ChecklistStatus.UnknownApplication, results["Dealer Portal Tool"].Status);
        Assert.IsFalse(results["Dealer Portal Tool"].Satisfied);
        Assert.IsFalse(results["Dealer Portal Tool"].CanConfirmManually);
        Assert.AreEqual(ChecklistStatus.ManualInstall, results["AHM System Manager (registered)"].Status,
            "Registration evidence from the source makes the app detectable, so its confirmation is ignored.");

        // Only a catalog app with no Windows detector and no installer identity can be confirmed.
        Assert.AreEqual(ChecklistStatus.ConfirmedManually, results["Allen & Heath AHM System Manager"].Status);
        Assert.IsTrue(results["Allen & Heath AHM System Manager"].Satisfied);
        Assert.AreEqual(ChecklistStatus.ManualInstall, results["AHM System Manager (unconfirmed)"].Status);
        Assert.IsTrue(results["AHM System Manager (unconfirmed)"].CanConfirmManually);
        StringAssert.Contains(results["AHM System Manager (unconfirmed)"].Detail, "no way to detect");
    }

    [TestMethod]
    public async Task ReportedSuccessIsNotCompletionUntilARescanDetectsTheApp()
    {
        var spec = Spec("7-Zip", catalogId: "7zip.7zip");
        var identity = MigrationFixtures.Identities.Resolve(spec.CatalogId, "", spec.DisplayName);
        var attempted = new DesiredApplication("item-1", spec, identity, true, LastAttempt: new InstallAttempt(MigrationFixtures.Now, true, "ok"));
        var service = new ApplicationReconciliationService();

        var notDetected = await Target([], MigrationFixtures.WinGetResult());
        var pending = service.Reconcile([attempted], notDetected.Reconciliation).Single();
        // A reported success that no scan has confirmed is neither complete nor a failure.
        Assert.AreEqual(ChecklistStatus.InstallUnverified, pending.Status);
        Assert.IsFalse(pending.Satisfied);
        StringAssert.Contains(pending.Detail, "no scan has detected");
        Assert.IsTrue(pending.CanInstallAutomatically, "An unverified attempt can be retried through the same authority.");

        var detected = await Target([], MigrationFixtures.WinGetResult(("7zip.7zip", "24.08")));
        Assert.AreEqual(ChecklistStatus.Installed, service.Reconcile([attempted], detected.Reconciliation).Single().Status);
        Assert.AreEqual(ChecklistStatus.Installing,
            service.Reconcile([attempted with { LastAttempt = null }], notDetected.Reconciliation, new HashSet<string> { "item-1" }).Single().Status);
    }

    [TestMethod]
    public async Task MigrationServiceInstallsThroughTheCoordinatorAndMarksDoneOnlyWhenDetected()
    {
        var before = await Target([], MigrationFixtures.WinGetResult());
        var after = await Target([], MigrationFixtures.WinGetResult(("7zip.7zip", "24.08")));
        var actions = new RecordingActionStore();
        var store = new MemorySessionStore();
        var service = Service(after.Plan, actions, store);
        service.StartFromInventory(Exported(SourceInventory()));
        Assert.IsTrue(service.Accept(before.Plan));
        var sevenZip = service.Checklist!.Items.Single(item => item.Desired.Identity.Application?.Id == "7zip.7zip");
        Assert.AreEqual(ChecklistStatus.ReadyToInstall, sevenZip.Status);

        var outcome = await service.InstallAsync([sevenZip.ItemId], riskAcknowledged: false);

        Assert.AreEqual(ActionResultStatus.Succeeded, outcome.Status);
        Assert.AreEqual(1, outcome.DetectedAfterward);
        Assert.AreEqual(ChecklistStatus.Installed, service.Checklist!.Items.Single(item => item.ItemId == sevenZip.ItemId).Status);
        Assert.IsTrue(store.Saved!.Items.Single(item => item.ItemId == sevenZip.ItemId).LastAttempt!.Succeeded);
    }

    [TestMethod]
    public async Task RefusedInstallRecordsNoAttemptAndLeavesNothingInstalling()
    {
        var before = await Target([], MigrationFixtures.WinGetResult());
        var actions = new RecordingActionStore();
        var store = new MemorySessionStore();
        var service = Service(before.Plan, actions, store);
        service.StartFromInventory(Exported(SourceInventory()));
        service.Accept(before.Plan);
        var wireshark = service.Checklist!.Items.Single(item => item.Desired.Identity.Application?.Id == "WiresharkFoundation.Wireshark");

        // A driver-bearing package without acknowledgement is refused by the coordinator's authorization, as in the main window.
        var refusal = await Assert.ThrowsAsync<ActionRequestValidationException>(() => service.InstallAsync([wireshark.ItemId], riskAcknowledged: false));

        Assert.AreEqual(ActionRequestFailure.PackageNotEligible, refusal.Failure);
        Assert.IsNull(actions.Request);
        Assert.AreEqual(ChecklistStatus.ReadyToInstall, service.Checklist!.Items.Single(item => item.ItemId == wireshark.ItemId).Status);
        Assert.IsNull(store.Saved!.Items.Single(item => item.ItemId == wireshark.ItemId).LastAttempt);
    }

    [TestMethod]
    public void SessionImportSelectsApplicationsAndKeepsComponentsOutOfTheDefaultList()
    {
        var document = Exported(SourceInventory());
        var session = MigrationSession.FromInventory(document, "fixture", MigrationFixtures.Now);

        Assert.AreEqual("SOURCE-PC", session.Source.Label);
        Assert.IsTrue(session.Items.Where(item => item.Application.Relevance == MigrationRelevance.Application).All(item => item.Included));
        Assert.IsTrue(session.Items.Where(item => item.Application.Relevance == MigrationRelevance.SupportComponent).All(item => !item.Included));
        Assert.IsFalse(session.Items.Any(item => item.Application.Relevance is MigrationRelevance.SystemComponent or MigrationRelevance.Update));
        Assert.AreEqual(document.Applications.Count(item => item.Relevance is MigrationRelevance.SystemComponent or MigrationRelevance.Update), session.SkippedComponentCount);
        Assert.IsTrue(session.Items.Any(item => item.Application.CatalogId == "Crestron.Toolbox" && item.Included));
        Assert.IsTrue(session.Items.Any(item => item.Application.DisplayName == "Vendor Widget Configuration Tool" && item.Included));
    }

    [TestMethod]
    public void SessionDeselectRemoveRestoreAndPersistenceRoundTrip()
    {
        var session = MigrationSession.FromInventory(Exported(SourceInventory()), "migration-fixture", MigrationFixtures.Now);
        var first = session.Items[0].ItemId;
        var second = session.Items[1].ItemId;
        var later = MigrationFixtures.Now.AddHours(1);

        session = session.SetIncluded(first, false, later);
        Assert.IsFalse(session.Items.Single(item => item.ItemId == first).Included);
        session = session.SetIncluded(first, true, later);
        Assert.IsTrue(session.Items.Single(item => item.ItemId == first).Included);
        session = session.Remove(second, later);
        Assert.IsFalse(session.Items.Any(item => item.ItemId == second));
        Assert.AreEqual(1, session.RemovedCount);
        Assert.Throws<KeyNotFoundException>(() => session.SetIncluded(second, true, later));
        session = session.Confirm(first, true, later).RecordAttempt(first, new InstallAttempt(later, false, "exit 1603"), later);

        var root = Path.Combine(Path.GetTempPath(), $"awt-migration-{Guid.NewGuid():N}");
        try
        {
            var store = new MigrationSessionFileStore(root);
            Assert.IsNull(store.Load());
            store.Save(session);
            var loaded = store.Load()!;
            Assert.AreEqual(session.SessionId, loaded.SessionId);
            Assert.AreEqual(session.Source, loaded.Source);
            Assert.AreEqual(session.RemovedCount, loaded.RemovedCount);
            Assert.AreEqual(session.SkippedComponentCount, loaded.SkippedComponentCount);
            Assert.HasCount(session.Items.Count, loaded.Items);
            foreach (var (expected, actual) in session.Items.Zip(loaded.Items))
            {
                Assert.AreEqual(expected.ItemId, actual.ItemId);
                Assert.AreEqual(expected.Included, actual.Included);
                Assert.AreEqual(expected.ConfirmedAtUtc, actual.ConfirmedAtUtc);
                Assert.AreEqual(expected.LastAttempt, actual.LastAttempt);
                Assert.AreEqual(expected.Application with { UninstallKeys = [] }, actual.Application with { UninstallKeys = [] });
                CollectionAssert.AreEqual(expected.Application.UninstallKeys.ToArray(), actual.Application.UninstallKeys.ToArray());
            }
            Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(root, "migration"), "*.tmp").Any());

            File.WriteAllText(store.SessionPath, "{\"schemaVersion\":1,\"documentType\":\"migration-session\",\"sessionId\":\"x\",\"unexpected\":true}");
            Assert.Throws<WorkstationDocumentException>(() => store.Load());
            store.Delete();
            Assert.IsNull(store.Load());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ProfileRoundTripValidationAndRevisionPreservation()
    {
        var profile = JumpPc(3);
        var parsed = DeploymentProfileCodec.Parse(DeploymentProfileCodec.Serialize(profile));
        Assert.AreEqual(profile.ProfileId, parsed.ProfileId);
        Assert.AreEqual(3, parsed.ProfileVersion);
        Assert.AreEqual(profile.Name, parsed.Name);
        CollectionAssert.AreEqual(profile.Applications.ToArray(), parsed.Applications.ToArray());
        CollectionAssert.AreEqual(profile.Checks.ToArray(), parsed.Checks.ToArray());
        Assert.AreEqual("remote-support-jump-pc", ProfileKeys.Slug("Remote Support Jump PC!"));

        Assert.Throws<WorkstationDocumentException>(() => DeploymentProfileCodec.Serialize(profile with { Applications = [.. profile.Applications, profile.Applications[0]] }));
        Assert.Throws<WorkstationDocumentException>(() => DeploymentProfileCodec.Serialize(profile with { ProfileId = "Not A Slug" }));
        Assert.Throws<WorkstationDocumentException>(() => DeploymentProfileCodec.Serialize(profile with { ProfileVersion = 0 }));
        var newer = Encoding.UTF8.GetString(DeploymentProfileCodec.Serialize(profile)).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 7", StringComparison.Ordinal);
        StringAssert.Contains(Assert.Throws<WorkstationDocumentException>(() => DeploymentProfileCodec.Parse(Encoding.UTF8.GetBytes(newer))).Message, "newer");
        var withCommand = Encoding.UTF8.GetString(DeploymentProfileCodec.Serialize(profile)).Replace("\"text\": \"Verify sleep is disabled\"",
            "\"text\": \"Verify sleep is disabled\", \"command\": \"powercfg /change standby-timeout-ac 0\"", StringComparison.Ordinal);
        StringAssert.Contains(Assert.Throws<WorkstationDocumentException>(() => DeploymentProfileCodec.Parse(Encoding.UTF8.GetBytes(withCommand))).Message, "unsupported field 'command'");

        var diff = DeploymentProfileRevision.Compare(JumpPc(3), JumpPc(4));
        Assert.AreEqual(3, diff.FromVersion);
        Assert.AreEqual(4, diff.ToVersion);
        CollectionAssert.AreEqual(new[] { "Mozilla Firefox" }, diff.AddedApplications.ToArray());
        CollectionAssert.AreEqual(new[] { "Adobe Acrobat Reader" }, diff.RemovedApplications.ToArray());
        CollectionAssert.AreEqual(new[] { "Verify customer VPN if applicable" }, diff.AddedChecks.ToArray());
    }

    [TestMethod]
    public async Task AppliedProfileReconcilesThroughTheSameEngineAndAdoptsARevision()
    {
        var identities = MigrationFixtures.Identities;
        var session = MigrationSession.FromProfile(JumpPc(3), identities, "profile-fixture", MigrationFixtures.Now);
        Assert.AreEqual(MigrationSourceKind.Profile, session.Source.Kind);
        Assert.AreEqual(3, session.Source.ProfileVersion);
        Assert.HasCount(4, session.Tasks);
        Assert.IsFalse(session.Items.Single(item => item.Application.DisplayName == "Adobe Acrobat Reader").Included, "Optional profile applications start excluded.");

        var target = await Target(
            [new RegistryUninstallRecord(RegistryInventorySource.Hklm64, "Google Chrome", "154.0", "Google LLC", "{C8B3A0BF}", WindowsInstaller: true)],
            MigrationFixtures.WinGetResult(("Google.Chrome", "154.0")));
        var items = Reconcile(session, target).ToDictionary(item => item.DisplayName);
        Assert.AreEqual(ChecklistStatus.Installed, items["Google Chrome"].Status);
        Assert.AreEqual(ChecklistStatus.UnknownApplication, items["TeamViewer Host"].Status, "Forbidden or uncatalogued products stay manual.");
        Assert.AreEqual(ChecklistStatus.UnknownApplication, items["Proactive Agent"].Status);
        Assert.AreEqual(ChecklistStatus.Excluded, items["Adobe Acrobat Reader"].Status);

        var chrome = session.Items.Single(item => item.Application.CatalogId == "Google.Chrome").ItemId;
        session = session.Confirm(chrome, true, MigrationFixtures.Now).SetTaskDone("verify-sleep", true, MigrationFixtures.Now);
        var diff = session.CompareToProfile(JumpPc(4));
        Assert.IsTrue(diff.HasChanges);
        var adopted = session.AdoptProfileRevision(JumpPc(4), identities, MigrationFixtures.Now.AddDays(1));
        Assert.AreEqual(4, adopted.Source.ProfileVersion);
        Assert.IsTrue(adopted.Items.Any(item => item.Application.CatalogId == "Mozilla.Firefox" && item.Included));
        Assert.IsFalse(adopted.Items.Any(item => item.Application.CatalogId == "Adobe.Acrobat.Reader.32-bit"));
        Assert.IsNotNull(adopted.Items.Single(item => item.ItemId == chrome).ConfirmedAtUtc, "Progress on kept applications survives a revision.");
        Assert.IsNotNull(adopted.Tasks.Single(task => task.Id == "verify-sleep").DoneAtUtc);
        Assert.IsTrue(adopted.Tasks.Any(task => task.Id == "verify-vpn"));
        Assert.AreEqual(adopted.Items.Count, adopted.Items.Select(item => item.ItemId).Distinct().Count());
    }

    internal static DeploymentProfile JumpPc(int version) => new(
        "remote-support-jump-pc",
        "Remote Support Jump PC",
        version,
        "Remote support jump workstation",
        MigrationFixtures.Now,
        version < 4
            ? [new("Google.Chrome", "Google Chrome"), new("", "TeamViewer Host", "TeamViewer", "TeamViewer.TeamViewer.Host"),
               new("", "Proactive Agent", "Proactive"), new("Adobe.Acrobat.Reader.32-bit", "Adobe Acrobat Reader", Required: false)]
            : [new("Google.Chrome", "Google Chrome"), new("", "TeamViewer Host", "TeamViewer", "TeamViewer.TeamViewer.Host"),
               new("", "Proactive Agent", "Proactive"), new("Mozilla.Firefox", "Mozilla Firefox")],
        version < 4
            ? [new("verify-proactive", "Verify Proactive enrollment"), new("verify-teamviewer", "Verify TeamViewer assignment"),
               new("verify-connectivity", "Verify remote connectivity"), new("verify-sleep", "Verify sleep is disabled")]
            : [new("verify-proactive", "Verify Proactive enrollment"), new("verify-teamviewer", "Verify TeamViewer assignment"),
               new("verify-connectivity", "Verify remote connectivity"), new("verify-sleep", "Verify sleep is disabled"),
               new("verify-vpn", "Verify customer VPN if applicable")]);

    internal static WorkstationInventory SourceInventory() => MigrationFixtures.Inventory(
    [
        MigrationFixtures.Registration("Crestron Toolbox 3.1390.0008.3", "3.1390.0008.3", "Crestron Electronics Inc.", UninstallHive.Machine32, "{1B52BC01-2F6E-4FAE-BB09-1F28D2BF1D63}_is1"),
        MigrationFixtures.Registration("Extron Electronics - Toolbelt", "2.35.0.14", "Extron", key: "{6910E638-B48D-4090-B491-9742B5CDEAD9}", windowsInstaller: true, upgradeCode: "{9A1B2C3D-0000-4000-8000-000000000001}"),
        MigrationFixtures.Registration("Wireshark 4.4.0 x64", "4.4.0", "The Wireshark developer community, https://www.wireshark.org", key: "Wireshark"),
        MigrationFixtures.Registration("7-Zip 26.03 (x64 edition)", "26.03.00.0", "Igor Pavlov", key: "{23170F69-40C1-2702-2603-000001000000}", windowsInstaller: true),
        MigrationFixtures.Registration("Vendor Widget Configuration Tool", "4.2", "Vendor Corp", key: "VendorWidgetTool"),
        MigrationFixtures.Registration("Proton VPN", "5.1.8", "Proton AG", key: "Proton VPN_is1"),
        MigrationFixtures.Registration("Microsoft .NET Runtime - 10.0.11 (x64)", "80.44.56884", "Microsoft Corporation", key: "{0B4F3EF1-06F1-46E4-B662-A33DECC02141}", systemComponent: true, windowsInstaller: true),
        MigrationFixtures.Registration("Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.44.35211", "14.44.35211.0", "Microsoft Corporation", UninstallHive.Machine32, "{0b5169e3-39da-4313-808e-1f9c0407f3bf}")
    ],
    [
        new("WiresharkFoundation.Wireshark", "4.4.0"),
        new("7zip.7zip", "26.03.00.0"),
        new("Proton.ProtonVPN", "5.1.8"),
        new("Microsoft.VCRedist.2015+.x86", "14.44.35211.0")
    ]);

    internal static InventoryDocument Exported(WorkstationInventory inventory) =>
        WorkstationInventoryDocumentCodec.Parse(WorkstationInventoryDocumentCodec.Serialize(inventory, Generator));

    private static DesiredApplicationSpec Spec(
        string name,
        string catalogId = "",
        string winGetId = "",
        string publisher = "",
        string upgradeCode = "",
        IReadOnlyList<string>? uninstallKeys = null) =>
        new(name, "1.0", publisher, string.Empty, catalogId, winGetId, upgradeCode, uninstallKeys ?? [], MigrationRelevance.Application);

    private static IReadOnlyList<ReconciledApplication> Reconcile(MigrationSession session, TargetState target) =>
        new ApplicationReconciliationService().Reconcile(session.Resolve(MigrationFixtures.Identities), target.Reconciliation);

    internal static async Task<TargetState> Target(
        IReadOnlyList<RegistryUninstallRecord> registry,
        InstalledPackageInventoryResult winGet,
        bool rebootPending = false,
        ProviderQuality registryQuality = ProviderQuality.Complete)
    {
        var plan = await MigrationFixtures.PlanAsync(MigrationFixtures.RegistryResult(registry, registryQuality), winGet, rebootPending);
        var inventory = MigrationFixtures.InventoryService().Build(plan.Evidence!);
        return new TargetState(plan, new ReconciliationTarget(plan.Packages, plan.Reboot, inventory));
    }

    internal static WorkstationMigrationService Service(WorkstationPlan refreshed, RecordingActionStore actions, MemorySessionStore? store = null)
    {
        var planning = new FixedPlanning(refreshed);
        var coordinator = new CompiledActionCoordinator(actions, new ImmediateLauncher(), planning,
            pollInterval: TimeSpan.FromMilliseconds(1), resultTimeout: TimeSpan.FromSeconds(10));
        return new WorkstationMigrationService(planning, MigrationFixtures.InventoryService(), store ?? new MemorySessionStore(), coordinator,
            new FixedTime(MigrationFixtures.Now));
    }

    internal sealed record TargetState(WorkstationPlan Plan, ReconciliationTarget Reconciliation);

    internal sealed class FixedPlanning(WorkstationPlan plan) : IWorkstationPlanningCoordinator
    {
        public Task<WorkstationPlan> RefreshAsync(IProgress<PlanningRefreshStage>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(plan);
    }

    internal sealed class MemorySessionStore : IMigrationSessionStore
    {
        public MigrationSession? Saved { get; private set; }
        public MigrationSession? Load() => Saved;
        public void Save(MigrationSession session) => Saved = session;
        public void Delete() => Saved = null;
    }

    internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// Records the persisted request and answers as the worker would: by default a verified success for every requested
    /// package, or the worker's own refusal (Blocked) or an installer failure (Failed).
    /// </summary>
    internal sealed class RecordingActionStore(ActionResultStatus outcome = ActionResultStatus.Succeeded) : IActionProtocolStore
    {
        private readonly ActionResultCodec codec = new();
        private byte[]? result;
        public ActionRequest? Request { get; private set; }

        /// <summary>While set and not yet completed, the worker has written no result, as while installers are running.</summary>
        public TaskCompletionSource? Running { get; set; }

        public Task<ActionArtifactPaths> PersistRequestAsync(AuthorizedActionRequest request, CancellationToken cancellationToken = default)
        {
            Request = request.Request;
            var id = request.Request.RequestId;
            var paths = new ActionArtifactPaths(id, $"C:\\fixture\\{id}.json", $"C:\\fixture\\{id}.progress.jsonl",
                $"C:\\fixture\\{id}.result.json", $"C:\\fixture\\{id}.cancel", $"C:\\fixture\\{id}.winget.log");
            var packages = request.Request.PackageIds.Select(package => outcome switch
            {
                ActionResultStatus.Blocked => new ActionPackageOutcome(package, package, ManagedRequestAction.Install,
                    PackageOutcomeStatus.Blocked, 3, false, null, MigrationFixtures.Now, []),
                ActionResultStatus.Failed => new ActionPackageOutcome(package, package, ManagedRequestAction.Install,
                    PackageOutcomeStatus.Failed, 1603, false, MigrationFixtures.Now, MigrationFixtures.Now, Arguments(package)),
                _ => new ActionPackageOutcome(package, package, ManagedRequestAction.Install,
                    PackageOutcomeStatus.Succeeded, 0, true, MigrationFixtures.Now, MigrationFixtures.Now, Arguments(package))
            }).ToArray();
            var (exitCode, message) = outcome switch
            {
                ActionResultStatus.Blocked => (3, "The worker blocked the request when it rechecked this PC."),
                ActionResultStatus.Failed => (1, "An installer failed."),
                _ => (0, "Succeeded")
            };
            result = codec.Serialize(new ActionFinalResult(ActionProtocolLimits.CurrentResultSchemaVersion, id, MigrationFixtures.Now, "Fixture",
                outcome, message, exitCode, request.Request.ManagedCatalogRevision, paths.RequestPath, paths.ProgressPath,
                paths.WinGetLogPath, packages), request.Request, paths);
            return Task.FromResult(paths);
        }

        public Task<bool> CreateCancellationMarkerAsync(string requestId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<byte[]?> TryReadArtifactAsync(string requestId, ActionArtifactKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(kind == ActionArtifactKind.Result && Running is not { Task.IsCompleted: false } ? result : null);

        private static string[] Arguments(string package) =>
            ["install", "--id", package, "--exact", "--source", "winget", "--accept-package-agreements", "--accept-source-agreements"];
    }

    internal sealed class ImmediateLauncher : ICompiledWorkerLauncher
    {
        public ValueTask<ICompiledWorkerSession> LaunchAsync(ActionArtifactPaths paths, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ICompiledWorkerSession>(new Session());

        private sealed class Session : ICompiledWorkerSession
        {
            public int ProcessId => 1;
            public bool HasExited => false;
            public int? ExitCode => null;
            public void Dispose() { }
        }
    }
}
