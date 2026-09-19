using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Processes;
using AVWorkstationToolkit.Infrastructure.Windows.WinGet;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class ProviderInfrastructureTests
{
    [TestMethod]
    public void ManagedCatalogJsonIsStrictAndTyped()
    {
        const string valid = """{"SchemaVersion":1,"ForbiddenPattern":"(?i)Forbidden","Packages":[{"Profile":"Standard","Name":"Fixture","Id":"Fixture.Tool","Vendor":"Fixture","Risk":"None","Note":"Fixture"}]}""";
        var catalog = RepositoryCatalogLoader.ParseManagedCatalog(valid);
        Assert.HasCount(1, catalog.Packages);
        Assert.AreEqual("Fixture.Tool", catalog.Packages[0].Id);
        Assert.Throws<InvalidDataException>(() => RepositoryCatalogLoader.ParseManagedCatalog(valid.Replace("\"Packages\"", "\"Unexpected\":true,\"Packages\"", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => RepositoryCatalogLoader.ParseManagedCatalog(valid.Replace("\"Profile\":\"Standard\"", "\"Profile\":\"Standard\",\"Profile\":\"Field\"", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => RepositoryCatalogLoader.ParseManagedCatalog(valid.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2", StringComparison.Ordinal)));
        // Duplicate detection compares decoded property names, so a JSON escape cannot disguise a repeat.
        Assert.Throws<InvalidDataException>(() => RepositoryCatalogLoader.ParseManagedCatalog(
            valid.Replace("\"Id\":\"Fixture.Tool\"", "\"Id\":\"Fixture.Tool\",\"\\u0049d\":\"Fixture.Other\"", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => RepositoryCatalogLoader.ParseManagedCatalog(
            valid.Replace("\"ForbiddenPattern\"", "\"\\u0046orbiddenPattern\":\"(?i)Forbidden\",\"ForbiddenPattern\"", StringComparison.Ordinal)));
        // Ordinal comparison: a case variant is not a duplicate, but it is an unmapped member.
        Assert.Throws<InvalidDataException>(() => RepositoryCatalogLoader.ParseManagedCatalog(
            valid.Replace("\"Id\":\"Fixture.Tool\"", "\"Id\":\"Fixture.Tool\",\"id\":\"Fixture.Other\"", StringComparison.Ordinal)));
        // An escape inside an ordinary string value is not a property name.
        Assert.AreEqual("Fixture\tTool", RepositoryCatalogLoader.ParseManagedCatalog(
            valid.Replace("\"Note\":\"Fixture\"", "\"Note\":\"Fixture\\tTool\"", StringComparison.Ordinal)).Packages[0].Note);
    }

    [TestMethod]
    public void InstalledParserCombinesDuplicateIdentitiesWithoutLosingVersions()
    {
        const string json = "{\"Sources\":[{\"Packages\":[{\"PackageIdentifier\":\"Vendor.Tool\",\"Version\":\"2.0\"},{\"PackageIdentifier\":\"Vendor.Tool\",\"Version\":\"1.0\"}]}]}";
        var packages = WinGetInventoryParsers.ParseInstalledExport(json);
        Assert.HasCount(1, packages);
        Assert.AreEqual("Vendor.Tool", packages[0].Id);
        Assert.AreEqual("1.0 / 2.0", packages[0].InstalledVersion);
    }

    [TestMethod]
    public void InstalledParserRejectsMissingIdentityAndTruncatedJson()
    {
        Assert.Throws<InvalidDataException>(() => WinGetInventoryParsers.ParseInstalledExport("{\"Sources\":[{\"Packages\":[{\"Version\":\"1.0\"}]}]}"));
        Assert.Throws<System.Text.Json.JsonException>(() => WinGetInventoryParsers.ParseInstalledExport("{\"Sources\":["));
    }

    [TestMethod]
    public void UpdateParserDistinguishesNoUpdatesFromMalformedOutput()
    {
        Assert.IsEmpty(WinGetInventoryParsers.ParseAvailableUpdates("No applicable upgrade found."));
        Assert.Throws<InvalidDataException>(() => WinGetInventoryParsers.ParseAvailableUpdates("arbitrary nonempty output"));
        Assert.Throws<InvalidDataException>(() => WinGetInventoryParsers.ParseAvailableUpdates("Name Id Version Available Source\n--------\nTool…"));
    }

    [TestMethod]
    public void UpdateParserReadsValidatedRows()
    {
        const string output = "Name                  Id           Version  Available Source\n---------------------------------------------------------------\nVendor Tool           Vendor.Tool  1.2.3    1.3.0     winget";
        var updates = WinGetInventoryParsers.ParseAvailableUpdates(output);
        Assert.HasCount(1, updates);
        Assert.AreEqual(new AvailableUpdateRecord("Vendor.Tool", "1.2.3", "1.3.0"), updates[0]);
    }

    [TestMethod]
    public void UpdateParserAcceptsCurrentWinGetTableWithoutSourceColumnAndKnownSummary()
    {
        const string output = "Name                  Id           Version  Available\n------------------------------------------------------\nVendor Tool           Vendor.Tool  1.2.3    1.3.0\n1 upgrade available.";
        var updates = WinGetInventoryParsers.ParseAvailableUpdates(output);
        Assert.HasCount(1, updates);
        Assert.AreEqual(new AvailableUpdateRecord("Vendor.Tool", "1.2.3", "1.3.0"), updates[0]);
    }

    [TestMethod]
    public void ReadOnlyInvocationPolicyExposesOnlyFixedInventoryVectors()
    {
        CollectionAssert.AreEqual(new[] { "--version" }, WinGetReadOnlyInvocationPolicy.GetArguments(WinGetReadOnlyOperation.Version).ToArray());
        CollectionAssert.AreEqual(new[] { "list", "--upgrade-available", "--source", "winget", "--disable-interactivity", "--accept-source-agreements" },
            WinGetReadOnlyInvocationPolicy.GetArguments(WinGetReadOnlyOperation.AvailableUpdates).ToArray());
        var exportPath = Path.Combine(Path.GetTempPath(), "AVWorkstationToolkit-winget-export-0123456789abcdef0123456789abcdef.json");
        var export = WinGetReadOnlyInvocationPolicy.GetArguments(WinGetReadOnlyOperation.InstalledInventory, exportPath);
        CollectionAssert.Contains(export.ToArray(), "export");
        CollectionAssert.Contains(export.ToArray(), "--include-versions");
        var allArguments = string.Join(' ', new[] { export, WinGetReadOnlyInvocationPolicy.GetArguments(WinGetReadOnlyOperation.AvailableUpdates) }.SelectMany(value => value));
        StringAssert.DoesNotMatch(allArguments, new System.Text.RegularExpressions.Regex(@"(?i)\b(?:install|uninstall|import)\b|--all"));
        Assert.Throws<ArgumentException>(() => WinGetReadOnlyInvocationPolicy.GetArguments(WinGetReadOnlyOperation.InstalledInventory, @"C:\Temp\arbitrary.json"));
        Assert.Throws<ArgumentOutOfRangeException>(() => WinGetReadOnlyInvocationPolicy.GetArguments((WinGetReadOnlyOperation)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WinGetReadOnlyProcessRunner(new StubResolver(), TimeSpan.FromMinutes(6)));
    }

    [TestMethod]
    public void WinGetTrustPolicyRejectsEveryUnsafeCandidateDimension()
    {
        var trusted = Candidate();
        Assert.IsTrue(WinGetCandidatePolicy.Evaluate(trusted).Trusted);
        Assert.IsFalse(WinGetCandidatePolicy.Evaluate(trusted with { PathContained = false }).Trusted);
        Assert.IsFalse(WinGetCandidatePolicy.Evaluate(trusted with { ReparseFree = false }).Trusted);
        Assert.IsFalse(WinGetCandidatePolicy.Evaluate(trusted with { SignatureValid = false }).Trusted);
        Assert.IsFalse(WinGetCandidatePolicy.Evaluate(trusted with { SignerSubject = "O=Example" }).Trusted);
        Assert.IsFalse(WinGetCandidatePolicy.Evaluate(trusted with { FileExists = false }).Trusted);
        Assert.IsFalse(WinGetCandidatePolicy.Evaluate(trusted with { ExecutablePath = @"C:\Temp\winget.exe", PathContained = false }).Trusted);
        Assert.IsFalse(WindowsWinGetResolver.IsExpectedExecutablePath(@"C:\Temp\winget.exe"));
    }

    [TestMethod]
    public void RegistryMatcherPreservesPartialAndPackageSpecificFailure()
    {
        var statuses = new[]
        {
            new RegistrySourceStatus(RegistryInventorySource.Hklm64, true, 1, "OK"),
            new RegistrySourceStatus(RegistryInventorySource.Hklm32, true, 0, "OK"),
            new RegistrySourceStatus(RegistryInventorySource.Hkcu, false, 0, "Denied")
        };
        var matcher = new ExternalInventoryMatcher();
        var partial = new RegistryInventoryResult(ProviderQuality.Partial, ProviderFailureKind.PartialInventory,
            [new(RegistryInventorySource.Hklm64, "Tool", "1.2")], statuses, "Partial");
        var detected = matcher.Match(Package("^Tool$", string.Empty), partial);
        Assert.IsTrue(detected.Reliable);
        Assert.AreEqual(InventoryQuality.Partial, detected.InventoryQuality);
        Assert.AreEqual("1.2", detected.InstalledVersion);
        var malformed = matcher.Match(Package("^Tool$", string.Empty), partial with
        {
            Records = [new(RegistryInventorySource.Hklm64, "Tool", "banana")]
        });
        Assert.IsFalse(malformed.Reliable);
        Assert.AreEqual(InventoryQuality.PackageError, malformed.InventoryQuality);

        var awareness = matcher.Match(Package(string.Empty, string.Empty, DetectionMode.None), partial);
        Assert.IsTrue(awareness.Reliable);
        Assert.IsFalse(awareness.Installed);
        Assert.AreEqual(InventoryQuality.NotApplicable, awareness.InventoryQuality);
    }

    [TestMethod]
    public async Task ProvidersDoNotCollapseExecutionFailureIntoEmptySuccess()
    {
        var runner = new StubRunner(new(5, string.Empty, "failure", string.Empty, false, false, false, ProviderFailureKind.ExecutionFailed));
        var installed = await new WinGetInstalledPackageInventory(runner, []).ReadAsync();
        var updates = await new WinGetAvailableUpdateInventory(runner).ReadAsync();
        Assert.AreEqual(ProviderQuality.Unavailable, installed.Quality);
        Assert.AreEqual(ProviderQuality.Unavailable, updates.Quality);
        Assert.AreEqual(ProviderFailureKind.ExecutionFailed, installed.Failure);
        Assert.AreEqual(ProviderFailureKind.ExecutionFailed, updates.Failure);
    }

    private static WinGetCandidateFacts Candidate() =>
        new("Microsoft.DesktopAppInstaller", "O=Microsoft Corporation", "1.29.0.0",
            @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.29.0.0_x64__8wekyb3d8bbwe",
            @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.29.0.0_x64__8wekyb3d8bbwe\winget.exe",
            true, true, true, true, true, "O=Microsoft Corporation");

    private static PackageDefinition Package(string displayPattern, string versionPattern, DetectionMode detectionMode = DetectionMode.Registry) =>
        new("Fixture.Tool", "Fixture Tool", "Fixture", string.Empty, "Fixture", ProviderKind.External,
            CatalogAuthority.OperationalExternal, PackageProfile.Optional, PackagePriority.P2, PackageRisk.None,
            DeploymentPolicy.ManualHold, MaintenancePolicy.Hold, DeploymentClass.InventoryOnly, CatalogMaintenancePolicy.Manual,
            VersionRule.InventoryOnly, VersionCouplingMode.Independent, string.Empty, Lifecycle.Current,
            [ApplicationType.FieldUtility], [], [], [LicensingModel.UnknownCost], ["UNKNOWN-ACCESS"], DistributionPolicy.LinkOnly,
            [], [SupportedOperatingSystem.Windows], DeliveryMode.InventoryOnly, ReleaseMode.InventoryOnly, detectionMode,
            detectionMode == DetectionMode.None ? DetectionVersionPolicy.None : DetectionVersionPolicy.AtLeast, "Inventory", string.Empty, string.Empty, string.Empty, [], null, null, null, null,
            null, null, null, null, null, string.Empty, [], displayPattern, versionPattern);

    private sealed class StubRunner(WinGetProcessResult result) : IWinGetReadOnlyProcessRunner
    {
        public Task<WinGetProcessResult> RunAsync(WinGetReadOnlyOperation operation, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class StubResolver : IWinGetResolver
    {
        public Task<TrustedWinGetResolution> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TrustedWinGetResolution(false, string.Empty, ProviderFailureKind.TrustFailure, "Fixture resolver is unavailable."));
    }
}
