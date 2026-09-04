using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class SoftwareCompatibilityCatalogTests
{
    [TestMethod]
    public void VersionOneFixturePreservesProductAndReleaseFamilyIdentity()
    {
        var catalog = LoadFixture();

        Assert.HasCount(2, catalog.Products);
        Assert.HasCount(2, catalog.ReleaseFamilies);
        Assert.AreEqual(ReleaseFamilyKind.Lts, catalog.GetRequiredReleaseFamily(new ReleaseFamilyId("Example.Designer.LTS")).Kind);
        Assert.ThrowsExactly<CatalogValidationException>(() => new CompatibilityCatalogParser().Parse(FixtureJson().Replace(
            "\"Id\": \"Example.Designer.LTS\"", "\"Id\": \"Example.Designer.Current\"", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void MultipleObservedVersionsRemainSeparateEvidenceForOneProduct()
    {
        var installed = LoadFixture().GetInstalledVersions(new SoftwareProductId("Example.Designer"));

        Assert.HasCount(2, installed);
        CollectionAssert.AreEquivalent(new[] { "10.2.3", "9.4.8" }, installed.Select(item => item.RawVersion).ToArray());
        Assert.IsTrue(installed.All(item => item.State == InstalledVersionEvidenceState.Observed));
    }

    [TestMethod]
    public void RelationshipsDriveBothSoftwareAndDeviceIndexesIncludingAliases()
    {
        var catalog = LoadFixture();

        Assert.HasCount(1, catalog.FindProducts("EXD"));
        Assert.AreEqual("Example.Designer", catalog.FindProducts("Example Configurator")[0].Id.Value);
        Assert.HasCount(1, catalog.GetDevicesForSoftware(new SoftwareProductId("Example.Designer")));
        var deviceRelations = catalog.GetSoftwareForDevice("Core100");
        Assert.HasCount(2, deviceRelations);
        CollectionAssert.AreEquivalent(new[] { "Example.Designer", "Example.ServiceTool" }, deviceRelations.Select(item => item.ProductId.Value).ToArray());
    }

    [TestMethod]
    public void RelationValidationEnforcesSemanticUniquenessAndOptionalFamilyOwnership()
    {
        var parser = new CompatibilityCatalogParser();
        var duplicate = FixtureJson()
            .Replace("\"ProductId\": \"Example.ServiceTool\",\n      \"ReleaseFamilyId\": null", "\"ProductId\": \"Example.Designer\",\n      \"ReleaseFamilyId\": \"Example.Designer.Current\"", StringComparison.Ordinal)
            .Replace("\"Purpose\": \"Diagnostics\"", "\"Purpose\": \"Programming\"", StringComparison.Ordinal);
        Assert.ThrowsExactly<CatalogValidationException>(() => parser.Parse(duplicate));

        var mismatchedFamily = FixtureJson().Replace(
            "\"ProductId\": \"Example.ServiceTool\",\n      \"ReleaseFamilyId\": null", "\"ProductId\": \"Example.ServiceTool\",\n      \"ReleaseFamilyId\": \"Example.Designer.Current\"", StringComparison.Ordinal);
        Assert.ThrowsExactly<CatalogValidationException>(() => parser.Parse(mismatchedFamily));
    }

    [TestMethod]
    public void EvidenceAndConfidenceAreStrictAndRequireHttps()
    {
        var parser = new CompatibilityCatalogParser();
        Assert.ThrowsExactly<CatalogValidationException>(() => parser.Parse(FixtureJson().Replace(
            "\"Confidence\": \"Unresolved\"", "\"Confidence\": \"ManufacturerInferred\"", StringComparison.Ordinal)));
        Assert.ThrowsExactly<CatalogValidationException>(() => parser.Parse(FixtureJson().Replace(
            "https://support.example.test/core-100/service", "http://support.example.test/core-100/service", StringComparison.Ordinal)));
        Assert.ThrowsExactly<CatalogValidationException>(() => parser.Parse(FixtureJson().Replace(
            "\"EvidenceKind\": \"VendorSupportArticle\"", "\"EvidenceKind\": \"Unknown\"", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void IncompleteInventoryDoesNotClaimAbsenceOrVersion()
    {
        var catalog = LoadFixture();
        var incomplete = catalog.GetInstalledVersions(new SoftwareProductId("Example.ServiceTool")).Single();

        Assert.AreEqual(InstalledVersionEvidenceState.Incomplete, incomplete.State);
        Assert.IsNull(incomplete.NormalizedVersion);
        Assert.AreEqual(string.Empty, incomplete.RawVersion);
        var unknown = new CompatibilityCatalogParser().Parse(FixtureJson().Replace(
            "\"State\": \"Incomplete\"", "\"State\": \"Unknown\"", StringComparison.Ordinal));
        Assert.AreEqual(InstalledVersionEvidenceState.Unknown, unknown.GetInstalledVersions(new SoftwareProductId("Example.ServiceTool")).Single().State);
        Assert.ThrowsExactly<CatalogValidationException>(() => new CompatibilityCatalogParser().Parse(FixtureJson().Replace(
            "\"State\": \"Incomplete\",\n      \"RawDisplayName\": \"\",\n      \"RawVersion\": \"\"", "\"State\": \"Incomplete\",\n      \"RawDisplayName\": \"\",\n      \"RawVersion\": \"1.0\"", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void CompatibilityMetadataCannotGrantManagedExecutionAuthority()
    {
        var product = LoadFixture().FindProducts("EXD").Single();
        var external = Package("Example.Designer", CatalogAuthority.OperationalExternal, ProviderKind.External);
        var awareness = Package("Example.ServiceTool", CatalogAuthority.AwarenessOnly, ProviderKind.External);

        Assert.IsNotNull(product);
        Assert.IsFalse(external.HasManagedExecutionAuthority);
        Assert.IsFalse(awareness.HasManagedExecutionAuthority);
        CollectionAssert.DoesNotContain(typeof(DeviceSoftwareRelation).GetProperties().Select(item => item.Name).ToArray(), "Authority");
        CollectionAssert.DoesNotContain(typeof(DeviceSoftwareRelation).GetProperties().Select(item => item.Name).ToArray(), "Provider");
    }

    [TestMethod]
    public void ExistingProductionCatalogsRemainBackwardCompatibleWithAdditiveCompatibilityManifest()
    {
        var root = RepositoryRoot();
        var parser = new CatalogParser(DateOnly.FromDateTime(DateTime.UtcNow));
        var external = parser.ParseExternalCatalog(File.ReadAllText(Path.Combine(root, "manifests", "external-applications.json")));
        var awareness = parser.ParseExternalCatalog(File.ReadAllText(Path.Combine(root, "manifests", "commercial-av-catalog.json")), CatalogAuthority.AwarenessOnly);

        Assert.IsGreaterThan(0, external.Items.Count);
        Assert.IsGreaterThan(0, awareness.Items.Count);
        Assert.IsFalse(external.Items.Concat(awareness.Items).Any(item => item.HasManagedExecutionAuthority));
        Assert.IsTrue(File.Exists(Path.Combine(root, "manifests", "software-compatibility.json")));
    }

    [TestMethod]
    public void ParserRejectsUnknownFieldsAndDuplicateJsonProperties()
    {
        var parser = new CompatibilityCatalogParser();
        Assert.ThrowsExactly<CatalogValidationException>(() => parser.Parse(FixtureJson().Replace(
            "\"SchemaVersion\": 1,", "\"SchemaVersion\": 1,\n  \"Unexpected\": true,", StringComparison.Ordinal)));
        Assert.ThrowsExactly<CatalogValidationException>(() => parser.Parse(FixtureJson().Replace(
            "\"SchemaVersion\": 1,", "\"SchemaVersion\": 1,\n  \"SchemaVersion\": 1,", StringComparison.Ordinal)));
        Assert.ThrowsExactly<CatalogValidationException>(() => parser.Parse(FixtureJson().Replace(
            "\"SchemaVersion\": 1", "\"SchemaVersion\": 2", StringComparison.Ordinal)));
    }

    private static SoftwareCompatibilityCatalog LoadFixture() => new CompatibilityCatalogParser().Parse(FixtureJson());

    private static string FixtureJson() => File.ReadAllText(Path.Combine(RepositoryRoot(), "tests", "fixtures", "software-compatibility", "schema-v1-representative.json"));

    private static PackageDefinition Package(string id, CatalogAuthority authority, ProviderKind provider) => new(
        id, id, "Example AV", string.Empty, "Test", provider, authority, PackageProfile.Field, PackagePriority.P2, PackageRisk.None,
        DeploymentPolicy.ManualHold, MaintenancePolicy.Hold, DeploymentClass.ManualHandoff, CatalogMaintenancePolicy.Manual,
        VersionRule.Unknown, VersionCouplingMode.Independent, string.Empty, Lifecycle.Current, [], [], [],
        [LicensingModel.UnknownCost], ["UNKNOWN-ACCESS"], DistributionPolicy.LinkOnly, [], [SupportedOperatingSystem.Windows],
        DeliveryMode.Awareness, ReleaseMode.None, DetectionMode.None, DetectionVersionPolicy.None, string.Empty, string.Empty,
        string.Empty, string.Empty, [], null, null, null, null, null, null, null, null, null, string.Empty, []);

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "VERSION"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
