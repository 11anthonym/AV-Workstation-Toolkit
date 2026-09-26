using System.Text.Json;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Application.Workstation;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Domain.Workstation;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class WorkstationInventoryTests
{
    [TestMethod]
    public void ManagedProgramNamesResolveToExactlyTheirManagedRecord()
    {
        var identities = MigrationFixtures.Identities;
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(MigrationFixtures.RepositoryRoot(), "tests", "fixtures", "inventory", "managed-program-names.json")));
        var failures = new List<string>();
        foreach (var entry in fixture.RootElement.GetProperty("Entries").EnumerateArray())
        {
            var name = entry.GetProperty("DisplayName").GetString()!;
            var expected = entry.GetProperty("Expected").GetString();
            var resolution = identities.ResolveDisplayName(name);
            var detected = resolution.Confidence is IdentityConfidence.Catalog ? resolution.Application!.Id : null;
            if (resolution.Confidence == IdentityConfidence.Ambiguous || !string.Equals(detected, expected, StringComparison.Ordinal))
                failures.Add($"'{name}' → {resolution.Confidence} [{string.Join(", ", resolution.Candidates.Select(item => item.Id))}], expected [{expected}]");
        }
        Assert.HasCount(0, failures, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public void EveryManagedDetectorNamesALoadedManagedRecord()
    {
        foreach (var id in ManagedApplicationDetectors.Patterns.Keys)
            Assert.AreEqual(ApplicationManagement.ManagedWinGet, MigrationFixtures.Identities.Find(id)?.Management, id);
    }

    [TestMethod]
    public void IdentityCatalogDetectsExactlyWhatThePlanMatcherDetects()
    {
        // The catalog plan and the workstation inventory share one detection rule, so an observed AV application
        // resolves to the same record the plan reports as installed.
        var root = MigrationFixtures.RepositoryRoot();
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "inventory", "installed-program-names.json")));
        var external = MigrationFixtures.Catalog.Items.Where(item => item.Provider == ProviderKind.External && item.DetectionMode == DetectionMode.Registry).ToArray();
        var matcher = new ExternalInventoryMatcher();
        foreach (var entry in fixture.RootElement.GetProperty("Entries").EnumerateArray())
        {
            var name = entry.GetProperty("DisplayName").GetString()!;
            var registry = new RegistryInventoryResult(ProviderQuality.Complete, ProviderFailureKind.None,
                [new RegistryUninstallRecord(RegistryInventorySource.Hklm64, name, "1.0")],
                [new(RegistryInventorySource.Hklm64, true, 1, "ok")], "ok");
            var planDetected = external.Where(package => matcher.Match(package, registry).Installed).Select(package => package.Id).ToArray();
            // Managed records add their own detectors (for example "Microsoft Visual Studio Code (User)"); the plan's
            // matcher covers external records only, so compare the external part of the identity detection.
            var resolution = MigrationFixtures.Identities.ResolveDisplayName(name);
            var identityDetected = resolution.Evidence == IdentityEvidence.CatalogDetector
                ? resolution.Candidates.Where(item => item.Package.Provider == ProviderKind.External).Select(item => item.Id).ToArray()
                : [];
            CollectionAssert.AreEquivalent(planDetected, identityDetected, name);
        }
    }

    [TestMethod]
    public void IdentityResolutionPrefersCatalogIdThenExactManagedWinGetIdThenDetector()
    {
        var identities = MigrationFixtures.Identities;
        var byCatalog = identities.Resolve("Crestron.Toolbox", string.Empty, "Something else entirely");
        Assert.AreEqual(IdentityConfidence.Exact, byCatalog.Confidence);
        Assert.AreEqual("Crestron.Toolbox", byCatalog.Application!.Id);

        var byWinGet = identities.Resolve(string.Empty, "WiresharkFoundation.Wireshark", string.Empty);
        Assert.AreEqual(IdentityConfidence.Exact, byWinGet.Confidence);
        Assert.AreEqual(IdentityEvidence.WinGetId, byWinGet.Evidence);

        var byAlias = identities.Resolve(string.Empty, string.Empty, "Extron Electronics - Toolbelt");
        Assert.AreEqual(IdentityConfidence.Catalog, byAlias.Confidence);
        Assert.AreEqual("Extron.Toolbelt", byAlias.Application!.Id);
        Assert.IsTrue(byAlias.IsConfident);
    }

    [TestMethod]
    public void UnmanagedWinGetIdAndUnknownNamesAreNotCatalogIdentities()
    {
        var identities = MigrationFixtures.Identities;
        var arbitrary = identities.Resolve(string.Empty, "Some.Arbitrary.Package", "Some Arbitrary Package");
        Assert.AreEqual(IdentityConfidence.Unidentified, arbitrary.Confidence);
        Assert.IsNull(arbitrary.Application);
        var unknown = identities.ResolveDisplayName("Vendor Widget Configuration Tool");
        Assert.AreEqual(IdentityConfidence.Unidentified, unknown.Confidence);
        Assert.IsFalse(unknown.IsConfident);
        // A catalog ID the local catalog doesn't know confers nothing either.
        Assert.IsFalse(identities.Resolve("Some.Arbitrary.Package", string.Empty, "Some Arbitrary Package").IsConfident);
    }

    [TestMethod]
    public void AmbiguousAndSimilarNameMatchesNeverYieldAnApplication()
    {
        const string json = """
        {"SchemaVersion":3,"Packages":[
        {"Profile":"Field","Name":"Fixture Tool","Id":"Fixture.ToolA","Risk":"None","Note":"a","Deployment":"ManualHold","Maintenance":"Hold","Detection":{"RegistryDisplayNamePattern":"^Fixture Tool","VersionPolicy":"AtLeast"},"Release":{"Mode":"InventoryOnly","Channel":"Fixture"},"Delivery":{"Mode":"VendorPage","Uri":"https://example.com/a"},"Metadata":{"Vendor":"Fixture","ApplicationType":["FieldUtility"],"DeploymentClass":"ManualHandoff","SupportedOS":["Windows"],"OfficialProductUri":"https://example.com/a","ValidationMethod":["Registry"]}},
        {"Profile":"Field","Name":"Fixture Tool B","Id":"Fixture.ToolB","Risk":"None","Note":"b","Deployment":"ManualHold","Maintenance":"Hold","Detection":{"RegistryDisplayNamePattern":"^Fixture","VersionPolicy":"AtLeast"},"Release":{"Mode":"InventoryOnly","Channel":"Fixture"},"Delivery":{"Mode":"VendorPage","Uri":"https://example.com/b"},"Metadata":{"Vendor":"Fixture","ApplicationType":["FieldUtility"],"DeploymentClass":"ManualHandoff","SupportedOS":["Windows"],"OfficialProductUri":"https://example.com/b","ValidationMethod":["Registry"]}},
        {"Profile":"Field","Name":"Widget Studio","Id":"Fixture.WidgetStudio","Risk":"None","Note":"c","Deployment":"ManualHold","Maintenance":"Hold","Detection":{"Mode":"None"},"Release":{"Mode":"InventoryOnly","Channel":"Fixture"},"Delivery":{"Mode":"Awareness"},"Metadata":{"Vendor":"Fixture","ApplicationType":["FieldUtility"],"DeploymentClass":"AwarenessOnly","SupportedOS":["Windows"],"OfficialProductUri":"https://example.com/c","ValidationMethod":["Unknown"]}}]}
        """;
        var identities = new ApplicationIdentityCatalog(new CatalogParser(new DateOnly(2026, 9, 26)).ParseExternalCatalog(json));

        var ambiguous = identities.ResolveDisplayName("Fixture Tool 2.0");
        Assert.AreEqual(IdentityConfidence.Ambiguous, ambiguous.Confidence);
        Assert.IsNull(ambiguous.Application);
        Assert.HasCount(2, ambiguous.Candidates);
        Assert.IsFalse(ambiguous.IsConfident);

        var similar = identities.ResolveDisplayName("Widget Studio 2.1 (x64)");
        Assert.AreEqual(IdentityConfidence.Probable, similar.Confidence);
        Assert.IsNull(similar.Application);
        Assert.AreEqual("Fixture.WidgetStudio", similar.Candidates.Single().Id);
        Assert.IsFalse(similar.IsConfident);
    }

    [TestMethod]
    public void NameNormalizationRemovesVersionsArchitectureAndLegalSuffixes()
    {
        Assert.AreEqual("wireshark", ApplicationNames.NameKey("Wireshark 4.4.0 x64"));
        Assert.AreEqual("7-zip", ApplicationNames.NameKey("7-Zip 26.03 (x64 edition)"));
        Assert.AreEqual("notepad++", ApplicationNames.NameKey("Notepad++ (32-bit x86)"));
        Assert.AreEqual("crestron device database", ApplicationNames.NameKey("Crestron Device Database200.460.001.00"));
        Assert.AreEqual("microsoft visual c++ 2015-2022 redistributable", ApplicationNames.NameKey("Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.44.35211"));
        Assert.AreEqual("soulseekqt", ApplicationNames.NameKey("SoulseekQt version 2026.4.30 (64-bit)"));
        Assert.AreEqual("crestron", ApplicationNames.PublisherKey("Crestron Electronics, Inc."));
        Assert.AreEqual("notepad++", ApplicationNames.PublisherKey("Notepad++ team (MSI installer)"));
        Assert.IsTrue(ApplicationNames.PublishersAgree("Crestron Electronics Inc.", "Crestron"));
        Assert.AreEqual("x86", ApplicationNames.Architecture("Crestron Toolbox 3.1", UninstallHive.Machine32));
        Assert.AreEqual("arm64", ApplicationNames.Architecture("Python 3.11.9 (ARM64)", UninstallHive.Machine64));
    }

    [TestMethod]
    public void MigrationInventoryIsTheUnionOfRegistryAndWinGetEvidenceNotWinGetAlone()
    {
        var inventory = MigrationFixtures.Inventory(
        [
            MigrationFixtures.Registration("Crestron Toolbox 3.1390.0008.3", "3.1390.0008.3", "Crestron Electronics Inc.", UninstallHive.Machine32, "{1B52BC01-2F6E-4FAE-BB09-1F28D2BF1D63}_is1"),
            MigrationFixtures.Registration("Extron Electronics - Toolbelt", "2.35.0.14", "Extron", key: "{6910E638-B48D-4090-B491-9742B5CDEAD9}", windowsInstaller: true),
            MigrationFixtures.Registration("Symetrix Composer 11.2", "11.2", "Symetrix", key: "SymetrixComposer"),
            MigrationFixtures.Registration("Wireshark 4.4.0 x64", "4.4.0", "The Wireshark developer community, https://www.wireshark.org", key: "Wireshark"),
            MigrationFixtures.Registration("Vendor Widget Configuration Tool", "4.2", "Vendor Corp", key: "VendorWidgetTool"),
            MigrationFixtures.Registration("Proton VPN", "5.1.8", "Proton AG", key: "Proton VPN_is1"),
            MigrationFixtures.Registration("Microsoft .NET Runtime - 10.0.11 (x64)", "80.44.56884", "Microsoft Corporation", key: "{0B4F3EF1-06F1-46E4-B662-A33DECC02141}", systemComponent: true, windowsInstaller: true),
            MigrationFixtures.Registration("Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.44.35211", "14.44.35211.0", "Microsoft Corporation", UninstallHive.Machine32, "{0b5169e3-39da-4313-808e-1f9c0407f3bf}"),
            MigrationFixtures.Registration("Windows Driver Package - Crestron Electronics Inc. (WinUSB) Crestron  (01/23/2018 3.0.0.0)", "01/23/2018 3.0.0.0", "Crestron Electronics Inc.", key: "8B38028CEF858FCC39F58756DACD894026EFE962"),
            MigrationFixtures.Registration("Security Update for Vendor Suite (KB123)", "1", "Vendor Corp", key: "KB123", parentKey: "VendorSuite")
        ],
        [
            new("WiresharkFoundation.Wireshark", "4.4.0"),
            new("Proton.ProtonVPN", "5.1.8"),
            new("Microsoft.VCRedist.2015+.x86", "14.44.35211.0"),
            new("Microsoft.WindowsTerminal", "1.24.11911.0"),
            new("Anthropic.Claude", "2.9939.2.0")
        ]);

        var toolbox = inventory.Applications.Single(item => item.CatalogId == "Crestron.Toolbox");
        Assert.AreEqual(string.Empty, toolbox.WinGetId);
        Assert.AreEqual(MigrationRelevance.Application, toolbox.Relevance);
        Assert.AreEqual(IdentityKind.Catalog, toolbox.IdentityKind);
        Assert.AreEqual("Crestron Toolbox 3.1390.0008.3", toolbox.Registrations.Single().DisplayName, "Catalog identity must not replace the raw evidence.");
        Assert.AreEqual(InstallScope.Machine, toolbox.Scope);
        Assert.AreEqual("x86", toolbox.Architecture);

        Assert.AreEqual(MigrationRelevance.Application, inventory.Applications.Single(item => item.CatalogId == "Extron.Toolbelt").Relevance);
        Assert.AreEqual(MigrationRelevance.Application, inventory.Applications.Single(item => item.CatalogId == "Symetrix.Composer").Relevance);

        var unknown = inventory.Applications.Single(item => item.DisplayName == "Vendor Widget Configuration Tool");
        Assert.AreEqual(MigrationRelevance.Application, unknown.Relevance);
        Assert.AreEqual(string.Empty, unknown.CatalogId);
        Assert.AreEqual(string.Empty, unknown.WinGetId);
        Assert.AreEqual(IdentityKind.Installer, unknown.IdentityKind);

        var wireshark = inventory.Applications.Single(item => item.CatalogId == "WiresharkFoundation.Wireshark");
        Assert.AreEqual("WiresharkFoundation.Wireshark", wireshark.WinGetId);
        Assert.AreEqual(WinGetCorrelation.CatalogIdentity, wireshark.WinGetCorrelation);
        Assert.AreEqual(IdentityConfidence.Exact, wireshark.CatalogIdentity.Confidence);
        Assert.HasCount(1, wireshark.Registrations);

        var proton = inventory.Applications.Where(item => item.DisplayName.Contains("Proton", StringComparison.Ordinal) || item.WinGetId == "Proton.ProtonVPN").ToArray();
        Assert.HasCount(1, proton, "A registry application correlated with a WinGet identity must be one application, not two.");
        Assert.AreEqual(WinGetCorrelation.RegisteredName, proton[0].WinGetCorrelation);

        var runtime = inventory.Applications.Single(item => item.DisplayName.StartsWith("Microsoft .NET Runtime", StringComparison.Ordinal));
        Assert.AreEqual(MigrationRelevance.SystemComponent, runtime.Relevance, "Hidden components stay in the inventory as evidence.");
        var redistributable = inventory.Applications.Where(item => item.DisplayName.StartsWith("Microsoft Visual C++", StringComparison.Ordinal) || item.WinGetId.StartsWith("Microsoft.VCRedist", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(1, redistributable);
        Assert.AreEqual(MigrationRelevance.SupportComponent, redistributable[0].Relevance);
        Assert.AreEqual(WinGetCorrelation.RegisteredVersion, redistributable[0].WinGetCorrelation);
        Assert.AreEqual(MigrationRelevance.SupportComponent, inventory.Applications.Single(item => item.DisplayName.StartsWith("Windows Driver Package", StringComparison.Ordinal)).Relevance);
        Assert.AreEqual(MigrationRelevance.Update, inventory.Applications.Single(item => item.DisplayName.StartsWith("Security Update", StringComparison.Ordinal)).Relevance);

        var terminal = inventory.Applications.Single(item => item.WinGetId == "Microsoft.WindowsTerminal");
        Assert.AreEqual("Microsoft.WindowsTerminal", terminal.CatalogId);
        Assert.AreEqual(WinGetCorrelation.ExportOnly, terminal.WinGetCorrelation);
        Assert.AreEqual("Windows Terminal", terminal.DisplayName);
        var claude = inventory.Applications.Single(item => item.WinGetId == "Anthropic.Claude");
        Assert.AreEqual(IdentityKind.WinGet, claude.IdentityKind);
        Assert.AreEqual(MigrationRelevance.Application, claude.Relevance);
    }

    [TestMethod]
    public void RegistryApplicationsSurviveWhenWinGetIsUnavailable()
    {
        var inventory = MigrationFixtures.Inventory(
            [MigrationFixtures.Registration("Crestron Toolbox 3.1390.0008.3", "3.1390.0008.3", "Crestron Electronics Inc.", UninstallHive.Machine32),
             MigrationFixtures.Registration("BiampCanvas", "3.2.0", "Biamp Systems"),
             MigrationFixtures.Registration("Vendor Widget Configuration Tool", "4.2", "Vendor Corp")],
            [], winGetQuality: EvidenceQuality.Unavailable);

        Assert.AreEqual(EvidenceQuality.Unavailable, inventory.Sources.WinGet);
        Assert.AreEqual(EvidenceQuality.Complete, inventory.Sources.Registry);
        CollectionAssert.IsSubsetOf(new[] { "Crestron.Toolbox", "Biamp.Canvas" }, inventory.Applications.Select(item => item.CatalogId).ToArray());
        Assert.AreEqual(3, inventory.ApplicationCount);
    }

    [TestMethod]
    public void DuplicateRegistrationsMergeWhileSideBySideVersionsStaySeparate()
    {
        var inventory = MigrationFixtures.Inventory(
        [
            MigrationFixtures.Registration("Notepad++ (32-bit x86)", "8.6.9", "Notepad++ Team", UninstallHive.Machine32, "Notepad++"),
            MigrationFixtures.Registration("Notepad++ (64-bit x64)", "8.9.8", "Notepad++ Team", UninstallHive.Machine64, "Notepad++"),
            MigrationFixtures.Registration("Vendor Tool", "2.0", "Vendor Corp", UninstallHive.Machine64, "VendorTool"),
            MigrationFixtures.Registration("Vendor Tool", "2.0", "Vendor Corp, Inc.", UninstallHive.User, "VendorTool"),
            MigrationFixtures.Registration("Vendor Designer 1.0", "1.0", "Vendor Corp", key: "VendorDesigner1"),
            MigrationFixtures.Registration("Vendor Designer 2.0", "2.0", "Vendor Corp", key: "VendorDesigner2"),
            MigrationFixtures.Registration("Partial Metadata App", string.Empty, string.Empty, UninstallHive.User, "PartialApp")
        ], []);

        var notepad = inventory.Applications.Single(item => item.CatalogId == "Notepad++.Notepad++");
        Assert.HasCount(2, notepad.Registrations);
        Assert.AreEqual("8.9.8", notepad.DisplayVersion);
        Assert.AreEqual("x64, x86", notepad.Architecture);
        var tool = inventory.Applications.Single(item => item.DisplayName == "Vendor Tool");
        Assert.HasCount(2, tool.Registrations);
        Assert.AreEqual(InstallScope.Machine, tool.Scope);
        Assert.HasCount(2, inventory.Applications.Where(item => item.DisplayName.StartsWith("Vendor Designer", StringComparison.Ordinal)).ToArray());
        var partial = inventory.Applications.Single(item => item.DisplayName == "Partial Metadata App");
        Assert.AreEqual(string.Empty, partial.DisplayVersion);
        Assert.AreEqual(string.Empty, partial.Publisher);
        Assert.AreEqual(InstallScope.User, partial.Scope);
        Assert.AreEqual(MigrationRelevance.Application, partial.Relevance);
    }

    [TestMethod]
    public void WindowsInstallerPackedGuidsConvertBothWays()
    {
        // 7-Zip 26.03's x64 MSI product code and its upgrade code as the UpgradeCodes key stores them.
        const string productCode = "{23170F69-40C1-2702-2603-000001000000}";
        Assert.AreEqual("96F071321C0420726230000010000000", AVWorkstationToolkit.Infrastructure.Windows.Registry.MsiPackedGuid.Pack(productCode));
        Assert.IsTrue(AVWorkstationToolkit.Infrastructure.Windows.Registry.MsiPackedGuid.TryUnpack("96F071321C0420720000000040000000", out var upgradeCode));
        Assert.AreEqual("{23170F69-40C1-2702-0000-000004000000}", upgradeCode);
        Assert.IsTrue(AVWorkstationToolkit.Infrastructure.Windows.Registry.MsiPackedGuid.TryUnpack(
            AVWorkstationToolkit.Infrastructure.Windows.Registry.MsiPackedGuid.Pack(productCode), out var roundTrip));
        Assert.AreEqual(productCode, roundTrip);
        Assert.IsFalse(AVWorkstationToolkit.Infrastructure.Windows.Registry.MsiPackedGuid.TryUnpack("not-a-packed-guid", out _));
    }

    [TestMethod]
    public async Task InventoryServiceReadsTheSameProvidersAsTheCatalogPlan()
    {
        var registry = MigrationFixtures.RegistryResult([new RegistryUninstallRecord(RegistryInventorySource.Hkcu, "Crestron Toolbox 3.1390.0008.3", "3.1390.0008.3", "Crestron Electronics Inc.", "{1B52}_is1")]);
        var winGet = new InstalledPackageInventoryResult(ProviderQuality.Complete, ProviderFailureKind.None, [new("7zip.7zip", "24.08")], "ok", string.Empty);
        var service = new WorkstationInventoryService(MigrationFixtures.Catalog, new FixedInstalled(winGet), new FixedRegistry(registry), new FixedMachine());

        var scanned = await service.ScanAsync();
        var plan = await MigrationFixtures.PlanAsync(registry, winGet);
        var fromPlan = service.Build(plan.Evidence!);

        CollectionAssert.AreEqual(scanned.Applications.Select(item => item.ObservationKey).ToArray(), fromPlan.Applications.Select(item => item.ObservationKey).ToArray());
        Assert.AreEqual(InstallScope.User, scanned.Applications.Single(item => item.CatalogId == "Crestron.Toolbox").Scope);
        Assert.AreEqual("FIXTURE-PC", scanned.Machine.ComputerName);
        Assert.IsTrue(plan.Packages.Single(item => item.Package.Id == "Crestron.Toolbox").Installed, "The plan and the inventory see the same registry evidence.");
    }

    internal sealed class FixedInstalled(InstalledPackageInventoryResult result) : IInstalledPackageInventory
    {
        public Task<InstalledPackageInventoryResult> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    internal sealed class FixedRegistry(RegistryInventoryResult result) : IExternalApplicationInventory
    {
        public Task<RegistryInventoryResult> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    internal sealed class FixedMachine : IWorkstationMachineInfoProvider
    {
        public WorkstationMachine Read() => new("FIXTURE-PC", "Windows 11 Pro", "24H2", "26100.1", "x64");
    }
}

internal static class MigrationFixtures
{
    private static readonly Lazy<PackageCatalog> LazyCatalog = new(() => new RepositoryCatalogLoader().Load(RepositoryRoot()));
    private static readonly Lazy<ApplicationIdentityCatalog> LazyIdentities = new(() => new ApplicationIdentityCatalog(LazyCatalog.Value));

    public static PackageCatalog Catalog => LazyCatalog.Value;
    public static ApplicationIdentityCatalog Identities => LazyIdentities.Value;
    public static DateTimeOffset Now { get; } = new(2026, 9, 26, 5, 0, 0, TimeSpan.Zero);

    public static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "VERSION"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    public static UninstallRegistration Registration(
        string name,
        string version = "",
        string publisher = "",
        UninstallHive hive = UninstallHive.Machine64,
        string key = "",
        bool systemComponent = false,
        bool windowsInstaller = false,
        string parentKey = "",
        string upgradeCode = "") =>
        new(hive, key.Length > 0 ? key : name, name, version, publisher, systemComponent, windowsInstaller, parentKey, string.Empty, upgradeCode);

    public static WorkstationInventory Inventory(
        IEnumerable<UninstallRegistration> registrations,
        IEnumerable<WinGetPackageEvidence> winGet,
        EvidenceQuality registryQuality = EvidenceQuality.Complete,
        EvidenceQuality winGetQuality = EvidenceQuality.Complete) =>
        new WorkstationInventoryBuilder(Identities).Build(registrations, registryQuality, winGet, winGetQuality,
            new WorkstationMachine("SOURCE-PC", "Windows 11 Pro", "24H2", "26100.1", "x64"), Now);

    public static RegistryInventoryResult RegistryResult(IReadOnlyList<RegistryUninstallRecord> records, ProviderQuality quality = ProviderQuality.Complete) =>
        new(quality, quality == ProviderQuality.Complete ? ProviderFailureKind.None : ProviderFailureKind.PartialInventory, records,
            [new(RegistryInventorySource.Hklm64, true, records.Count, "ok"), new(RegistryInventorySource.Hklm32, true, 0, "ok"),
             new(RegistryInventorySource.Hkcu, quality == ProviderQuality.Complete, 0, quality == ProviderQuality.Complete ? "ok" : "failed")], "fixture");

    public static InstalledPackageInventoryResult WinGetResult(params (string Id, string Version)[] packages) =>
        new(ProviderQuality.Complete, ProviderFailureKind.None, packages.Select(item => new InstalledPackageRecord(item.Id, item.Version)).ToArray(), "ok", string.Empty);

    public static Task<WorkstationPlan> PlanAsync(RegistryInventoryResult registry, InstalledPackageInventoryResult winGet, bool rebootPending = false) =>
        new WorkstationPlanningCoordinator(
            Catalog,
            new WorkstationInventoryTests.FixedInstalled(winGet),
            new FixedUpdates(),
            new WorkstationInventoryTests.FixedRegistry(registry),
            new FixedReboot(rebootPending)).RefreshAsync();

    public static WorkstationInventoryService InventoryService() =>
        new(Catalog, new WorkstationInventoryTests.FixedInstalled(WinGetResult()), new WorkstationInventoryTests.FixedRegistry(RegistryResult([])),
            new WorkstationInventoryTests.FixedMachine());

    private sealed class FixedUpdates : IAvailableUpdateInventory
    {
        public Task<AvailableUpdateInventoryResult> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AvailableUpdateInventoryResult(ProviderQuality.Complete, ProviderFailureKind.None, [], "ok", string.Empty));
    }

    private sealed class FixedReboot(bool pending) : IRebootStateProvider
    {
        public Task<RebootDetectionResult> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RebootDetectionResult(pending, pending ? [RebootReason.WindowsUpdate] : [], ProviderQuality.Complete, ProviderFailureKind.None,
                pending ? "Windows Update requires a restart." : "clear"));
    }
}
