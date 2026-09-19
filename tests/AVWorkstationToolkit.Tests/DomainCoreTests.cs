using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Domain.Policies;
using AVWorkstationToolkit.Domain.Versions;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class DomainCoreTests
{
    [TestMethod]
    [DataRow("1.2", "1.2.0.0")]
    [DataRow("1.2.3", "1.2.3.0")]
    [DataRow("1.2.3.4", "1.2.3.4")]
    [DataRow("01.002.0003", "1.2.3.0")]
    public void VersionParsingNormalizesSupportedNumericVersions(string input, string expected) =>
        Assert.AreEqual(expected, VersionValue.Parse(input).Normalized);

    [TestMethod]
    [DataRow("")]
    [DataRow("1")]
    [DataRow("1.2.3.4.5")]
    [DataRow("1.2-beta")]
    [DataRow("2147483648.1")]
    public void VersionParsingRejectsUnsupportedValues(string input) =>
        Assert.ThrowsExactly<FormatException>(() => VersionValue.Parse(input));

    [TestMethod]
    public void VersionComparisonUsesAllFourNumericParts()
    {
        Assert.IsGreaterThan(0, VersionValue.Parse("2.0").CompareTo(VersionValue.Parse("1.99.99.99")));
        Assert.AreEqual(0, VersionValue.Parse("1.2").CompareTo(VersionValue.Parse("1.2.0.0")));
        Assert.IsLessThan(0, VersionValue.Parse("1.2.3").CompareTo(VersionValue.Parse("1.2.4")));
    }

    [TestMethod]
    public void ExternalCatalogsParseStrictlyAndPreserveAuthorityBoundaries()
    {
        var parser = new CatalogParser(DateOnly.FromDateTime(DateTime.UtcNow));
        var root = RepositoryRoot();
        var operational = parser.ParseExternalCatalog(File.ReadAllText(Path.Combine(root, "manifests", "external-applications.json")));
        var awareness = parser.ParseExternalCatalog(File.ReadAllText(Path.Combine(root, "manifests", "commercial-av-catalog.json")), CatalogAuthority.AwarenessOnly);
        Assert.IsGreaterThan(0, operational.Items.Count);
        Assert.IsGreaterThan(0, awareness.Items.Count);
        Assert.IsFalse(operational.Items.Concat(awareness.Items).Any(item => item.HasManagedExecutionAuthority));
        Assert.IsTrue(awareness.Items.All(item => item.Authority == CatalogAuthority.AwarenessOnly));
    }

    [TestMethod]
    public void CatalogParserRejectsUnknownFieldsAndDuplicateIds()
    {
        var parser = new CatalogParser(new DateOnly(2026, 8, 27));
        const string unknown = """{"SchemaVersion":3,"Packages":[],"Unexpected":true}""";
        Assert.ThrowsExactly<CatalogValidationException>(() => parser.ParseExternalCatalog(unknown));
        var package = Managed("Example.Package");
        Assert.ThrowsExactly<CatalogValidationException>(() => parser.NormalizeManagedCatalog([package, package], "(?i)forbidden"));
    }

    [TestMethod]
    public void CatalogAuthorityIsNotGrantedByParsingExternalRecords()
    {
        var managed = Definition("Managed.Package", CatalogAuthority.ManagedWinGet, ProviderKind.WinGet);
        var external = Definition("External.Package", CatalogAuthority.OperationalExternal, ProviderKind.External);
        var awareness = Definition("Awareness.Package", CatalogAuthority.AwarenessOnly, ProviderKind.External);
        Assert.IsTrue(managed.HasManagedExecutionAuthority);
        Assert.IsFalse(external.HasManagedExecutionAuthority);
        Assert.IsFalse(awareness.HasManagedExecutionAuthority);
    }

    [TestMethod]
    public void FiltersComposeWithoutUiState()
    {
        var dsp = Definition("Vendor.Dsp", vendor: "Acme", types: [ApplicationType.DSPAudio], roles: [PackageRole.DSPEngineering]);
        var video = Definition("Other.Video", vendor: "Other", types: [ApplicationType.BroadcastVideo], roles: [PackageRole.BroadcastVideo]);
        var query = new CatalogQuery(new HashSet<PackageProfile> { PackageProfile.Standard }, new HashSet<PackagePriority>(), "Acme",
            CatalogDiscipline.DSP, new HashSet<PackageRole> { PackageRole.DSPEngineering }, "dsp", QuickView.Missing);
        var result = new CatalogQueryService().Apply([
            new(dsp, PackageStatus.Missing, false, string.Empty),
            new(video, PackageStatus.Missing, false, string.Empty)], query);
        Assert.HasCount(1, result);
        Assert.AreEqual("Vendor.Dsp", result[0].Package.Id);
    }

    [TestMethod]
    public void PlanningPreservesUnavailableAndWarningStates()
    {
        var external = Definition("External.Package", CatalogAuthority.OperationalExternal, ProviderKind.External,
            detection: DetectionMode.Registry, release: ReleaseMode.InventoryOnly, delivery: DeliveryMode.VendorPage);
        var partial = new PlanningService().Evaluate(external, new PackageEvidence(true, true, false, InventoryQuality.Partial, false, string.Empty, [], false, string.Empty, "One source unavailable.", string.Empty));
        var unavailable = new PlanningService().Evaluate(external, new PackageEvidence(true, true, false, InventoryQuality.Unavailable, false, string.Empty, [], false, string.Empty, "All sources unavailable.", string.Empty));
        Assert.AreEqual(PackageStatus.InventoryIncomplete, partial.Status);
        Assert.AreEqual(PackageStatus.InventoryUnavailable, unavailable.Status);
        Assert.AreNotEqual(PackageStatus.Missing, partial.Status);
        Assert.AreNotEqual(PackageStatus.Current, unavailable.Status);
        var policy = new SelectionPolicy();
        Assert.AreEqual(PolicyDisposition.NotActionable, policy.Evaluate(partial, RebootState.Clear, false).Disposition);
        Assert.AreEqual(PolicyDisposition.NotActionable, policy.Evaluate(unavailable, RebootState.Clear, false).Disposition);
    }

    [TestMethod]
    public void SelectionAndRebootPolicyRemainRiskSensitive()
    {
        var planner = new PlanningService();
        var policy = new SelectionPolicy();
        var low = Definition("Managed.Low");
        var driver = Definition("Managed.Driver", risk: PackageRisk.Driver);
        var evidence = new PackageEvidence(true, false, true, InventoryQuality.Complete, false, string.Empty, [], false, string.Empty, string.Empty, string.Empty);
        var reboot = new RebootState(true, [RebootReason.WindowsUpdate], "Windows Update reboot pending");
        Assert.AreEqual(PolicyDisposition.Allowed, policy.Evaluate(planner.Evaluate(low, evidence), reboot, true).Disposition);
        Assert.AreEqual(PolicyDisposition.Blocked, policy.Evaluate(planner.Evaluate(driver, evidence), reboot, true).Disposition);
        Assert.AreEqual(PolicyDisposition.RequiresAcknowledgement, policy.Evaluate(planner.Evaluate(driver, evidence), RebootState.Clear, false).Disposition);

        // Full risk-sensitive reboot matrix across both supported reboot reasons. A low-risk package
        // stays actionable; every risk-bearing class is blocked until Windows restarts, and without a
        // pending reboot each risk-bearing class still requires a run-specific acknowledgement.
        var service = Definition("Managed.Service", risk: PackageRisk.Service);
        var listener = Definition("Managed.Listener", risk: PackageRisk.Listener);
        foreach (var reason in new[] { RebootReason.WindowsUpdate, RebootReason.ComponentBasedServicing })
        {
            var pending = new RebootState(true, [reason], $"{reason} requires a restart");
            Assert.AreEqual(PolicyDisposition.Allowed, policy.Evaluate(planner.Evaluate(low, evidence), pending, true).Disposition);
            foreach (var risky in new[] { driver, service, listener })
            {
                Assert.AreEqual(PolicyDisposition.Blocked, policy.Evaluate(planner.Evaluate(risky, evidence), pending, true).Disposition);
            }
        }
        foreach (var risky in new[] { driver, service, listener })
        {
            Assert.AreEqual(PolicyDisposition.RequiresAcknowledgement, policy.Evaluate(planner.Evaluate(risky, evidence), RebootState.Clear, false).Disposition);
            Assert.AreEqual(PolicyDisposition.Allowed, policy.Evaluate(planner.Evaluate(risky, evidence), RebootState.Clear, true).Disposition);
        }
    }

    private static ManagedPackageInput Managed(string id) => new("Standard", id, id, "Example", "None", "Test", "Allowlisted", "Allowlisted");

    private static PackageDefinition Definition(
        string id,
        CatalogAuthority authority = CatalogAuthority.ManagedWinGet,
        ProviderKind provider = ProviderKind.WinGet,
        string vendor = "Example",
        IReadOnlyList<ApplicationType>? types = null,
        IReadOnlyList<PackageRole>? roles = null,
        PackageRisk risk = PackageRisk.None,
        DetectionMode detection = DetectionMode.WinGet,
        ReleaseMode release = ReleaseMode.None,
        DeliveryMode delivery = DeliveryMode.None) =>
        new(id, id, vendor, string.Empty, "Test", provider, authority, PackageProfile.Standard, PackagePriority.P2, risk,
            authority == CatalogAuthority.ManagedWinGet ? DeploymentPolicy.Allowlisted : DeploymentPolicy.ManualHold,
            authority == CatalogAuthority.ManagedWinGet ? MaintenancePolicy.Allowlisted : MaintenancePolicy.Hold,
            authority == CatalogAuthority.ManagedWinGet ? DeploymentClass.Managed : DeploymentClass.ManualHandoff,
            authority == CatalogAuthority.ManagedWinGet ? CatalogMaintenancePolicy.Managed : CatalogMaintenancePolicy.Manual,
            VersionRule.Latest, VersionCouplingMode.Independent, string.Empty, Lifecycle.Current, types ?? [], roles ?? [], [],
            [LicensingModel.Free], ["PUBLIC-DL"], DistributionPolicy.PackageManagerOnly, [InstallationForm.WinGet], [SupportedOperatingSystem.Windows],
            delivery, release, detection, DetectionVersionPolicy.None, "Stable", string.Empty, string.Empty, string.Empty, [],
            null, null, null, null, null, risk == PackageRisk.Driver, risk == PackageRisk.Service, risk == PackageRisk.Listener, null, string.Empty, []);

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "VERSION"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
