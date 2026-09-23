using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.Application.Compatibility;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class ProductionCompatibilityCatalogTests
{
    [TestMethod]
    public void ProductionCatalogParsesWithRequiredVendorsAndNoInstalledEvidence()
    {
        // Loading is itself the uniqueness and referential-integrity assertion: the parser rejects
        // duplicate IDs, a release family or relation naming an unknown product, and a relation whose
        // release family belongs to another product. Restating those here would be tautological, and
        // exact totals only create churn - every added record edited a number in several files.
        var catalog = LoadCatalog();

        Assert.IsGreaterThan(0, catalog.Products.Count);
        Assert.IsGreaterThan(0, catalog.ReleaseFamilies.Count);
        Assert.IsGreaterThan(0, catalog.DeviceSoftwareRelations.Count);

        // No installed-version evidence may ship in the catalog; that is observed on the workstation.
        Assert.HasCount(0, catalog.InstalledVersions);
        Assert.IsTrue(catalog.Products.All(item => item.OfficialSourceUri.Scheme == Uri.UriSchemeHttps));
        Assert.IsTrue(catalog.Products.All(item => !string.IsNullOrWhiteSpace(item.Vendor)));

        // The manufacturers the product exists to cover must stay represented. This is a required
        // floor, not a ledger: adding a vendor does not edit this test.
        var vendors = catalog.Products.Select(item => item.Vendor).ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[]
        {
            "Crestron", "Extron", "AMX", "Biamp", "Q-SYS", "Shure", "Sennheiser", "Audinate",
            "Barco", "Christie", "Yamaha Professional Audio", "BrightSign", "Clear-Com", "Blackmagic Design"
        })
        {
            Assert.Contains(required, vendors);
        }
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
            .Order().SequenceEqual(new[] { DeviceSoftwarePurpose.Commissioning, DeviceSoftwarePurpose.Diagnostics, DeviceSoftwarePurpose.Firmware }.Order()));
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
    public void AvOverIpBatchAProvidesExactQsysAndEndpointScopedRelationshipsWithoutAuthority()
    {
        var service = CreateQueryService();

        var nvx = service.SearchDevices("DM NVX 363").Single(item => item.Hardware?.Id == "Crestron.DMNVX363");
        Assert.AreEqual("Crestron.DMNVX363", nvx.Hardware!.Id);
        Assert.AreEqual(HardwareDeviceCategory.AvOverIp, nvx.Hardware.Category);
        Assert.IsTrue(service.GetSoftwareForDevice(nvx).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Crestron.DMNVXTool" && item.Purpose == DeviceSoftwarePurpose.Configuration));

        var nv32 = service.SearchDevices("NV-32-H").Single();
        Assert.AreEqual("QSYS.NV32H", nv32.Hardware!.Id);
        Assert.AreEqual("QSYS.NVSeries", nv32.DeviceFamilyId);
        Assert.AreEqual(HardwareDeviceCategory.AvOverIp, nv32.Hardware.Category);
        var nv32Software = service.GetSoftwareForDevice(nv32).SelectMany(group => group.Software).ToArray();
        Assert.IsTrue(nv32Software.Any(item => item.ProductId.Value == "QSYSDesigner" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        Assert.IsTrue(nv32Software.Any(item => item.Constraints.Contains("Core Mode", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(nv32Software.Any(item => item.ProductId.Value.Contains("Dante", StringComparison.OrdinalIgnoreCase)));

        var nv21 = service.SearchDevices("NV-21-HU").Single();
        Assert.AreEqual("QSYS.NV21HU", nv21.Hardware!.Id);
        Assert.IsTrue(service.GetSoftwareForDevice(nv21).SelectMany(group => group.Software)
            .Any(item => item.Constraints.Contains("encoder/decoder", StringComparison.OrdinalIgnoreCase)));

        var nv1 = service.SearchDevices("NV-1-H-WE").Single();
        Assert.AreEqual("QSYS.NV1HWE", nv1.Hardware!.Id);
        Assert.IsTrue(service.GetSoftwareForDevice(nv1).SelectMany(group => group.Software)
            .Any(item => item.Constraints.Contains("encoder-only", StringComparison.OrdinalIgnoreCase)));

        var encoder = service.SearchDevices("NVM-302E").Single();
        var decoder = service.SearchDevices("NVM-302D").Single();
        Assert.AreEqual("QSYS.NVM302E", encoder.Hardware!.Id);
        Assert.AreEqual("QSYS.NVM302D", decoder.Hardware!.Id);
        Assert.AreEqual("QSYS.NVMSeries", decoder.DeviceFamilyId);
        Assert.IsTrue(service.GetSoftwareForDevice(decoder).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "QSYSDesigner" && item.Constraints.Contains("decoder", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void AvOverIpBatchBScopesDesktopSoftwareAndBrowserAppliancesToExactModels()
    {
        var service = CreateQueryService();

        var kds = service.SearchDevices("KDS-EN7").Single();
        Assert.AreEqual("Kramer.KDSEN7", kds.Hardware!.Id);
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet, kds.LookupState);
        Assert.HasCount(0, service.GetSoftwareForDevice(kds));
        Assert.IsFalse(service.GetSoftwareForDevice(kds).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Kramer.KConfig"));

        var dss = service.SearchDevices("ConvertIP DSS").Single();
        var matrox = service.GetSoftwareForDevice(dss).SelectMany(group => group.Software).ToArray();
        Assert.AreEqual("Matrox.ConvertIPDSS", dss.Hardware!.Id);
        Assert.IsTrue(matrox.Any(item => item.ProductId.Value == "Matrox.ConvertIPManager" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        Assert.IsFalse(matrox.Any(item => item.ProductId.Value == "Matrox.ConductIP"));
        Assert.IsFalse(service.SearchProducts("Command Center").Any());

        var duet = service.SearchDevices("DuetE-5").Single();
        Assert.AreEqual("Visionary.DuetE5", duet.Hardware!.Id);
        Assert.IsTrue(service.GetSoftwareForDevice(duet).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Visionary.VLite" && item.Purpose == DeviceSoftwarePurpose.Discovery));

        var e4200 = service.SearchDevices("E4200").Single();
        Assert.IsTrue(service.GetSoftwareForDevice(e4200).SelectMany(group => group.Software)
            .Any(item => item.Constraints.Contains("2.3.169", StringComparison.Ordinal)));

        var vpx = service.SearchDevices("VPX-TC1").Single(item => item.Hardware?.Id == "Aurora.VPXTC1");
        Assert.AreEqual("Aurora.VPXTC1", vpx.Hardware!.Id);
        Assert.IsTrue(service.GetSoftwareForDevice(vpx).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Aurora.IPBaseTManager"));

        var mxnet = service.SearchDevices("AC-MXNET-1G-EV2").Single(item => item.Hardware?.Id == "AVProEdge.MXnet1GEV2");
        Assert.AreEqual("AVProEdge.MXnet1GEV2", mxnet.Hardware!.Id);
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet, mxnet.LookupState);
        Assert.HasCount(0, service.GetSoftwareForDevice(mxnet));
        Assert.IsFalse(service.SearchProducts("Mentor").Any());

        var nhd = service.SearchDevices("NHD-500-TX").Single(item => item.Hardware?.Id == "WyreStorm.NHD500TX");
        Assert.IsTrue(service.GetSoftwareForDevice(nhd).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "WyreStorm.ManagementSuite" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        var nhd600 = service.SearchDevices("NHD-600-TX").Single(item => item.Hardware?.Id == "WyreStorm.NHD600TX");
        Assert.IsTrue(service.GetSoftwareForDevice(nhd600).SelectMany(group => group.Software)
            .Any(item => item.Purpose == DeviceSoftwarePurpose.Firmware));
        Assert.IsFalse(service.GetSoftwareForDevice(nhd600).SelectMany(group => group.Software)
            .Any(item => item.Purpose == DeviceSoftwarePurpose.Configuration));

        var maxColor = service.SearchDevices("MC-TX1").Single(item => item.Hardware?.Id == "JustAddPower.MCTX1");
        var maxColorSoftware = service.GetSoftwareForDevice(maxColor).SelectMany(group => group.Software).ToArray();
        Assert.IsTrue(maxColorSoftware.Any(item => item.ProductId.Value == "JustAddPower.AMP"));
        Assert.IsFalse(maxColorSoftware.Any(item => item.ProductId.Value == "JustAddPower.JADConfig"));
        Assert.AreEqual(Lifecycle.Legacy, service.SearchProducts("JADConfig").Single().Lifecycle);
    }

    [TestMethod]
    public void LightwareUbexExactModelsPreserveIdentityAndUseOnlyReviewedLdcAndLdu2Scopes()
    {
        var service = CreateQueryService();

        var f100 = service.SearchDevices("UBEX-PRO20-HDMI-F100").Single(item => item.Hardware?.Id == "Lightware.UBEXF100");
        Assert.AreEqual(HardwareDeviceCategory.AvOverIp, f100.Hardware!.Category);
        Assert.AreEqual("Lightware.UBEX", f100.DeviceFamilyId);
        var f100Software = service.GetSoftwareForDevice(f100).SelectMany(group => group.Software).ToArray();
        CollectionAssert.AreEquivalent(
            new[] { "Lightware.LDC", "Lightware.LDU2" },
            f100Software.Select(item => item.ProductId.Value).Distinct().ToArray());
        CollectionAssert.IsSubsetOf(
            new[] { DeviceSoftwarePurpose.Discovery, DeviceSoftwarePurpose.Configuration, DeviceSoftwarePurpose.Firmware },
            f100Software.Select(item => item.Purpose).ToArray());

        var f110 = service.SearchDevices("UBEX-PRO20-HDMI-F110").Single(item => item.Hardware?.Id == "Lightware.UBEXF110");
        var f111 = service.SearchDevices("UBEX-PRO20-HDMI-F111").Single(item => item.Hardware?.Id == "Lightware.UBEXF111");
        var f120 = service.SearchDevices("UBEX-PRO20-HDMI-F120").Single(item => item.Hardware?.Id == "Lightware.UBEXF120");
        var f121 = service.SearchDevices("UBEX-PRO20-HDMI-F121").Single(item => item.Hardware?.Id == "Lightware.UBEXF121");
        Assert.AreNotEqual(f110.Hardware!.Id, f111.Hardware!.Id);
        Assert.AreNotEqual(f120.Hardware!.Id, f121.Hardware!.Id);
        Assert.IsTrue(service.GetSoftwareForDevice(f110).SelectMany(group => group.Software)
            .Any(item => item.Constraints.Contains("F110 and F120 are transitional", StringComparison.OrdinalIgnoreCase)));

        var r100 = service.SearchDevices("UBEX-PRO20-HDMI-R100 2xSM-BiDi-DUO").Single(item => item.Hardware?.Id == "Lightware.UBEXR100SMBiDiDUO");
        Assert.AreEqual("UBEX-PRO20-HDMI-R100 2xSM-BiDi-DUO", r100.Hardware!.ExactModel);
        Assert.IsTrue(service.GetSoftwareForDevice(r100).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Lightware.LDU2"));

        var mmu = service.SearchDevices("UBEX-MMU-X200").Single(item => item.Hardware?.Id == "Lightware.UBEXMMUX200");
        var mmuSoftware = service.GetSoftwareForDevice(mmu).SelectMany(group => group.Software).ToArray();
        Assert.IsTrue(mmuSoftware.Any(item => item.ProductId.Value == "Lightware.LDC"));
        Assert.IsTrue(mmuSoftware.Any(item => item.ProductId.Value == "Lightware.LDU2"));
        Assert.IsTrue(mmuSoftware.Any(item => item.Constraints.Contains("does not transmit video", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(service.SearchDevices("Lightware device").Any(item => item.DeviceFamilyId == "Lightware.UBEX"));
        Assert.IsFalse(service.SearchDevices("VINX").Any(item => item.DeviceFamilyId == "Lightware.UBEX"));
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
        Assert.AreEqual("Crestron.DMNVX", service.SearchDevices("DM-NVX").Single(item => item.Hardware?.Id == "Crestron.DMNVX").DeviceFamilyId);
        Assert.IsTrue(service.GetSoftwareForDevice("Crestron CP4N").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Crestron.SIMPLWindows"));
        Assert.IsTrue(service.GetDevicesForProduct(new SoftwareProductId("Crestron.Toolbox"))
            .Any(item => item.ExactModelIds.Contains("CP4N")));
        Assert.HasCount(3, service.GetReleaseFamilies(new SoftwareProductId("QSYSDesigner")));
    }

    [TestMethod]
    public void CamerasAndConferencingBatchAUsesExactIdentityAndEvidenceScopedDesktopWorkflows()
    {
        var service = CreateQueryService();

        var aver = service.SearchDevices("CAM520 Pro2").Single(item => item.Hardware?.Id == "AVer.CAM520Pro2");
        Assert.AreEqual("CAM520 Pro2", aver.Hardware!.ExactModel);
        Assert.AreEqual("AVer.CollaborationCameras", aver.DeviceFamilyId);
        Assert.IsTrue(service.GetSoftwareForDevice(aver).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "AVer.RoomManagement" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        var ptzAppAlias = service.SearchDevices("PTZApp2").Single();
        CollectionAssert.AreEquivalent(new[] { "AVer.PTZApp2" }, service.GetSoftwareForDevice(ptzAppAlias)
            .SelectMany(group => group.Software).Select(item => item.ProductId.Value).Distinct().ToArray());
        Assert.IsFalse(service.GetSoftwareForDevice(ptzAppAlias).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "AVer.RoomManagement"));

        var rallyBar = service.SearchDevices("Rally Bar").Single(item => item.Hardware?.Id == "Logitech.RallyBar");
        var logitech = service.GetSoftwareForDevice(rallyBar).SelectMany(group => group.Software).ToArray();
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithVerifiedRelationships, rallyBar.LookupState);
        Assert.IsTrue(logitech.Any(item => item.ProductId.Value == "Logitech.Sync" && item.Purpose == DeviceSoftwarePurpose.Monitoring));
        Assert.IsFalse(logitech.Any(item => item.ProductId.Value == "Logitech.Tune"));
        var ptzPro = service.SearchDevices("PTZ Pro 2").Single(item => item.Hardware?.Id == "Logitech.PTZPro2");
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet, ptzPro.LookupState);
        Assert.HasCount(0, service.GetSoftwareForDevice(ptzPro));

        var studioX52 = service.SearchDevices("Poly Studio X52").Single(item => item.Hardware?.Id == "HPPoly.StudioX52");
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet, studioX52.LookupState);
        Assert.HasCount(0, service.GetSoftwareForDevice(studioX52));
        Assert.IsFalse(service.GetSoftwareForDevice(studioX52).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value is "HPPoly.LensDesktop" or "HPPoly.StudioDesktop"));

        var roomBar = service.SearchDevices("Room Bar").Single(item => item.Hardware?.Id == "Cisco.RoomBar");
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet, roomBar.LookupState);
        Assert.HasCount(0, service.GetSoftwareForDevice(roomBar));
        Assert.IsFalse(service.SearchProducts("RoomOS").Any());
        Assert.IsFalse(service.SearchProducts("Control Hub").Any());

        var nc12 = service.SearchDevices("NC-12x80").Single(item => item.Hardware?.Id == "QSYS.NC12x80");
        var ncSoftware = service.GetSoftwareForDevice(nc12).SelectMany(group => group.Software).ToArray();
        Assert.AreEqual(HardwareDeviceCategory.Camera, nc12.Hardware!.Category);
        Assert.IsTrue(ncSoftware.Any(item => item.ProductId.Value == "QSYSDesigner" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        Assert.IsTrue(ncSoftware.All(item => item.Constraints.Contains("camera", StringComparison.OrdinalIgnoreCase)));

        var huddly = service.SearchDevices("Huddly L1").Single(item => item.Hardware?.Id == "Huddly.L1");
        var huddlySoftware = service.GetSoftwareForDevice(huddly).SelectMany(group => group.Software).ToArray();
        Assert.IsTrue(huddlySoftware.Any(item => item.ProductId.Value == "Huddly.Connect" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        Assert.IsTrue(huddlySoftware.Any(item => item.ProductId.Value == "Huddly.Connect" && item.Purpose == DeviceSoftwarePurpose.Firmware));
        Assert.IsFalse(huddlySoftware.Any(item => item.ProductId.Value == "Huddly.DesktopApp"));
    }

    [TestMethod]
    public void CamerasAndConferencingBatchBPreservesExactCameraScopesAndDescriptiveOnlyWorkflows()
    {
        var service = CreateQueryService();

        var oneBeyond = service.SearchDevices("IV-CAM-I12-B").Single(item => item.Hardware?.Id == "Crestron.OneBeyondI12");
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet, oneBeyond.LookupState);
        Assert.HasCount(0, service.GetSoftwareForDevice(oneBeyond));

        AssertModelSoftware(service, "SRG-X400", "Sony.SRGX400", "Sony.RMIPSetupTool", DeviceSoftwarePurpose.Configuration);
        var sonyA40 = service.SearchDevices("SRG-A40").Single(item => item.Hardware?.Id == "Sony.SRGA40");
        Assert.HasCount(0, service.GetSoftwareForDevice(sonyA40));

        AssertModelSoftware(service, "AW-UE150A", "Panasonic.AWUE150A", "Panasonic.MediaProductionSuite", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "AW-UE50", "Panasonic.AWUE50", "Panasonic.EasyIPSetupToolPlus", DeviceSoftwarePurpose.Discovery);

        var lumens = service.SearchDevices("VC-TR40").Single(item => item.Hardware?.Id == "Lumens.VCTR40");
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet, lumens.LookupState);
        Assert.HasCount(0, service.GetSoftwareForDevice(lumens));

        AssertModelSoftware(service, "Move 4K 20X", "PTZOptics.Move4K20X", "PTZOptics.CameraManagementPlatform", DeviceSoftwarePurpose.Configuration);
        var ptzSoftware = service.GetSoftwareForDevice(service.SearchDevices("Link 4K").Single(item => item.Hardware?.Id == "PTZOptics.Link4K"))
            .SelectMany(group => group.Software).ToArray();
        Assert.IsTrue(ptzSoftware.Any(item => item.ProductId.Value == "PTZOptics.CameraManagementPlatform"));
        Assert.IsFalse(ptzSoftware.Any(item => item.ProductName.Contains("NDI", StringComparison.OrdinalIgnoreCase)));

        AssertModelSoftware(service, "PanaCast 50", "Jabra.PanaCast50", "Jabra.Direct", DeviceSoftwarePurpose.Configuration);
        Assert.IsFalse(service.GetSoftwareForDevice(service.SearchDevices("PanaCast 50").Single(item => item.Hardware?.Id == "Jabra.PanaCast50"))
            .SelectMany(group => group.Software).Any(item => item.ProductId.Value == "Jabra.Xpress"));

        AssertModelSoftware(service, "UVC86", "Yealink.UVC86", "Yealink.USBConnect", DeviceSoftwarePurpose.Configuration);
        var meetingBar = service.SearchDevices("MeetingBar A30").Single(item => item.Hardware?.Id == "Yealink.MeetingBarA30");
        Assert.HasCount(0, service.GetSoftwareForDevice(meetingBar));

        var neat = service.SearchDevices("Neat Board 50").Single(item => item.Hardware?.Id == "Neat.Board50");
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet, neat.LookupState);
        Assert.HasCount(0, service.GetSoftwareForDevice(neat));
        Assert.IsFalse(service.SearchProducts("Neat Pulse").Any());
        Assert.IsFalse(service.SearchProducts("RoomOS").Any());

        foreach (var (search, id) in new[]
        {
            ("IV-CAM-I12-B", "Crestron.OneBeyondI12"),
            ("IV-CAM-P12-B", "Crestron.OneBeyondP12"),
            ("IV-CAMA3-20", "Crestron.OneBeyondAutoTracker3"),
            ("SRG-A40", "Sony.SRGA40"),
            ("BRC-X1000", "Sony.BRCX1000"),
            ("AW-UE100", "Panasonic.AWUE100"),
            ("VC-A61P", "Lumens.VCA61P"),
            ("VC-A71P", "Lumens.VCA71P"),
            ("UVC40", "Yealink.UVC40"),
            ("MeetingBar A40", "Yealink.MeetingBarA40"),
            ("Neat Bar", "Neat.Bar"),
            ("Neat Center", "Neat.Center")
        })
            Assert.AreEqual(id, service.SearchDevices(search).Single(item => item.Hardware?.Id == id).Hardware!.Id);
    }

    [TestMethod]
    public void DisplayAndProjectorFirstPassUsesExactModelsAndEvidenceScopedDesktopWorkflows()
    {
        var service = CreateQueryService();

        var barco = service.SearchDevices("UDX4K22").Single(item => item.Hardware?.Id == "Barco.UDX4K22");
        Assert.AreEqual(HardwareDeviceCategory.Display, barco.Hardware!.Category);
        Assert.AreEqual(CompatibilitySearchMatchKind.NormalizedExact, barco.MatchKind);
        var barcoSoftware = service.GetSoftwareForDevice(barco).SelectMany(group => group.Software).ToArray();
        Assert.IsTrue(barcoSoftware.Any(item => item.ProductId.Value == "Barco.ProjectorToolset" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        Assert.IsTrue(barcoSoftware.Any(item => item.ProductId.Value == "Barco.ProjectorToolset" && item.Purpose == DeviceSoftwarePurpose.Diagnostics));

        var christie = service.SearchDevices("Griffyn 4K35-RGB").Single(item => item.Hardware?.Id == "Christie.Griffyn4K35RGB");
        var christieSoftware = service.GetSoftwareForDevice(christie).SelectMany(group => group.Software).ToArray();
        Assert.IsTrue(christieSoftware.Any(item => item.ProductId.Value == "Christie.Mystique" && item.Purpose == DeviceSoftwarePurpose.Commissioning));
        Assert.IsTrue(christieSoftware.Any(item => item.ProductId.Value == "Christie.Conductor" && item.Purpose == DeviceSoftwarePurpose.Monitoring));

        AssertModelSoftware(service, "Pro L12000Q", "Epson.ProL12000Q", "Epson.ProjectorProfessionalTool", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "PT RQ50K", "Panasonic.PTRQ50K", "Panasonic.GeometryManagerPro", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "PN ME552", "SharpNEC.PNME552", "SharpNEC.NaViSetAdministrator2", DeviceSoftwarePurpose.Configuration);

        Assert.IsFalse(service.SearchDevices("UDM-4K23").Any(item => item.Hardware?.Id == "Barco.UDM4K22"));
        Assert.AreEqual(CompatibilitySearchOutcome.NoVerifiedRelationshipInCurrentCatalog, service.GetSearchOutcome("UDM-4K23"));
    }

    [TestMethod]
    public void DeviceLookupPreservesMatchedCanonicalRelationshipScopeAndRanksExactMatches()
    {
        var service = CreateQueryService();

        var cp4n = service.SearchDevices("CP 4N").Single();
        Assert.AreEqual("Crestron.4Series", cp4n.DeviceFamilyId);
        Assert.AreEqual(CompatibilitySearchMatchKind.NormalizedExact, cp4n.MatchKind);
        CollectionAssert.AreEquivalent(
            new[] { "Crestron.SIMPLWindows", "Crestron.Database", "Crestron.DeviceDatabase", "Crestron.Toolbox" },
            service.GetSoftwareForDevice(cp4n).SelectMany(group => group.Software).Select(item => item.ProductId.Value).Distinct().ToArray());

        var ptzApp = service.SearchDevices("PTZApp2").Single();
        Assert.AreEqual("AVer.CollaborationCameras", ptzApp.DeviceFamilyId);
        Assert.AreEqual(CompatibilitySearchMatchKind.ExactModelOrAlias, ptzApp.MatchKind);
        CollectionAssert.AreEquivalent(new[] { "AVer.PTZApp2" },
            service.GetSoftwareForDevice(ptzApp).SelectMany(group => group.Software).Select(item => item.ProductId.Value).Distinct().ToArray());
        Assert.IsFalse(service.GetSoftwareForDevice(ptzApp).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "AVer.RoomManagement"));

        Assert.AreEqual(CompatibilitySearchMatchKind.PrefixOrToken, service.SearchDevices("CP4").Single().MatchKind);
        var rmc4 = service.SearchDevices("RMC4").First();
        Assert.AreEqual("Crestron.RMC4", rmc4.Hardware!.Id);
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithVerifiedRelationships, rmc4.LookupState);
        Assert.IsTrue(service.GetSoftwareForDevice(rmc4).SelectMany(group => group.Software)
            .Any(software => software.ProductId.Value == "Crestron.Toolbox"));
        Assert.AreEqual("Crestron.DMNVX363", service.SearchDevices("DM-NVX-363").Single(item => item.Hardware?.Id == "Crestron.DMNVX363").Hardware!.Id);
    }

    [TestMethod]
    public void DeviceLookupNormalizesSeparatorsWithoutFabricatingModelCompatibility()
    {
        var service = CreateQueryService();

        Assert.AreEqual("Crestron.DMNVX", service.SearchDevices("DM NVX").Single(item => item.Hardware?.Id == "Crestron.DMNVX").DeviceFamilyId);
        Assert.AreEqual("Crestron.DMNVX", service.SearchDevices("DM-NVX").Single(item => item.Hardware?.Id == "Crestron.DMNVX").DeviceFamilyId);
        Assert.AreEqual(CompatibilitySearchOutcome.NoDeviceOrCatalogMatch, service.GetSearchOutcome("not-a-device"));
    }

    [TestMethod]
    public void HardwareIdentityCatalogMakesCoverageStatesAndRelationAuthorityExplicit()
    {
        var root = RepositoryRoot();
        var hardware = new RepositoryHardwareIdentityCatalogLoader().Load(root);
        var service = CreateQueryService();

        Assert.IsGreaterThan(0, hardware.Families.Count);
        Assert.IsGreaterThan(0, hardware.Models.Count);

        // GetCoverageSummary is derived logic the parser does not validate, so assert it against the
        // catalog it summarizes rather than against frozen totals that change with every new model.
        var coverage = hardware.GetCoverageSummary();
        Assert.AreEqual(hardware.Families.Count, coverage.Families);
        Assert.AreEqual(hardware.Models.Count, coverage.Models);
        Assert.AreEqual(
            hardware.Families.Sum(family => family.Aliases.Count) + hardware.Models.Sum(model => model.Aliases.Count),
            coverage.Aliases);
        Assert.AreEqual(hardware.Models.Count(model => model.CoverageState == HardwareCoverageState.VerifiedSoftwareRelationships), coverage.VerifiedModels);
        Assert.AreEqual(hardware.Models.Count(model => model.CoverageState == HardwareCoverageState.Unresolved), coverage.UnresolvedModels);
        // FamilyOnlyCoverage counts families, not models, so the model states partition the model set.
        Assert.AreEqual(hardware.Families.Count(family => family.CoverageState == HardwareCoverageState.FamilyOnly), coverage.FamilyOnlyCoverage);
        Assert.AreEqual(
            coverage.Models,
            coverage.VerifiedModels + coverage.UnresolvedModels
                + hardware.Models.Count(model => model.CoverageState == HardwareCoverageState.FamilyOnly));
        // Category rollups must account for every family and model exactly once.
        Assert.AreEqual(coverage.Families, coverage.Categories.Sum(category => category.Families));
        Assert.AreEqual(coverage.Models, coverage.Categories.Sum(category => category.Models));

        var cp4n = service.SearchDevices("Crestron CP4N").Single();
        Assert.AreEqual("Crestron.CP4N", cp4n.Hardware!.Id);
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithVerifiedRelationships, cp4n.LookupState);
        Assert.IsTrue(service.GetSoftwareForDevice(cp4n).SelectMany(group => group.Software)
            .Any(software => software.ProductId.Value == "Crestron.SIMPLWindows"));

        var core110 = service.SearchDevices("Core 110f").Single();
        Assert.AreEqual("QSYS.Core110f", core110.Hardware!.Id);
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithVerifiedRelationships, core110.LookupState);
        Assert.IsTrue(service.GetSoftwareForDevice(core110).SelectMany(group => group.Software)
            .Any(software => software.ProductId.Value == "QSYSDesigner"));
        Assert.AreEqual(CompatibilitySearchOutcome.ExactVerifiedRelationship, service.GetSearchOutcome("Core 110f"));

        var coreFamily = service.SearchDevices("Q-SYS Core").Single(result => result.Hardware?.Id == "QSYS.Core");
        Assert.AreEqual(HardwareLookupState.KnownFamilyWithVerifiedRelationships, coreFamily.LookupState);
        Assert.IsTrue(service.GetSoftwareForDevice(coreFamily).SelectMany(group => group.Software)
            .Any(software => software.ProductId.Value == "QSYSDesigner"));
        Assert.AreEqual(HardwareDeviceCategory.AudioDsp, core110.Hardware.Category);
    }

    [TestMethod]
    public void ExtronMediaPortAndStreamingProcessorsKeepExactHardwareAndDesktopWorkflowScopesSeparate()
    {
        var service = CreateQueryService();

        var mediaPort200 = service.SearchDevices("MediaPort 200").Single(item => item.Hardware?.Id == "Extron.MediaPort200");
        var mediaPort300 = service.SearchDevices("MediaPort300").Single(item => item.Hardware?.Id == "Extron.MediaPort300");
        Assert.AreEqual(HardwareDeviceCategory.AvInterface, mediaPort200.Hardware!.Category);
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithVerifiedRelationships, mediaPort200.LookupState);
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithVerifiedRelationships, mediaPort300.LookupState);
        Assert.IsTrue(service.GetSoftwareForDevice(mediaPort200).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Extron.PCS" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        Assert.AreEqual(Lifecycle.Legacy, mediaPort200.Hardware.Lifecycle);
        Assert.IsTrue(service.GetSoftwareForDevice(mediaPort300).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Extron.PCS" && item.Purpose == DeviceSoftwarePurpose.Commissioning));

        foreach (var model in new[] { "SMP 111", "SMP351", "SMP352", "Extron SMP 401" })
        {
            var result = service.SearchDevices(model).Single(item => item.DeviceFamilyId == "Extron.StreamingMediaProcessors");
            Assert.AreEqual(HardwareDeviceCategory.RecordingAppliance, result.Hardware!.Category);
            Assert.AreEqual(HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet, result.LookupState);
            Assert.HasCount(0, service.GetSoftwareForDevice(result));
        }
    }

    [TestMethod]
    public void DigitalSignageAndAvNetworkHardwareUseExactVendorScopedDesktopRelationships()
    {
        var service = CreateQueryService();

        var brightSign = service.SearchDevices("BrightSign XT245").Single(item => item.Hardware?.Id == "BrightSign.XT245");
        Assert.AreEqual(HardwareDeviceCategory.DigitalSignage, brightSign.Hardware!.Category);
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithVerifiedRelationships, brightSign.LookupState);
        Assert.IsTrue(service.GetSoftwareForDevice(brightSign).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "BrightSign.BrightAuthorConnected" && item.Purpose == DeviceSoftwarePurpose.Configuration));

        var luminex = service.SearchDevices("GigaCore10t").Single(item => item.Hardware?.Id == "Luminex.GigaCore10t");
        Assert.AreEqual(HardwareDeviceCategory.NetworkInfrastructure, luminex.Hardware!.Category);
        var luminexSoftware = service.GetSoftwareForDevice(luminex).SelectMany(group => group.Software).ToArray();
        CollectionAssert.AreEquivalent(
            new[] { DeviceSoftwarePurpose.Configuration, DeviceSoftwarePurpose.Monitoring, DeviceSoftwarePurpose.Firmware },
            luminexSoftware.Where(item => item.ProductId.Value == "Luminex.Araneo").Select(item => item.Purpose).ToArray());

        var netgear = service.SearchDevices("GSM4212UX").Single(item => item.Hardware?.Id == "NETGEAR.GSM4212UX");
        Assert.AreEqual(HardwareDeviceCategory.NetworkInfrastructure, netgear.Hardware!.Category);
        var netgearSoftware = service.GetSoftwareForDevice(netgear).SelectMany(group => group.Software).ToArray();
        CollectionAssert.AreEquivalent(
            new[] { DeviceSoftwarePurpose.Configuration, DeviceSoftwarePurpose.Discovery, DeviceSoftwarePurpose.Monitoring, DeviceSoftwarePurpose.Firmware },
            netgearSoftware.Where(item => item.ProductId.Value == "NETGEAR.EngageController").Select(item => item.Purpose).ToArray());

        Assert.IsFalse(netgearSoftware.Any(item => item.ProductId.Value == "Luminex.Araneo"));
    }

    [TestMethod]
    public void IntercomMatricesResolveOnlyToTheirVendorDocumentedConfigurationTools()
    {
        var service = CreateQueryService();

        var eclipse = service.SearchDevices("Eclipse HX Delta").Single(item => item.Hardware?.Id == "ClearCom.EclipseHXDelta");
        Assert.AreEqual(HardwareDeviceCategory.Intercom, eclipse.Hardware!.Category);
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithVerifiedRelationships, eclipse.LookupState);
        var eclipseSoftware = service.GetSoftwareForDevice(eclipse).SelectMany(group => group.Software).Select(item => item.ProductId.Value).ToArray();
        CollectionAssert.IsSubsetOf(new[] { "ClearCom.EHX", "ClearCom.DynamEC" }, eclipseSoftware);

        var freeSpeak = service.SearchDevices("FreeSpeak 2 Base II").Single(item => item.Hardware?.Id == "ClearCom.FreeSpeakIIBaseII");
        Assert.IsTrue(service.GetSoftwareForDevice(freeSpeak).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "ClearCom.FreeSpeakIIConfigurationEditor" && item.Purpose == DeviceSoftwarePurpose.Configuration));

        var odin = service.SearchDevices("RTS ODIN").Single(item => item.Hardware?.Id == "RTS.ODIN");
        Assert.IsTrue(service.GetSoftwareForDevice(odin).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "RTS.NEOIntercomManagementSuite" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        Assert.IsFalse(service.GetSoftwareForDevice(odin).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value.StartsWith("ClearCom.", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ExactShortModelAliasAndControlInterfaceScopesRemainEvidenceBound()
    {
        var service = CreateQueryService();

        var core110 = service.SearchDevices("110F").First();
        Assert.AreEqual("QSYS.Core110f", core110.Hardware!.Id);
        Assert.AreEqual("Q-SYS", core110.Hardware.Manufacturer);
        Assert.AreEqual("Core 110f", core110.Hardware.ExactModel);
        Assert.AreEqual(CompatibilitySearchMatchKind.ExactModelOrAlias, core110.MatchKind);
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithVerifiedRelationships, core110.LookupState);
        Assert.IsTrue(service.GetSoftwareForDevice(core110).SelectMany(group => group.Software)
            .Any(software => software.ProductId.Value == "QSYSDesigner" && software.Constraints.Contains("2 GB", StringComparison.OrdinalIgnoreCase)));

        var rdl = service.SearchDevices("D BTN21").Single(item => item.Hardware?.Id == "RDL.DBTN21");
        Assert.AreEqual(HardwareDeviceCategory.AvInterface, rdl.Hardware!.Category);
        AssertModelSoftware(service, "D-BTN21", "RDL.DBTN21", "RDL.Console", DeviceSoftwarePurpose.Configuration);
        var rdlDd = service.SearchDevices("DD-BTN44").Single(item => item.Hardware?.Id == "RDL.DDBTN44");
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithVerifiedRelationships, rdlDd.LookupState);
        AssertModelSoftware(service, "DDS-BTN44", "RDL.DDSBTN44", "RDL.Console", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "DD-RN31", "RDL.DDRN31", "RDL.Console", DeviceSoftwarePurpose.Configuration);

        var nbp = service.SearchDevices("NBP1200C").Single(item => item.Hardware?.Id == "Extron.NBP1200C");
        Assert.AreEqual(HardwareDeviceCategory.ControlPanel, nbp.Hardware!.Category);
        var nbpSoftware = service.GetSoftwareForDevice(nbp).SelectMany(group => group.Software).ToArray();
        Assert.IsTrue(nbpSoftware.Any(item => item.ProductId.Value == "Extron.Toolbelt" && item.Purpose == DeviceSoftwarePurpose.Discovery));
        Assert.IsTrue(nbpSoftware.Any(item => item.ProductId.Value == "Extron.Toolbelt" && item.Purpose == DeviceSoftwarePurpose.Firmware));
        Assert.IsTrue(nbpSoftware.Any(item => item.ProductId.Value == "Extron.GlobalConfiguratorPlus" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        Assert.IsTrue(nbpSoftware.Any(item => item.ProductId.Value == "Extron.GlobalScripter" && item.Purpose == DeviceSoftwarePurpose.Programming));

        var kramer = service.SearchDevices("SL 240C").Single(item => item.Hardware?.Id == "Kramer.SL240C");
        Assert.AreEqual(Lifecycle.Legacy, kramer.Hardware!.Lifecycle);
        AssertModelSoftware(service, "SL-240C", "Kramer.SL240C", "Kramer.KUpload", DeviceSoftwarePurpose.Firmware);
        AssertModelSoftware(service, "RC-74DL", "Kramer.RC74DL", "Kramer.KConfig", DeviceSoftwarePurpose.Programming);
        var rackLink = service.SearchDevices("RLNK 910R").Single(item => item.Hardware?.Id == "MiddleAtlantic.RLNK910R");
        Assert.AreEqual(HardwareDeviceCategory.PowerDistribution, rackLink.Hardware!.Category);
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet, rackLink.LookupState);
        Assert.HasCount(0, service.GetSoftwareForDevice(rackLink));
    }

    [TestMethod]
    public void SignalDistributionBatchAUsesExactEvidenceScopesAndKeepsNonDesktopHardwareExplicit()
    {
        var service = CreateQueryService();

        AssertModelSoftware(service, "DTP2 R 211", "Extron.DTP2R211", "Extron.FirmwareLoader", DeviceSoftwarePurpose.Firmware);
        AssertModelSoftware(service, "IN1808", "Extron.IN1808", "Extron.PCS", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "XTP II CrossPoint 1600", "Extron.XTPIICrossPoint1600", "Extron.XTPSystemConfiguration", DeviceSoftwarePurpose.Commissioning);
        AssertModelSoftware(service, "UCX 2x1 HC40", "Lightware.UCX2x1HC40", "Lightware.LDC", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "UCX 2x1 HC40", "Lightware.UCX2x1HC40", "Lightware.LDU2", DeviceSoftwarePurpose.Firmware);
        AssertModelSoftware(service, "VS 88H2", "Kramer.VS88H2", "Kramer.Network", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "AT OME MS42 HDBT", "Atlona.OMEMS42HDBT", "Atlona.VelocityDeviceManager", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "KD MS8x8G", "KeyDigital.MS8x8G", "KeyDigital.KDMSPro", DeviceSoftwarePurpose.Configuration);

        foreach (var (query, id, manufacturer, lifecycle) in new[]
        {
            ("DM MD8X8", "Crestron.DMMD8X8", "Crestron", Lifecycle.Legacy),
            ("KD PS42", "KeyDigital.PS42", "Key Digital", Lifecycle.Current),
            ("UHBX SW3 WP", "Hall.UHBXSW3WP", "Hall Technologies", Lifecycle.Current),
            ("EXT UHD600A 44", "Gefen.UHD600A44", "Gefen", Lifecycle.Legacy),
            ("DIGI HD60C S", "Intelix.DIGIHD60CS", "Liberty / Intelix", Lifecycle.Legacy),
            ("MuxLab 500451", "MuxLab.500451", "MuxLab", Lifecycle.Current)
        })
        {
            var result = service.SearchDevices(query).Single(item => item.Hardware?.Id == id);
            Assert.AreEqual(manufacturer, result.Hardware!.Manufacturer);
            Assert.AreEqual(HardwareDeviceCategory.SignalDistribution, result.Hardware.Category);
            Assert.AreEqual(lifecycle, result.Hardware.Lifecycle);
            Assert.AreEqual(HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet, result.LookupState);
            Assert.HasCount(0, service.GetSoftwareForDevice(result));
        }

        var opus = service.SearchDevices("AT OPUS 46M").Single(item => item.Hardware?.Id == "Atlona.OPUS46M");
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet, opus.LookupState);
        Assert.HasCount(0, service.GetSoftwareForDevice(opus));

        var crestron = service.SearchDevices("DM MD8X8").Single(item => item.Hardware?.Id == "Crestron.DMMD8X8");
        Assert.IsFalse(service.GetSoftwareForDevice(crestron).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value is "Crestron.DMNVXTool" or "Crestron.SIMPLWindows"));
    }

    [TestMethod]
    public void HolisticWave1InstalledMicrophonesAndWirelessUseEvidenceScopedDesktopTools()
    {
        var service = CreateQueryService();

        AssertModelSoftware(service, "MXA920", "Shure.MXA920", "Shure.Designer", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "ANIUSB MATRIX", "Shure.ANIUSBMatrix", "Shure.UpdateUtility", DeviceSoftwarePurpose.Firmware);
        AssertModelSoftware(service, "ULXD4D", "Shure.ULXD4D", "Shure.WirelessWorkbench", DeviceSoftwarePurpose.Monitoring);
        AssertModelSoftware(service, "TCC2", "Sennheiser.TCC2", "Sennheiser.ControlCockpit", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "TeamConnect Ceiling Medium", "Sennheiser.TCCM", "Sennheiser.ControlCockpit", DeviceSoftwarePurpose.Monitoring);
        AssertModelSoftware(service, "ATND 1061 DAN", "AudioTechnica.ATND1061DAN", "AudioTechnica.DigitalMicrophoneManager", DeviceSoftwarePurpose.Commissioning);

        var ulxd = service.SearchDevices("ULXD4D").Single(item => item.Hardware?.Id == "Shure.ULXD4D");
        Assert.IsFalse(service.GetSoftwareForDevice(ulxd).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value is "Shure.Designer" or "AudioTechnica.WirelessManager"));
    }

    [TestMethod]
    public void HolisticWave2WirelessPresentationSeparatesClientsCloudAndManagementTools()
    {
        var service = CreateQueryService();

        AssertModelSoftware(service, "Solstice Pod Gen3", "Mersive.SolsticePodGen3", "Mersive.SolsticeDashboard", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "ShareLink 1100", "Extron.ShareLinkPro1100", "Extron.PCS", DeviceSoftwarePurpose.Firmware);
        AssertModelSoftware(service, "SBWD1100P", "ScreenBeam.1100Plus", "ScreenBeam.CMSEnterprise", DeviceSoftwarePurpose.Monitoring);

        foreach (var (query, id) in new[]
        {
            ("ClickShare CX50 Gen2", "Barco.CX50Gen2"),
            ("AM3200WF", "Crestron.AM3200WF"),
            ("VIA Connect 2", "Kramer.VIAConnect2")
        })
        {
            var result = service.SearchDevices(query).Single(item => item.Hardware?.Id == id);
            Assert.AreEqual(HardwareDeviceCategory.WirelessPresentation, result.Hardware!.Category);
            Assert.AreEqual(HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet, result.LookupState);
            Assert.HasCount(0, service.GetSoftwareForDevice(result));
        }

        var clickShare = service.SearchDevices("CX-20").Single(item => item.Hardware?.Id == "Barco.CX20");
        Assert.IsFalse(service.GetSoftwareForDevice(clickShare).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Barco.ClickShareConfigurator"));
    }

    [TestMethod]
    public void HolisticWave3AmplifiersUseOnlyTheirReviewedControlApplications()
    {
        var service = CreateQueryService();

        AssertModelSoftware(service, "UNICA 8K8", "Powersoft.Unica8K8", "Powersoft.ArmoniaPlus", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "Connect 704", "LEAProfessional.Connect704", "LEAProfessional.SharkWare", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "CX Q 4K4", "QSYS.CXQ4K4", "QSYSDesigner", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "d&b 40D", "dbaudio.40D", "dbaudio.R1", DeviceSoftwarePurpose.Monitoring);
        AssertModelSoftware(service, "LA12X", "LAcoustics.LA12X", "LAcoustics.LANetworkManager", DeviceSoftwarePurpose.Monitoring);

        var cxq = service.SearchDevices("CX-Q 8K8").Single(item => item.Hardware?.Id == "QSYS.CXQ8K8");
        Assert.AreEqual(Lifecycle.Legacy, cxq.Hardware!.Lifecycle);
        Assert.IsFalse(service.GetSoftwareForDevice(cxq).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value is "Powersoft.ArmoniaPlus" or "dbaudio.R1"));
    }

    [TestMethod]
    public void HolisticWave4RecordingAppliancesSeparateDesktopAndBrowserWorkflows()
    {
        var service = CreateQueryService();

        AssertModelSoftware(service, "HyperDeck Studio HD Mini", "Blackmagic.HyperDeckStudioHDMini", "Blackmagic.HyperDeckSetup", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "AJA HELO Plus", "AJA.HELOPlus", "AJA.EMiniSetup", DeviceSoftwarePurpose.Configuration);

        foreach (var (query, id) in new[] { ("Pearl-2", "Epiphan.Pearl2"), ("Ultra Encode AIO", "Magewell.UltraEncodeAIO") })
        {
            var result = service.SearchDevices(query).Single(item => item.Hardware?.Id == id);
            Assert.AreEqual(HardwareDeviceCategory.RecordingAppliance, result.Hardware!.Category);
            Assert.AreEqual(HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet, result.LookupState);
            Assert.HasCount(0, service.GetSoftwareForDevice(result));
        }
    }

    [TestMethod]
    public void HolisticWave5AssistiveListeningDoesNotInferDanteOrBrowserSoftware()
    {
        var service = CreateQueryService();

        AssertModelSoftware(service, "LA-490", "ListenTechnologies.LA490", "ListenTechnologies.ListenWIFIManager", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "ADP USB AU 2X2", "Audinate.DanteAVIOUSB", "Audinate.DanteController", DeviceSoftwarePurpose.Configuration);

        foreach (var (query, id) in new[] { ("LW-100P", "ListenTechnologies.LW100P"), ("WaveCAST C", "WilliamsAV.WaveCASTC"), ("FM T55", "WilliamsAV.FMT55") })
        {
            var result = service.SearchDevices(query).Single(item => item.Hardware?.Id == id);
            Assert.AreEqual(HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet, result.LookupState);
            Assert.HasCount(0, service.GetSoftwareForDevice(result));
        }
    }

    [TestMethod]
    public void HolisticWave6UtilityHardwarePreservesEmbeddedAndUnresolvedWorkflows()
    {
        var service = CreateQueryService();

        AssertModelSoftware(service, "CEN IO COM 102", "Crestron.CENIOCOM102", "Crestron.Toolbox", DeviceSoftwarePurpose.Discovery);

        foreach (var (query, id) in new[] { ("WB 800 IPVM 12", "WattBox.WB800IPVM12"), ("SX-1120-RT", "SurgeX.SX1120RT"), ("RLNK 415R IEC", "MiddleAtlantic.RLNK415RIEC") })
        {
            var result = service.SearchDevices(query).Single(item => item.Hardware?.Id == id);
            Assert.AreEqual(HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet, result.LookupState);
            Assert.HasCount(0, service.GetSoftwareForDevice(result));
        }
    }

    [TestMethod]
    public void HolisticWave7VideoProcessorsKeepBrowserDesktopAndUnknownScopesDistinct()
    {
        var service = CreateQueryService();

        AssertModelSoftware(service, "Aquilon RS alpha", "AnalogWay.AquilonRSAlpha", "AnalogWay.LivePremierWebRCS", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "CM2 547", "tvONE.CORIOmaster2", "tvONE.CORIOgrapher", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "VSN1172", "Datapath.VSN1172", "Datapath.WallControl10", DeviceSoftwarePurpose.Configuration);

        foreach (var (query, id) in new[] { ("GAL16", "RGBSpectrum.GalileoGAL16"), ("VuWall PAK 40", "VuWall.PAK40") })
        {
            var result = service.SearchDevices(query).Single(item => item.Hardware?.Id == id);
            Assert.AreEqual(HardwareDeviceCategory.VideoProcessor, result.Hardware!.Category);
            Assert.AreEqual(HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet, result.LookupState);
            Assert.HasCount(0, service.GetSoftwareForDevice(result));
        }
    }

    [TestMethod]
    public void HolisticFrozenPriorityOneDenominatorIsComplete()
    {
        var service = CreateQueryService();
        string[] exactModels =
        [
            "MXA920", "MXA902", "MXA710-2FT", "ANIUSB-MATRIX", "ULXD4D", "TeamConnect Ceiling 2", "TeamConnect Ceiling Medium", "ATND1061DAN",
            "CX-20", "CX-30", "CX-50 Gen2", "C-10", "Solstice Pod Gen3", "AM-3200-WF", "AM-3100-WF", "ShareLink Pro 1100", "VIA Connect2", "ScreenBeam 1100 Plus",
            "UNICA 8K8", "MEZZO 604 A", "T604 A", "X8", "Connect 354", "Connect 704", "CX-Q 4K4", "CX-Q 8K8", "40D", "D80", "LA12X",
            "HyperDeck Studio HD Mini", "HyperDeck Studio HD Plus", "HyperDeck Studio 4K Pro", "Pearl Mini", "Pearl-2", "Ultra Encode AIO", "HELO Plus",
            "LW-100P", "LA-490", "WaveCAST C", "FM T55", "Dante AVIO USB Adapter",
            "CEN-IO-COM-102", "CEN-IO-RY-104", "CEN-IO-DIGIN-204", "WB-800-IPVM-12", "SX-1120-RT", "RLNK-415R-IEC",
            "Aquilon RS alpha", "Aquilon C+", "CORIOmaster2", "Galileo GAL16", "PAK 40", "VSN1172"
        ];

        Assert.HasCount(53, exactModels);
        foreach (string model in exactModels)
        {
            Assert.IsTrue(service.SearchDevices(model).Any(result =>
                result.Hardware is not null && string.Equals(result.Hardware.ExactModel, model, StringComparison.OrdinalIgnoreCase)), model);
        }
    }

    [TestMethod]
    public void FinalDeviceLookupAcceptanceCoversEveryRepresentedCategoryAndRequiredHistoricalSearch()
    {
        var service = CreateQueryService();
        var hardware = new RepositoryHardwareIdentityCatalogLoader().Load(RepositoryRoot());
        var categorySamples = new (HardwareDeviceCategory Category, string Query, string ModelId)[]
        {
            (HardwareDeviceCategory.ControlProcessor, "CP4N", "Crestron.CP4N"),
            (HardwareDeviceCategory.AudioDsp, "Core 110f", "QSYS.Core110f"),
            (HardwareDeviceCategory.AvOverIp, "DM NVX 363", "Crestron.DMNVX363"),
            (HardwareDeviceCategory.Camera, "CAM520 Pro2", "AVer.CAM520Pro2"),
            (HardwareDeviceCategory.Display, "QM 65C", "Samsung.QM65C"),
            (HardwareDeviceCategory.ControlPanel, "NBP1200C", "Extron.NBP1200C"),
            (HardwareDeviceCategory.AvInterface, "DD BTN44", "RDL.DDBTN44"),
            (HardwareDeviceCategory.PowerDistribution, "RLNK 910R", "MiddleAtlantic.RLNK910R"),
            (HardwareDeviceCategory.SignalDistribution, "DTP2 T 211", "Extron.DTP2T211"),
            (HardwareDeviceCategory.InstalledMicrophone, "Shure MXA920", "Shure.MXA920"),
            (HardwareDeviceCategory.WirelessPresentation, "CX 50 Gen2", "Barco.CX50Gen2"),
            (HardwareDeviceCategory.Amplifier, "UNICA 8K8", "Powersoft.Unica8K8"),
            (HardwareDeviceCategory.RecordingAppliance, "HyperDeck Studio HD Mini", "Blackmagic.HyperDeckStudioHDMini"),
            (HardwareDeviceCategory.AssistiveListening, "LA 490", "ListenTechnologies.LA490"),
            (HardwareDeviceCategory.VideoProcessor, "Aquilon RS alpha", "AnalogWay.AquilonRSAlpha"),
            (HardwareDeviceCategory.Wireless, "ULX D quad receiver", "Shure.ULXD4Q"),
            (HardwareDeviceCategory.DigitalSignage, "BrightSign LS425", "BrightSign.LS425"),
            (HardwareDeviceCategory.NetworkInfrastructure, "GSM4212P", "NETGEAR.GSM4212P"),
            (HardwareDeviceCategory.Intercom, "Eclipse HX Delta", "ClearCom.EclipseHXDelta")
        };

        CollectionAssert.AreEquivalent(
            hardware.Families.Select(item => item.Category).Distinct().ToArray(),
            categorySamples.Select(item => item.Category).Distinct().ToArray());
        foreach (var sample in categorySamples)
        {
            var result = service.SearchDevices(sample.Query).Single(item => item.Hardware?.Id == sample.ModelId);
            Assert.AreEqual(sample.Category, result.Hardware!.Category, sample.Query);
        }

        foreach (var (query, modelId) in new (string Query, string ModelId)[]
        {
            ("RMC4", "Crestron.RMC4"), ("PRO4", "Crestron.PRO4"), ("110F", "QSYS.Core110f"),
            ("BLU-100", "BSS.BLU100"), ("Room Bar", "Cisco.RoomBar"), ("ULXD4Q", "Shure.ULXD4Q"),
            ("TeamConnect Ceiling Medium", "Sennheiser.TCCM"), ("CX-50 Gen2", "Barco.CX50Gen2")
        })
            Assert.IsTrue(service.SearchDevices(query).Any(item => item.Hardware?.Id == modelId), query);

        Assert.IsTrue(service.SearchDevices("TesiraFORTÉ").Any(item => item.Hardware?.Family == "Biamp Tesira Processors"));
        Assert.IsTrue(service.SearchDevices("HyperDeck Studio").Any(item => item.Hardware?.Family == "Blackmagic HyperDeck Studio Recorders"));
        Assert.AreEqual("Crestron.DMNVX363", service.SearchDevices("DM-NVX-363").First().Hardware!.Id);
        Assert.AreEqual("Cisco.RoomBar", service.SearchDevices("Room Bar").First().Hardware!.Id);
    }

    [TestMethod]
    public void FinalDeviceLookupAcceptanceKeepsCoverageAndSoftwareAuthorityConsistent()
    {
        var service = CreateQueryService();
        var hardware = new RepositoryHardwareIdentityCatalogLoader().Load(RepositoryRoot());

        foreach (var model in hardware.Models)
        {
            var result = service.SearchDevices(model.Name).Single(item => item.Hardware?.Id == model.Id.Value);
            var software = service.GetSoftwareForDevice(result).SelectMany(group => group.Software).ToArray();
            if (model.CoverageState == HardwareCoverageState.VerifiedSoftwareRelationships)
                Assert.IsGreaterThan(0, software.Length, model.Id.Value);
            else
                Assert.HasCount(0, software, model.Id.Value);
        }

        var qm65c = service.SearchDevices("Samsung QM65C").Single(item => item.Hardware?.Id == "Samsung.QM65C");
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet, qm65c.LookupState);
        Assert.HasCount(0, service.GetSoftwareForDevice(qm65c));
        AssertModelSoftware(service, "ULXD4Q", "Shure.ULXD4Q", "Shure.WirelessWorkbench", DeviceSoftwarePurpose.Configuration);
        Assert.IsFalse(service.GetSoftwareForDevice(qm65c).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value.StartsWith("Samsung.", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ControlProcessorCoveragePreservesExactModelsAndSeparateGenerations()
    {
        var service = CreateQueryService();

        var nx4200 = service.SearchDevices("NX 4200").Single();
        Assert.AreEqual("AMX.NX4200", nx4200.Hardware!.Id);
        Assert.AreEqual(HardwareDeviceCategory.ControlProcessor, nx4200.Hardware.Category);
        CollectionAssert.AreEquivalent(new[] { "AMX.NetLinxStudio4" }, service.GetSoftwareForDevice(nx4200)
            .SelectMany(group => group.Software).Select(software => software.ProductId.Value).Distinct().ToArray());

        var xi = service.SearchDevices("IPCP Pro 360Q xi").Single();
        Assert.AreEqual("Extron.IPCPPro360QXi", xi.Hardware!.Id);
        CollectionAssert.AreEquivalent(
            new[] { "Extron.GlobalConfiguratorPlus", "Extron.GlobalConfiguratorProfessional", "Extron.GlobalScripter", "Extron.Toolbelt" },
            service.GetSoftwareForDevice(xi).SelectMany(group => group.Software).Select(software => software.ProductId.Value).Distinct().ToArray());

        var retired = service.SearchDevices("IPL Pro S1").Single();
        Assert.AreEqual("Extron.IPLProS1", retired.Hardware!.Id);
        Assert.AreEqual(Lifecycle.Legacy, retired.Hardware.Lifecycle);
        CollectionAssert.AreEquivalent(new[] { "Extron.GlobalConfiguratorPlus", "Extron.GlobalScripter" }, service.GetSoftwareForDevice(retired)
            .SelectMany(group => group.Software).Select(software => software.ProductId.Value).Distinct().ToArray());
    }

    [TestMethod]
    public void DspBatchAExactModelsKeepSoftwareScopesAndCaveatsEvidenceBacked()
    {
        var service = CreateQueryService();

        var core110 = service.SearchDevices("Core 110f").Single();
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithVerifiedRelationships, core110.LookupState);
        AssertModelSoftware(service, "Core 110f", "QSYS.Core110f", "QSYSDesigner", DeviceSoftwarePurpose.Configuration);

        AssertModelSoftware(service, "Core Nano", "QSYS.CoreNano", "QSYSDesigner", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "TesiraFORTÉ X 800", "Biamp.TesiraForteX800", "Biamp.Tesira", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "DMP 128 Plus C V AT", "Extron.DMP128PlusCVAT", "Extron.DSPConfiguratorPro", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "P300-IMX", "Shure.P300IMX", "Shure.Designer", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "CONVERGE Pro 2 128", "ClearOne.ConvergePro2_128", "ClearOne.ConsoleAI", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "Radius NX", "Symetrix.RadiusNX", "Symetrix.Composer", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "Solus NX", "Symetrix.SolusNX", "Symetrix.Composer", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "EX-1280", "Bose.EX1280", "Bose.ControlSpaceDesigner", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "AHM-64", "AllenHeath.AHM64", "AllenHeath.AHMSystemManager", DeviceSoftwarePurpose.Configuration);
        AssertModelSoftware(service, "DME7", "Yamaha.DME7", "Yamaha.ProVisionaireDesign", DeviceSoftwarePurpose.Configuration);

        var p300 = service.GetSoftwareForDevice(service.SearchDevices("IntelliMix P300").Single(item => item.Hardware?.Id == "Shure.P300IMX")).SelectMany(group => group.Software).ToArray();
        Assert.IsFalse(p300.Any(item => item.ProductId.Value == "Shure.IntelliMixRoom"));
        Assert.IsTrue(p300.Any(item => item.ProductId.Value == "Shure.UpdateUtility" && item.Purpose == DeviceSoftwarePurpose.Firmware));
        Assert.IsTrue(p300.Any(item => item.Constraints.Contains("firmware", StringComparison.OrdinalIgnoreCase)));

        var jupiter = service.GetSoftwareForDevice(service.SearchDevices("Jupiter 8").Single(item => item.Hardware?.Id == "Symetrix.Jupiter8")).SelectMany(group => group.Software).ToArray();
        Assert.IsTrue(jupiter.Any(item => item.ProductId.Value == "Symetrix.Jupiter"));
        Assert.IsFalse(jupiter.Any(item => item.ProductId.Value == "Symetrix.Composer"));
    }

    [TestMethod]
    public void DspBatchBPreservesGenerationScopesAndExplicitUnresolvedModels()
    {
        var service = CreateQueryService();

        var omni = service.GetSoftwareForDevice("256p").SelectMany(group => group.Software).ToArray();
        Assert.IsTrue(omni.Any(item => item.ProductId.Value == "BSS.AVXArchitect" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        Assert.IsFalse(omni.Any(item => item.ProductId.Value == "BSS.AudioArchitect"));

        var london = service.GetSoftwareForDevice("BLU-100").SelectMany(group => group.Software).ToArray();
        Assert.IsTrue(london.Any(item => item.ProductId.Value == "BSS.AudioArchitect"));
        Assert.IsFalse(london.Any(item => item.ProductId.Value == "BSS.AVXArchitect"));

        var qr1Uc = service.SearchDevices("QR1-UC").Single();
        Assert.IsTrue(service.GetSoftwareForDevice(qr1Uc).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Xilica.Designer" && item.Purpose == DeviceSoftwarePurpose.Diagnostics));
        Assert.IsTrue(service.GetSoftwareForDevice("DSP-1280").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Crestron.AviaAudioTool" && item.Purpose == DeviceSoftwarePurpose.Configuration));
        Assert.IsTrue(service.GetSoftwareForDevice("SoundStructure SR12").SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == "Polycom.SoundStructureStudio"));

        var atmosphere = service.SearchDevices("AZM8-D").Single();
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet, atmosphere.LookupState);
        Assert.HasCount(0, service.GetSoftwareForDevice(atmosphere).SelectMany(group => group.Software));
        Assert.IsFalse(service.SearchDevices("BLU-320").Any());
    }

    [TestMethod]
    public void HardwareIdentityDocumentCannotGrantSoftwareOrExecutionAuthority()
    {
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PackageId", "Provider", "Authority", "Deployment", "Executable", "Command", "Credential", "Delivery", "Arguments", "WorkingDirectory", "ProductId"
        };
        using var document = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot(), "manifests", "hardware-identities.json")));
        Assert.HasCount(0, EnumeratePropertyNames(document.RootElement).Where(forbidden.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        CollectionAssert.DoesNotContain(typeof(HardwareFamily).GetProperties().Select(item => item.Name).ToArray(), "Authority");
        CollectionAssert.DoesNotContain(typeof(HardwareModel).GetProperties().Select(item => item.Name).ToArray(), "Provider");
        CollectionAssert.DoesNotContain(typeof(HardwareModel).GetProperties().Select(item => item.Name).ToArray(), "PackageId");
    }

    [TestMethod]
    public void HardwareIdentityParserRejectsUnknownFieldsAndInvalidCoverageStates()
    {
        const string valid = """
            { "SchemaVersion": 1, "Families": [
              { "Id": "Fixture.Family", "Manufacturer": "Fixture", "Category": "AudioDsp", "Name": "Fixture family", "Aliases": [], "Lifecycle": "Current", "CoverageState": "Unresolved", "CompatibilityDeviceFamilyId": "" }
            ], "Models": [
              { "Id": "Fixture.Model", "FamilyId": "Fixture.Family", "Name": "Fixture model", "Aliases": [], "Lifecycle": "Current", "CoverageState": "Unresolved" }
            ] }
            """;
        var parser = new HardwareIdentityCatalogParser();
        _ = parser.Parse(valid);
        Assert.ThrowsExactly<CatalogValidationException>(() => parser.Parse(valid.Replace("\"Models\"", "\"Unexpected\": true, \"Models\"", StringComparison.Ordinal)));
        Assert.ThrowsExactly<CatalogValidationException>(() => parser.Parse(valid.Replace("\"CoverageState\": \"Unresolved\" }", "\"CoverageState\": \"FamilyOnly\" }", StringComparison.Ordinal)));
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

        Assert.IsGreaterThan(0, services.Compatibility.SearchProducts().Count);
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

        // The compatibility catalog is loaded independently of package authority; its size is not the invariant.
        Assert.IsGreaterThan(0, compatibility.Products.Count);
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
        var avoipBatchBProductIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "Aurora.IPBaseTManager", "Visionary.VLite", "Matrox.ConvertIPManager", "Matrox.ConductIP",
            "WyreStorm.ManagementSuite", "JustAddPower.AMP", "JustAddPower.JADConfig"
        };
        Assert.IsTrue(packages.Items.Where(item => avoipBatchBProductIds.Contains(item.Id)).All(item => !item.HasManagedExecutionAuthority));
        var dspBatchADescriptiveOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            "Shure.Designer", "Shure.UpdateUtility", "Yamaha.MTXMRXEditor"
        };
        Assert.IsTrue(packages.Items.Where(item => dspBatchADescriptiveOnly.Contains(item.Id)).All(item => !item.HasManagedExecutionAuthority));
        CollectionAssert.DoesNotContain(typeof(CompatibilityProductSummary).GetProperties().Select(item => item.Name).ToArray(), "Authority");
        CollectionAssert.DoesNotContain(typeof(RelevantSoftwareSummary).GetProperties().Select(item => item.Name).ToArray(), "Provider");
        CollectionAssert.DoesNotContain(typeof(RelevantSoftwareSummary).GetProperties().Select(item => item.Name).ToArray(), "Delivery");
    }

    private static CompatibilityCatalogQueryService CreateQueryService() => new(
        LoadCatalog(),
        new UnresolvedInstalledVersionEvidenceProvider(),
        new RepositoryHardwareIdentityCatalogLoader().Load(RepositoryRoot()));

    private static void AssertModelSoftware(
        CompatibilityCatalogQueryService service,
        string search,
        string expectedHardwareId,
        string expectedProductId,
        DeviceSoftwarePurpose expectedPurpose)
    {
        var result = service.SearchDevices(search).Single(item => item.Hardware?.Id == expectedHardwareId);
        Assert.AreEqual(expectedHardwareId, result.Hardware!.Id);
        Assert.AreEqual(HardwareLookupState.KnownExactModelWithVerifiedRelationships, result.LookupState);
        Assert.IsTrue(service.GetSoftwareForDevice(result).SelectMany(group => group.Software)
            .Any(item => item.ProductId.Value == expectedProductId && item.Purpose == expectedPurpose));
    }

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
