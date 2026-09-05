using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.Application.Compatibility;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class ProductionCompatibilityCatalogTests
{
    [TestMethod]
    public void ProductionCatalogParsesWithCompleteFrozenManufacturerLedger()
    {
        var catalog = LoadCatalog();

        Assert.HasCount(245, catalog.Products);
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
                "Matrox Video", "Medialon", "Mersive", "Meyer Sound", "Microsoft", "Milan Manager", "Multiple vendors", "NagleCode", "NDI", "NETGEAR",
                "NEXO", "NovaStar", "Nureva", "OBS Project", "Obsidian Control Systems", "Open Sound Meter", "Panasonic", "Pingman Tools", "Planar", "Powersoft", "Professional Wireless Systems", "QLC+ Project",
                "Rane Commercial", "Rational Acoustics", "RealTerm Project", "Resolume", "RF Explorer", "Riedel Communications", "Room EQ Wizard", "Ross Video", "RTS Intercoms", "sACNView Project", "Samsung", "ScreenBeam", "Sennheiser", "Sharp NEC Display Solutions", "Shure", "Sony Professional", "SoundBase", "StudioCoast", "Symetrix", "TeraTerm Project", "Unity Intercom", "Uwe Sieber", "Vaddio", "Wisycom", "WolfVision", "Xilica", "Yamaha Professional Audio", "Yealink", "ZeeVee", "Q-SYS"
            },
            catalog.Products.Select(item => item.Vendor).Distinct().ToArray());
        Assert.HasCount(80, catalog.ReleaseFamilies);
        Assert.HasCount(195, catalog.DeviceSoftwareRelations);
        Assert.HasCount(0, catalog.InstalledVersions);
        Assert.IsTrue(catalog.Products.All(item => item.OfficialSourceUri.Scheme == Uri.UriSchemeHttps));
    }

    [TestMethod]
    public void DeviceAliasesResolveToOneCanonicalDeviceFamily()
    {
        var collisions = LoadCatalog().DeviceSoftwareRelations
            .SelectMany(relation => relation.DeviceAliases.Concat(relation.ExactModelIds)
                .Select(alias => new { Alias = alias.Trim(), relation.DeviceFamilyId }))
            .GroupBy(item => item.Alias, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.DeviceFamilyId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.HasCount(0, collisions, $"Device aliases must not identify multiple device families: {string.Join(", ", collisions)}");
    }

    [TestMethod]
    public void CompatibilityDocumentContainsNoExecutionAuthorityFields()
    {
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PackageId", "Provider", "Authority", "Deployment", "Executable", "Command",
            "Credential", "Delivery", "Arguments", "WorkingDirectory"
        };
        using var document = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot(), "manifests", "software-compatibility.json")));
        var present = EnumeratePropertyNames(document.RootElement)
            .Where(forbidden.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.HasCount(0, present, $"Compatibility metadata must remain descriptive: {string.Join(", ", present)}");
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
    public void BatchEightRelationsRemainEvidenceScopedAndGenericToolsStayDescriptive()
    {
        var service = CreateQueryService();

        Assert.IsTrue(service.GetSoftwareForDevice("NXAMPmk2").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "NEXO.NeFu" && item.Purpose == DeviceSoftwarePurpose.Firmware));
        Assert.IsTrue(service.GetSoftwareForDevice("HDL410").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Nureva.App" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        Assert.IsTrue(service.GetSoftwareForDevice("NX3").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Obsidian.ONYX" && item.Purpose == DeviceSoftwarePurpose.Programming));
        Assert.IsTrue(service.GetSoftwareForDevice("WallDirector OS").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Planar.WallDirectorOS"));
        Assert.IsFalse(service.SearchDevices("OBS device").Any());
        Assert.HasCount(2, service.GetReleaseFamilies(new SoftwareProductId("QLCPlus.QLCPlus")));
    }

    [TestMethod]
    public void BatchNineRelationsKeepGenericToolsDescriptiveAndIntercomScopesSpecific()
    {
        var service = CreateQueryService();

        Assert.IsTrue(service.GetSoftwareForDevice("HAL3s").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Rane.Halogen" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        Assert.IsTrue(service.GetSoftwareForDevice("ADAM-M").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "RTS.NEOIntercomManagementSuite"));
        Assert.IsTrue(service.GetSoftwareForDevice("ScreenBeam 1100").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "ScreenBeam.CMSEnterprise"));
        Assert.IsTrue(service.GetSoftwareForDevice("RF Explorer analyzer").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "RFExplorer.ClearWaves"));
        Assert.IsFalse(service.SearchDevices("OBS device").Any());
        Assert.IsFalse(service.SearchDevices("sACN device").Any());
        Assert.HasCount(2, service.GetReleaseFamilies(new SoftwareProductId("Resolume.Arena")));
    }

    [TestMethod]
    public void BatchTenRelationsAreScopedAndDistinctUtilitiesRemainDescriptive()
    {
        var service = CreateQueryService();

        Assert.IsTrue(service.GetSoftwareForDevice("Spectera Base Station").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Sennheiser.SpecteraLinkDesk" && item.Purpose == DeviceSoftwarePurpose.Monitoring));
        Assert.IsTrue(service.GetSoftwareForDevice("AD4Q").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "SoundBase.Pro" && item.Purpose == DeviceSoftwarePurpose.Monitoring));
        Assert.IsTrue(service.GetSoftwareForDevice("BRC-X400").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Sony.RMIPSetupTool" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        Assert.IsTrue(service.GetSoftwareForDevice("Vaddio PTZ").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Vaddio.DeploymentTool" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        Assert.IsTrue(service.SearchProducts("UsbTreeView").Any(item => item.Id.Value == "UweSieber.UsbTreeView"));
        Assert.IsFalse(service.SearchDevices("Tera Term device").Any());
        Assert.IsFalse(service.SearchDevices("vMix device").Any());
        Assert.HasCount(2, service.GetReleaseFamilies(new SoftwareProductId("Symetrix.Composer")));
    }

    [TestMethod]
    public void BatchElevenRelationsKeepProfessionalAudioAndManagedPlatformsScoped()
    {
        var service = CreateQueryService();

        Assert.IsTrue(service.GetSoftwareForDevice("Cynap Core Pro").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "WolfVision.VSolutionLinkPro" && item.Purpose == DeviceSoftwarePurpose.Monitoring));
        Assert.IsTrue(service.GetSoftwareForDevice("Solaro QR1").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Xilica.Designer" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        Assert.IsTrue(service.GetSoftwareForDevice("Neutrino").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Xilica.Designer" && item.ReleaseFamilyId?.Value == "Xilica.Designer.Legacy"));
        Assert.IsTrue(service.GetSoftwareForDevice("DME7").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Yamaha.ProVisionaireDesign"));
        Assert.IsTrue(service.GetSoftwareForDevice("Yealink USB headset").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Yealink.USBConnect" && item.Purpose == DeviceSoftwarePurpose.Diagnostics));
        Assert.IsTrue(service.GetSoftwareForDevice("ZyPerUHD").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "ZeeVee.ZyPerManagementPlatform"));
        Assert.IsFalse(service.SearchDevices("Yealink cloud device").Any());
        Assert.HasCount(2, service.GetReleaseFamilies(new SoftwareProductId("Xilica.Designer")));
        Assert.HasCount(2, service.GetReleaseFamilies(new SoftwareProductId("Yamaha.ProVisionaireDesign")));
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

        Assert.HasCount(245, services.Compatibility.SearchProducts());
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

        Assert.HasCount(245, compatibility.Products);
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
        var batchEightVendors = new HashSet<string>(StringComparer.Ordinal)
        {
            "NEXO", "NovaStar", "Nureva", "OBS Project", "Obsidian Control Systems", "Open Sound Meter", "Panasonic", "Pingman Tools",
            "Planar", "Powersoft", "Professional Wireless Systems", "QLC+ Project"
        };
        var batchEightProductIds = compatibility.Products.Where(item => batchEightVendors.Contains(item.Vendor))
            .Select(item => item.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.IsTrue(packages.Items.Where(item => batchEightProductIds.Contains(item.Id)).All(item => !item.HasManagedExecutionAuthority));
        var batchNineVendors = new HashSet<string>(StringComparer.Ordinal)
        {
            "Rane Commercial", "Rational Acoustics", "RealTerm Project", "Resolume", "RF Explorer", "Riedel Communications", "Room EQ Wizard",
            "Ross Video", "RTS Intercoms", "sACNView Project", "Samsung", "ScreenBeam"
        };
        var batchNineProductIds = compatibility.Products.Where(item => batchNineVendors.Contains(item.Vendor))
            .Select(item => item.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.IsTrue(packages.Items.Where(item => batchNineProductIds.Contains(item.Id)).All(item => !item.HasManagedExecutionAuthority));
        var batchTenVendors = new HashSet<string>(StringComparer.Ordinal)
        {
            "Sennheiser", "Sharp NEC Display Solutions", "Shure", "Sony Professional", "SoundBase", "StudioCoast", "Symetrix",
            "TeraTerm Project", "Unity Intercom", "Uwe Sieber", "Vaddio", "Wisycom"
        };
        var batchTenProductIds = compatibility.Products.Where(item => batchTenVendors.Contains(item.Vendor))
            .Select(item => item.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.IsTrue(packages.Items.Where(item => batchTenProductIds.Contains(item.Id)).All(item => !item.HasManagedExecutionAuthority));
        var batchElevenVendors = new HashSet<string>(StringComparer.Ordinal)
        {
            "WolfVision", "Xilica", "Yamaha Professional Audio", "Yealink", "ZeeVee"
        };
        var batchElevenProductIds = compatibility.Products.Where(item => batchElevenVendors.Contains(item.Vendor))
            .Select(item => item.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.IsTrue(packages.Items.Where(item => batchElevenProductIds.Contains(item.Id)).All(item => !item.HasManagedExecutionAuthority));
        CollectionAssert.DoesNotContain(typeof(CompatibilityProductSummary).GetProperties().Select(item => item.Name).ToArray(), "Authority");
        CollectionAssert.DoesNotContain(typeof(RelevantSoftwareSummary).GetProperties().Select(item => item.Name).ToArray(), "Provider");
        CollectionAssert.DoesNotContain(typeof(RelevantSoftwareSummary).GetProperties().Select(item => item.Name).ToArray(), "Delivery");
    }

    private static CompatibilityCatalogQueryService CreateQueryService() => new(
        LoadCatalog(),
        new UnresolvedInstalledVersionEvidenceProvider());

    private static IEnumerable<string> EnumeratePropertyNames(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var nested in EnumeratePropertyNames(property.Value)) yield return nested;
            }
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var nested in EnumeratePropertyNames(item)) yield return nested;
        }
    }

    private static SoftwareCompatibilityCatalog LoadCatalog() =>
        new RepositoryCompatibilityCatalogLoader().Load(RepositoryRoot());

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "VERSION"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
