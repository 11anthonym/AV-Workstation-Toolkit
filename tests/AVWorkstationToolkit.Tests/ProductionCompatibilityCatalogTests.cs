using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.Application.Compatibility;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class ProductionCompatibilityCatalogTests
{
    [TestMethod]
    public void ProductionCatalogParsesWithApprovedReferenceAndCompletedBatchFiveVendors()
    {
        var catalog = LoadCatalog();

        Assert.HasCount(162, catalog.Products);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "7thSense", "Adamson", "AFMG", "AJA Video Systems", "Alcorn McBride", "Allen & Heath", "AMX", "Analog Way",
                "Angry IP Scanner Project", "Ashly Audio", "AtlasIED", "Atlona", "Audinate", "Audio-Technica", "AV Stumpfl", "AVer",
                "Avolites", "Barco", "Biamp", "Blackmagic Design", "Bose Professional", "BrightSign", "Brompton Technology", "BSS",
                "Capture Visualisation", "ChamSys", "Christie", "Cisco", "Clear-Com", "ClearOne", "Colorlight", "Crestron",
                "d&b audiotechnik", "Datapath", "Dataton", "Dell / Waves", "DELTACAST", "Disguise", "Epson", "ETC", "Extron",
                "Figure 53", "FileZilla Project", "Flachmann und Heggelbacher", "Green Hippo", "Green-GO", "HP Poly", "Huddly",
                "HW group", "Intermodulation Analysis", "Jabra", "JBL Professional", "Kramer", "L-Acoustics", "Lake",
                "LEA Professional", "Lectrosonics", "LG", "Lightware", "Logitech", "Luminex", "MA Lighting", "Magewell", "Martin Audio",
                "Matrox Video", "Medialon", "Mersive", "Meyer Sound", "Microsoft", "Milan Manager", "Multiple vendors", "NagleCode", "NDI", "NETGEAR", "Q-SYS"
            },
            catalog.Products.Select(item => item.Vendor).Distinct().ToArray());
        Assert.IsGreaterThan(0, catalog.ReleaseFamilies.Count);
        Assert.IsGreaterThan(0, catalog.DeviceSoftwareRelations.Count);
        Assert.HasCount(0, catalog.InstalledVersions);
        Assert.IsTrue(catalog.Products.All(item => item.OfficialSourceUri.Scheme == Uri.UriSchemeHttps));
    }

    [TestMethod]
    public void QsysDesignerIsOneProductWithCurrentLtsAndArchivedFamilies()
    {
        var catalog = LoadCatalog();
        var product = catalog.FindProducts("QDS").Single();
        var families = catalog.ReleaseFamilies.Where(item => item.ProductId == product.Id).ToArray();

        Assert.AreEqual("QSYSDesigner", product.Id.Value);
        Assert.HasCount(3, families);
        CollectionAssert.AreEquivalent(
            new[] { ReleaseFamilyKind.Current, ReleaseFamilyKind.Lts, ReleaseFamilyKind.Archived },
            families.Select(item => item.Kind).ToArray());
        Assert.HasCount(1, catalog.Products.Where(item => item.Id.Value == "QSYSDesigner").ToArray());
        Assert.IsTrue(families.All(item => item.MinimumVersion is null && item.MaximumVersion is null));
        Assert.IsTrue(families.All(item => item.Constraints.Contains("project", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Cp4nReverseLookupReturnsApprovedSoftwareSet()
    {
        var relations = LoadCatalog().GetSoftwareForDevice("CP4N");

        CollectionAssert.AreEquivalent(
            new[] { "Crestron.SIMPLWindows", "Crestron.Database", "Crestron.DeviceDatabase", "Crestron.Toolbox" },
            relations.Select(item => item.ProductId.Value).Distinct().ToArray());
        Assert.IsTrue(relations.All(item => item.Confidence == CompatibilityEvidenceConfidence.VendorDocumented));
        Assert.IsTrue(relations.Where(item => item.ProductId.Value == "Crestron.Toolbox").Select(item => item.Purpose)
            .Order().SequenceEqual(new[] { DeviceSoftwarePurpose.Diagnostics, DeviceSoftwarePurpose.Firmware }.Order()));
    }

    [TestMethod]
    public void DmNvxLookupKeepsToolAndToolboxPurposesSeparate()
    {
        var relations = LoadCatalog().GetSoftwareForDevice("DM-NVX");
        var nvxTool = relations.Where(item => item.ProductId.Value == "Crestron.DMNVXTool").Select(item => item.Purpose).Order().ToArray();
        var toolbox = relations.Where(item => item.ProductId.Value == "Crestron.Toolbox").Select(item => item.Purpose).Order().ToArray();

        CollectionAssert.AreEquivalent(
            new[] { DeviceSoftwarePurpose.Configuration, DeviceSoftwarePurpose.Commissioning, DeviceSoftwarePurpose.Diagnostics, DeviceSoftwarePurpose.Firmware },
            nvxTool);
        CollectionAssert.AreEquivalent(new[] { DeviceSoftwarePurpose.Discovery, DeviceSoftwarePurpose.Diagnostics }, toolbox);
    }

    [TestMethod]
    public void NexiaRemainsLegacyManualInformationWithoutExecutionAuthority()
    {
        var root = RepositoryRoot();
        var compatibility = LoadCatalog();
        var nexia = compatibility.FindProducts("Nexia").Single();
        var nexiaRelations = compatibility.GetDevicesForSoftware(nexia.Id);
        var packageCatalog = new RepositoryCatalogLoader().Load(root);

        Assert.AreEqual(Lifecycle.Legacy, nexia.Lifecycle);
        Assert.IsTrue(nexiaRelations.Any(item => item.Purpose == DeviceSoftwarePurpose.LegacyService));
        Assert.IsTrue(nexiaRelations.All(item => item.Constraints.Contains("Manual informational", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(packageCatalog.Items.Any(item => item.Id.Equals("Biamp.Nexia", StringComparison.OrdinalIgnoreCase)));
        CollectionAssert.DoesNotContain(typeof(Product).GetProperties().Select(item => item.Name).ToArray(), "Authority");
        CollectionAssert.DoesNotContain(typeof(DeviceSoftwareRelation).GetProperties().Select(item => item.Name).ToArray(), "Delivery");
    }

    [TestMethod]
    public void QueryServiceResolvesProductAndDeviceAliasesInBothDirections()
    {
        var service = CreateQueryService();

        Assert.AreEqual("QSYSDesigner", service.SearchProducts("QDS").Single().Id.Value);
        Assert.AreEqual("Crestron.4Series", service.SearchDevices("CP4N").Single().DeviceFamilyId);
        Assert.AreEqual("Crestron.DMNVX", service.SearchDevices("DM-NVX").Single().DeviceFamilyId);
        Assert.IsTrue(service.GetSoftwareForDevice("Crestron CP4N").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Crestron.SIMPLWindows"));
        Assert.IsTrue(service.GetDevicesForProduct(new SoftwareProductId("Crestron.Toolbox"))
            .Any(item => item.ExactModelIds.Contains("CP4N")));
        Assert.HasCount(3, service.GetReleaseFamilies(new SoftwareProductId("QSYSDesigner")));
    }

    [TestMethod]
    public void BatchTwoDeviceRelationsAreEvidenceBackedAndAliasesResolve()
    {
        var service = CreateQueryService();

        var aja = service.GetSoftwareForDevice("AJA Mini-Converters").SelectMany(group => group.Software).ToArray();
        CollectionAssert.AreEquivalent(
            new[] { DeviceSoftwarePurpose.Configuration, DeviceSoftwarePurpose.Firmware },
            aja.Where(item => item.ProductId.Value == "AJA.MiniConfig").Select(item => item.Purpose).ToArray());

        var ahm = service.GetSoftwareForDevice("AHM-64").SelectMany(group => group.Software).ToArray();
        Assert.IsTrue(ahm.Any(item => item.ProductId.Value == "AllenHeath.AHMSystemManager" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        Assert.IsTrue(service.SearchProducts("Velocity Device Manager").Any(item => item.Id.Value == "Atlona.VelocityDeviceManager"));
        Assert.IsTrue(service.SearchProducts("Blueprint AV").Any(item => item.Id.Value == "Adamson.BlueprintAV"));
        Assert.IsTrue(service.GetSoftwareForDevice("AQM408").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Ashly.AquaControlPortal" && item.Purpose == DeviceSoftwarePurpose.Discovery));
    }

    [TestMethod]
    public void BatchThreeDeviceRelationsAreEvidenceBackedAndAliasesResolve()
    {
        var service = CreateQueryService();

        var dante = service.GetSoftwareForDevice("Dante-enabled device").SelectMany(group => group.Software).ToArray();
        CollectionAssert.AreEquivalent(
            new[] { DeviceSoftwarePurpose.Configuration, DeviceSoftwarePurpose.Diagnostics, DeviceSoftwarePurpose.Discovery, DeviceSoftwarePurpose.Firmware },
            dante.Where(item => item.ProductId.Value == "Audinate.DanteController").Select(item => item.Purpose).ToArray());

        var projector = service.GetSoftwareForDevice("UDX").SelectMany(group => group.Software).ToArray();
        Assert.IsTrue(projector.Any(item => item.ProductId.Value == "Barco.ProjectorToolset" && item.Purpose == DeviceSoftwarePurpose.Diagnostics));

        var atem = service.GetSoftwareForDevice("ATEM Mini").SelectMany(group => group.Software).ToArray();
        Assert.IsTrue(atem.Any(item => item.ProductId.Value == "Blackmagic.ATEMSoftwareControl" && item.Purpose == DeviceSoftwarePurpose.Configuration));

        var bose = service.GetSoftwareForDevice("EX-1280C").SelectMany(group => group.Software).ToArray();
        Assert.IsTrue(bose.Any(item => item.ProductId.Value == "Bose.ControlSpaceDesigner" && item.Purpose == DeviceSoftwarePurpose.Commissioning));

        var bss = service.GetSoftwareForDevice("BSS AVX").SelectMany(group => group.Software).ToArray();
        Assert.IsTrue(bss.Any(item => item.ProductId.Value == "BSS.AVXManager" && item.Purpose == DeviceSoftwarePurpose.Discovery));
    }

    [TestMethod]
    public void BatchFourRelationsAndVersionFamiliesAreEvidenceBackedAndAliasesResolve()
    {
        var service = CreateQueryService();

        var christie = service.GetSoftwareForDevice("Christie 3DLP projector").SelectMany(group => group.Software).ToArray();
        Assert.IsTrue(christie.Any(item => item.ProductId.Value == "Christie.Conductor" && item.Purpose == DeviceSoftwarePurpose.Monitoring));

        var watchout = service.SearchProducts("WATCHOUT").Single();
        CollectionAssert.AreEquivalent(
            new[] { ReleaseFamilyKind.Current, ReleaseFamilyKind.Legacy },
            service.GetReleaseFamilies(watchout.Id).Select(item => item.Kind).ToArray());

        Assert.IsTrue(service.GetSoftwareForDevice("Fx4").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Datapath.WallDesigner" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        Assert.IsTrue(service.SearchProducts("LED Setting").Any(item => item.Id.Value == "Colorlight.LEDSetting"));
    }

    [TestMethod]
    public void BatchFiveRelationsKeepDistinctUtilitiesAndAliasesReadOnly()
    {
        var service = CreateQueryService();

        var extron = service.GetSoftwareForDevice("Extron XTP").SelectMany(group => group.Software).ToArray();
        Assert.IsTrue(extron.Any(item => item.ProductId.Value == "Extron.XTPSystemConfiguration" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        Assert.IsTrue(service.GetSoftwareForDevice("Poly Studio V12").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "HPPoly.StudioDesktop" && item.Purpose == DeviceSoftwarePurpose.Firmware));
        Assert.IsTrue(service.SearchProducts("HWg Config").Any(item => item.Id.Value == "HWGroup.HWgConfig"));
        Assert.IsTrue(service.SearchProducts("QLab").Single().OfficialSourceUri.Host.Equals("qlab.app", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void BatchSixRelationsKeepVendorScopesAndReleaseFamiliesExplicit()
    {
        var service = CreateQueryService();

        Assert.IsTrue(service.GetSoftwareForDevice("LA12X").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "LAcoustics.LANetworkManager" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        Assert.IsTrue(service.GetSoftwareForDevice("DCR822").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Lectrosonics.WirelessDesigner" && item.Purpose == DeviceSoftwarePurpose.Discovery));
        Assert.IsTrue(service.GetSoftwareForDevice("Luminex GigaCore").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Luminex.Araneo" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        Assert.HasCount(2, service.GetReleaseFamilies(new SoftwareProductId("Jabra.Direct")));
        Assert.HasCount(2, service.GetReleaseFamilies(new SoftwareProductId("MALighting.grandMA3onPC")));
    }

    [TestMethod]
    public void BatchSevenRelationsAvoidProtocolWideAuthorityAndRetainScopedLookups()
    {
        var service = CreateQueryService();

        Assert.IsTrue(service.GetSoftwareForDevice("USB Capture HDMI").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Magewell.USBCaptureUtility"));
        Assert.IsTrue(service.GetSoftwareForDevice("M4350").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "NETGEAR.EngageController"));
        Assert.IsTrue(service.GetSoftwareForDevice("Showmaster Pro").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Medialon.Manager"));
        Assert.IsFalse(service.SearchDevices("NDI device").Any());
        Assert.HasCount(2, service.GetReleaseFamilies(new SoftwareProductId("MartinAudio.Display3")));
    }

    [TestMethod]
    public async Task BatchTwoInstalledEvidenceRemainsExplicitAndDescriptive()
    {
        var service = CreateQueryService();
        var product = service.SearchProducts("Mini-Config").Single();
        var evidence = await service.GetInstalledVersionsAsync(product.Id);

        Assert.HasCount(1, evidence);
        Assert.AreEqual(InstalledVersionEvidenceState.Unknown, evidence[0].State);
        Assert.AreEqual(string.Empty, evidence[0].RawVersion);
        Assert.IsNull(evidence[0].NormalizedVersion);
        CollectionAssert.DoesNotContain(typeof(Product).GetProperties().Select(item => item.Name).ToArray(), "Authority");
        CollectionAssert.DoesNotContain(typeof(DeviceSoftwareRelation).GetProperties().Select(item => item.Name).ToArray(), "Delivery");
    }

    [TestMethod]
    public async Task UnresolvedInstalledVersionEvidenceRemainsExplicit()
    {
        var service = CreateQueryService();

        foreach (var product in service.SearchProducts())
        {
            var evidence = await service.GetInstalledVersionsAsync(product.Id);
            Assert.HasCount(1, evidence);
            Assert.AreEqual(InstalledVersionEvidenceState.Unknown, evidence[0].State);
            Assert.IsNull(evidence[0].NormalizedVersion);
            Assert.AreEqual(string.Empty, evidence[0].RawVersion);
            Assert.IsGreaterThan(0, evidence[0].Detail.Length);
        }
    }

    [TestMethod]
    public void CompiledCompositionLoadsEmbeddedCompatibilityBoundaryReadOnly()
    {
        var root = RepositoryRoot();
        var services = CompiledAppComposition.Create(root);
        var launcherSource = File.ReadAllText(Path.Combine(root, "src", "AVWorkstationToolkit.Launcher", "Program.cs"));

        Assert.HasCount(162, services.Compatibility.SearchProducts());
        Assert.IsTrue(services.Compatibility.SearchDevices("CP4N").Any());
        StringAssert.Contains(launcherSource, "manifests/software-compatibility.json");
        Assert.IsNull(services.Actions);
        Assert.IsNull(services.PackageDelivery);
    }

    [TestMethod]
    public void CompatibilityCompositionDoesNotChangePackageOrWorkerAuthority()
    {
        var root = RepositoryRoot();
        var packages = new RepositoryCatalogLoader().Load(root);
        var compatibility = new RepositoryCompatibilityCatalogLoader().Load(root);
        var packageCount = packages.Items.Count;

        Assert.HasCount(162, compatibility.Products);
        Assert.HasCount(packageCount, new RepositoryCatalogLoader().Load(root).Items);
        Assert.IsTrue(packages.Items.Where(item =>
                item.Id is "QSC.QSYSDesigner.LTS" or "Biamp.Tesira" or "Biamp.Canvas" or
                    "Crestron.SIMPLWindows" or "Crestron.Database" or "Crestron.DeviceDatabase" or
                    "Crestron.Toolbox" or "Crestron.DMNVXTool")
            .All(item => !item.HasManagedExecutionAuthority));
        var batchFourAndFiveVendors = new HashSet<string>(StringComparer.Ordinal)
        {
            "ChamSys", "Christie", "Cisco", "Clear-Com", "ClearOne", "Colorlight", "d&b audiotechnik", "Datapath", "Dataton",
            "Dell / Waves", "DELTACAST", "Disguise", "Epson", "ETC", "Extron", "Figure 53", "FileZilla Project",
            "Flachmann und Heggelbacher", "Green Hippo", "Green-GO", "HP Poly", "Huddly", "HW group", "Intermodulation Analysis"
        };
        var batchFourAndFiveProductIds = compatibility.Products.Where(item => batchFourAndFiveVendors.Contains(item.Vendor))
            .Select(item => item.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.IsTrue(packages.Items.Where(item => batchFourAndFiveProductIds.Contains(item.Id)).All(item => !item.HasManagedExecutionAuthority));
        var batchSixVendors = new HashSet<string>(StringComparer.Ordinal)
        {
            "Jabra", "JBL Professional", "Kramer", "L-Acoustics", "Lake", "LEA Professional", "Lectrosonics", "LG",
            "Lightware", "Logitech", "Luminex", "MA Lighting"
        };
        var batchSixProductIds = compatibility.Products.Where(item => batchSixVendors.Contains(item.Vendor))
            .Select(item => item.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.IsTrue(packages.Items.Where(item => batchSixProductIds.Contains(item.Id)).All(item => !item.HasManagedExecutionAuthority));
        var batchSevenVendors = new HashSet<string>(StringComparer.Ordinal)
        {
            "Magewell", "Martin Audio", "Matrox Video", "Medialon", "Mersive", "Meyer Sound", "Microsoft", "Milan Manager",
            "Multiple vendors", "NagleCode", "NDI", "NETGEAR"
        };
        var batchSevenProductIds = compatibility.Products.Where(item => batchSevenVendors.Contains(item.Vendor))
            .Select(item => item.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.IsTrue(packages.Items.Where(item => batchSevenProductIds.Contains(item.Id)).All(item => !item.HasManagedExecutionAuthority));
        CollectionAssert.DoesNotContain(typeof(CompatibilityProductSummary).GetProperties().Select(item => item.Name).ToArray(), "Authority");
        CollectionAssert.DoesNotContain(typeof(RelevantSoftwareSummary).GetProperties().Select(item => item.Name).ToArray(), "Provider");
        CollectionAssert.DoesNotContain(typeof(RelevantSoftwareSummary).GetProperties().Select(item => item.Name).ToArray(), "Delivery");
    }

    private static CompatibilityCatalogQueryService CreateQueryService() => new(
        LoadCatalog(),
        new UnresolvedInstalledVersionEvidenceProvider());

    private static SoftwareCompatibilityCatalog LoadCatalog() =>
        new RepositoryCompatibilityCatalogLoader().Load(RepositoryRoot());

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "VERSION"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
