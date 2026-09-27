using System.Text;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Workstation;
using AVWorkstationToolkit.Domain.Workstation;

namespace AVWorkstationToolkit.Tests;

/// <summary>
/// A catalog ID and a WinGet ID on one imported record describe one application. When they agree, the record is that
/// application; when they name different applications, the record is neither: it needs review, nothing on this PC can
/// complete it, and neither identity can make it installable.
/// </summary>
[TestClass]
public sealed class MigrationIdentityConflictTests
{
    [TestMethod]
    public void AgreeingIdentitiesResolveToTheOneApplication()
    {
        var sevenZip = MigrationFixtures.Identities.Resolve("7zip.7zip", "7zip.7zip", "7-Zip 26.03 (x64 edition)");
        Assert.IsTrue(sevenZip.IsConfident);
        Assert.AreEqual("7zip.7zip", sevenZip.Application!.Id);

        // A WinGet ID the catalog doesn't know can't be shown to contradict an external application's catalog ID.
        var toolbox = MigrationFixtures.Identities.Resolve("Crestron.Toolbox", "Contoso.ConfigTool", "Crestron Toolbox");
        Assert.IsTrue(toolbox.IsConfident);
        Assert.AreEqual("Crestron.Toolbox", toolbox.Application!.Id);
    }

    [TestMethod]
    [DataRow("7zip.7zip", "Git.Git")]
    [DataRow("Crestron.Toolbox", "7zip.7zip")]
    [DataRow("7zip.7zip", "Contoso.ConfigTool")]
    public void ConflictingIdentitiesResolveToNoApplication(string catalogId, string winGetId)
    {
        var identity = MigrationFixtures.Identities.Resolve(catalogId, winGetId, "Any name");

        Assert.IsFalse(identity.IsConfident);
        Assert.IsNull(identity.Application);
        Assert.AreEqual(IdentityConfidence.Ambiguous, identity.Confidence);
        Assert.AreEqual(IdentityEvidence.ConflictingIds, identity.Evidence);
        StringAssert.Contains(identity.Describe(), "different applications");
    }

    [TestMethod]
    public async Task AnImportedGitIdNeverCompletesOrInstallsSevenZip()
    {
        // Git is on this PC; 7-Zip is not.
        var target = await WorkstationMigrationTests.Target(
            [new RegistryUninstallRecord(RegistryInventorySource.Hklm64, "Git", "2.47.0", "The Git Development Community", "Git_is1")],
            MigrationFixtures.WinGetResult(("Git.Git", "2.47.0")));
        var actions = new WorkstationMigrationTests.RecordingActionStore();
        var service = WorkstationMigrationTests.Service(target.Plan, actions);
        service.StartFromInventory(ImportWithSevenZipRecordedAs("Git.Git"));
        Assert.IsTrue(service.Accept(target.Plan));

        var item = service.Checklist!.Items.Single(entry => entry.Desired.Spec.CatalogId == "7zip.7zip");
        Assert.AreEqual(ChecklistStatus.NeedsReview, item.Status);
        Assert.IsFalse(item.Satisfied, "The imported Git ID completed 7-Zip.");
        Assert.IsTrue(item.Desired.Included, "The item stays in Remaining until someone decides.");
        StringAssert.Contains(item.Detail, "Git.Git");
        Assert.IsNull(item.CatalogState);
        Assert.IsFalse(item.CanInstallAutomatically);

        // Neither identity reaches the worker: the request is refused before one is written.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync([item.ItemId], riskAcknowledged: true));
        Assert.IsNull(actions.Request);
    }

    [TestMethod]
    public async Task AConflictStaysUnresolvedEvenWhenBothApplicationsAreInstalled()
    {
        var target = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult(("Git.Git", "2.47.0"), ("7zip.7zip", "26.03")));
        var service = WorkstationMigrationTests.Service(target.Plan, new WorkstationMigrationTests.RecordingActionStore());
        service.StartFromInventory(ImportWithSevenZipRecordedAs("Git.Git"));
        Assert.IsTrue(service.Accept(target.Plan));

        var item = service.Checklist!.Items.Single(entry => entry.Desired.Spec.CatalogId == "7zip.7zip");
        Assert.AreEqual(ChecklistStatus.NeedsReview, item.Status);
        Assert.IsFalse(item.Satisfied);
    }

    [TestMethod]
    public async Task AnImportedWinGetIdTheCatalogCantVouchForNeverStandsInForCatalogDetection()
    {
        // An uncatalogued package is installed; Crestron Toolbox, which the catalog detects in the registry, is not.
        var target = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult(("Contoso.ConfigTool", "1.0")));
        var service = WorkstationMigrationTests.Service(target.Plan, new WorkstationMigrationTests.RecordingActionStore());
        service.StartFromProfile(new DeploymentProfile("fixture-lab", "Fixture lab", 1, "Fixture", MigrationFixtures.Now,
            [new ProfileApplication("Crestron.Toolbox", "Crestron Toolbox", "Crestron Electronics Inc.", "Contoso.ConfigTool")], []));
        Assert.IsTrue(service.Accept(target.Plan));

        var item = service.Checklist!.Items.Single();
        Assert.IsTrue(item.Desired.Identity.IsConfident);
        Assert.AreEqual(ChecklistStatus.ManualInstall, item.Status);
        Assert.IsFalse(item.Satisfied);
    }

    [TestMethod]
    public void AnInventoryWithConflictingIdentitiesStillImports()
    {
        // The file is well formed; only what it claims is contradictory, which reconciliation reports, not the parser.
        var document = ImportWithSevenZipRecordedAs("Git.Git");
        var sevenZip = document.Applications.Single(item => item.CatalogId == "7zip.7zip");
        Assert.AreEqual("Git.Git", sevenZip.WinGet?.Id);
    }

    private static InventoryDocument ImportWithSevenZipRecordedAs(string winGetId)
    {
        var golden = File.ReadAllText(Path.Combine(MigrationFixtures.RepositoryRoot(), "tests", "fixtures", "inventory", "golden-av-workstation-inventory.json"));
        var altered = golden.Replace("\"id\": \"7zip.7zip\"", $"\"id\": \"{winGetId}\"", StringComparison.Ordinal);
        Assert.AreNotEqual(golden, altered);
        return WorkstationInventoryDocumentCodec.Parse(Encoding.UTF8.GetBytes(altered));
    }
}
