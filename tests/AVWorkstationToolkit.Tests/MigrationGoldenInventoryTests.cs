using System.Text;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Workstation;
using AVWorkstationToolkit.Domain.Workstation;

namespace AVWorkstationToolkit.Tests;

/// <summary>
/// One reviewed export of a representative AV engineering workstation, kept as tests\fixtures\inventory\golden-av-workstation-inventory.json.
/// It pins the inventory schema and its classification end to end: raw evidence, export, parse, import, and reconciliation.
/// A change to any of them shows up here and needs a deliberate, reviewed update of the fixture.
/// </summary>
[TestClass]
public sealed class MigrationGoldenInventoryTests
{
    private const string Generator = "AV Workstation Toolkit golden fixture";
    private const string UncataloguedWinGetId = "FixtureAV.ConfigurationUtility";

    private static string FixturePath => Path.Combine(MigrationFixtures.RepositoryRoot(), "tests", "fixtures", "inventory", "golden-av-workstation-inventory.json");

    [TestMethod]
    public void ExportOfTheReviewedWorkstationMatchesTheGoldenFixture()
    {
        var actual = Normalize(Encoding.UTF8.GetString(WorkstationInventoryDocumentCodec.Serialize(Observed(), Generator)));
        var expected = File.Exists(FixturePath) ? Normalize(File.ReadAllText(FixturePath)) : string.Empty;
        if (actual == expected) return;
        var review = Path.Combine(Path.GetTempPath(), $"golden-av-workstation-inventory-{Guid.NewGuid():N}.json");
        File.WriteAllText(review, actual);
        Assert.Fail($"The export no longer matches {FixturePath}. A schema or classification change needs a reviewed fixture update; this build's export is at {review}.");
    }

    [TestMethod]
    public void GoldenFixtureParsesWithItsReviewedClassification()
    {
        var document = WorkstationInventoryDocumentCodec.Parse(File.ReadAllBytes(FixturePath));
        Assert.AreEqual(Generator, document.Generator);
        Assert.AreEqual("AV-LAB-PC-01", document.Machine.ComputerName);
        Assert.IsTrue(document.Sources.Complete);

        AssertApplication(document, "Crestron Toolbox 3.1390.0008.3", "Crestron.Toolbox", MigrationRelevance.Application, selected: true);
        var toolbelt = AssertApplication(document, "Extron Electronics - Toolbelt", "Extron.Toolbelt", MigrationRelevance.Application, selected: true);
        Assert.AreEqual("{5C1C7E62-3B8A-4E0B-9E0F-2B7A8C3D4E51}", toolbelt.MsiUpgradeCode);
        AssertApplication(document, "Tesira", "Biamp.Tesira", MigrationRelevance.Application, selected: true);
        var sevenZip = AssertApplication(document, "7-Zip 26.03 (x64 edition)", "7zip.7zip", MigrationRelevance.Application, selected: true);
        Assert.AreEqual("7zip.7zip", sevenZip.WinGet?.Id);
        var unknown = AssertApplication(document, "Fixture AV Configuration Utility", string.Empty, MigrationRelevance.Application, selected: true);
        Assert.AreEqual("Fixture AV", unknown.Publisher);
        var runtime = AssertApplication(document, "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.44.35211", string.Empty, MigrationRelevance.SupportComponent, selected: false);
        Assert.AreEqual(ApplicationComponentRules.RuntimeReason, runtime.RelevanceReason);
        var hidden = AssertApplication(document, "Microsoft .NET Runtime - 10.0.11 (x64)", string.Empty, MigrationRelevance.SystemComponent, selected: false);
        Assert.AreEqual("Hidden by Windows as a system component", hidden.RelevanceReason);

        // The uncatalogued WinGet ID stays evidence on the application it names, which gains no catalog identity from it.
        Assert.AreEqual(UncataloguedWinGetId, unknown.WinGet?.Id);
        Assert.AreEqual(WinGetCorrelation.RegisteredName, unknown.WinGetCorrelation);
        Assert.AreEqual(1, document.Applications.Count(item => item.WinGet?.Id == UncataloguedWinGetId));
    }

    [TestMethod]
    public async Task GoldenFixtureImportsAndReconcilesWithoutGrantingAuthority()
    {
        var document = WorkstationInventoryDocumentCodec.Parse(File.ReadAllBytes(FixturePath));
        // The replacement PC already has Toolbelt; nothing else from the old PC is installed yet.
        var target = await WorkstationMigrationTests.Target(
            [new RegistryUninstallRecord(RegistryInventorySource.Hklm64, "Extron Electronics - Toolbelt", "2.35.0.14", "Extron", "{6910E638-B48D-4090-B491-9742B5CDEAD9}")],
            MigrationFixtures.WinGetResult());
        var service = new WorkstationMigrationService(new WorkstationMigrationTests.FixedPlanning(target.Plan), MigrationFixtures.InventoryService(),
            new WorkstationMigrationTests.MemorySessionStore(), timeProvider: new WorkstationMigrationTests.FixedTime(MigrationFixtures.Now));

        var session = service.StartFromInventory(document);
        Assert.AreEqual(1, session.SkippedComponentCount, "The hidden system component stays in the file only.");
        Assert.IsTrue(service.Accept(target.Plan));
        var items = service.Checklist!.Items;
        ReconciledApplication Item(string name) => items.Single(item => item.Desired.Spec.DisplayName == name);

        Assert.AreEqual(ChecklistStatus.Installed, Item("Extron Electronics - Toolbelt").Status);
        Assert.AreEqual(ChecklistStatus.ReadyToInstall, Item("7-Zip 26.03 (x64 edition)").Status);
        Assert.IsTrue(Item("7-Zip 26.03 (x64 edition)").CanInstallAutomatically, "Only the catalog plan's exact managed install is automatic.");
        Assert.AreEqual(ChecklistStatus.ManualInstall, Item("Crestron Toolbox 3.1390.0008.3").Status);
        Assert.AreEqual(ChecklistStatus.ManualInstall, Item("Tesira").Status);
        Assert.AreEqual(ChecklistStatus.UnknownApplication, Item("Fixture AV Configuration Utility").Status);
        Assert.AreEqual(ChecklistStatus.Excluded, Item("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.44.35211").Status);
        Assert.IsFalse(items.Any(item => item.Desired.Spec.DisplayName.StartsWith("Microsoft .NET Runtime", StringComparison.Ordinal)));

        // An imported WinGet ID for an uncatalogued application never becomes installable.
        var uncatalogued = items.Single(item => item.Desired.Spec.WinGetId == UncataloguedWinGetId);
        Assert.AreEqual("Fixture AV Configuration Utility", uncatalogued.Desired.Spec.DisplayName);
        Assert.IsFalse(uncatalogued.CanInstallAutomatically);
        CollectionAssert.AreEqual(new[] { "7zip.7zip" }, items.Where(item => item.CanInstallAutomatically).Select(item => item.CatalogState!.Package.Id).ToArray());

        var summary = service.Checklist.Summary;
        Assert.AreEqual(1, summary.Satisfied);
        Assert.AreEqual(1, summary.Supporting);
        Assert.AreEqual(0, summary.Excluded, "A supporting component left out is not a user exclusion.");
        Assert.AreEqual(summary.Included - 1, summary.Remaining);
    }

    // The raw evidence a real AV engineering workstation reports: every registry registration, and WinGet only where it knows the app.
    private static WorkstationInventory Observed() => new WorkstationInventoryBuilder(MigrationFixtures.Identities).Build(
        [
            MigrationFixtures.Registration("Crestron Toolbox 3.1390.0008.3", "3.1390.0008.3", "Crestron Electronics Inc.", UninstallHive.Machine32, "{1B52BC01-2F6E-4FAE-BB09-1F28D2BF1D63}_is1"),
            MigrationFixtures.Registration("Extron Electronics - Toolbelt", "2.35.0.14", "Extron", key: "{6910E638-B48D-4090-B491-9742B5CDEAD9}", windowsInstaller: true, upgradeCode: "{5C1C7E62-3B8A-4E0B-9E0F-2B7A8C3D4E51}"),
            MigrationFixtures.Registration("Tesira", "4.11.0.24162", "Biamp Systems", UninstallHive.Machine32, "{7D0B2E54-1C3A-4F6B-8E9D-0A1B2C3D4E5F}", windowsInstaller: true),
            MigrationFixtures.Registration("7-Zip 26.03 (x64 edition)", "26.03.00.0", "Igor Pavlov", key: "{23170F69-40C1-2702-2603-000001000000}", windowsInstaller: true),
            MigrationFixtures.Registration("Fixture AV Configuration Utility", "4.2", "Fixture AV", key: "FixtureAVConfigurationUtility"),
            MigrationFixtures.Registration("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.44.35211", "14.44.35211.0", "Microsoft Corporation", key: "{d8bbe9f9-7c5b-42c6-b715-9ee898a2e515}"),
            MigrationFixtures.Registration("Microsoft .NET Runtime - 10.0.11 (x64)", "80.44.56884", "Microsoft Corporation", key: "{0B4F3EF1-06F1-46E4-B662-A33DECC02141}", systemComponent: true, windowsInstaller: true)
        ],
        EvidenceQuality.Complete,
        [new("7zip.7zip", "26.03.00.0"), new("Microsoft.VCRedist.2015+.x64", "14.44.35211.0"), new(UncataloguedWinGetId, "4.2")],
        EvidenceQuality.Complete,
        new WorkstationMachine("AV-LAB-PC-01", "Windows 11 Pro", "24H2", "26100.4652", "x64"),
        MigrationFixtures.Now);

    private static InventoryDocumentApplication AssertApplication(InventoryDocument document, string name, string catalogId, MigrationRelevance relevance, bool selected)
    {
        var application = document.Applications.Single(item => item.DisplayName == name);
        Assert.AreEqual(catalogId, application.CatalogId, name);
        Assert.AreEqual(relevance, application.Relevance, name);
        Assert.AreEqual(selected, application.SelectedForMigration, name);
        // Only a classification other than the default "application" records why.
        if (relevance == MigrationRelevance.Application) Assert.AreEqual(string.Empty, application.RelevanceReason, name);
        else Assert.IsGreaterThan(0, application.RelevanceReason.Length, $"{name} has no classification reason.");
        return application;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
}
