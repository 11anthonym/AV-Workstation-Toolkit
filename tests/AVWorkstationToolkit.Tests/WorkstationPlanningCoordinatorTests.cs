using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class WorkstationPlanningCoordinatorTests
{
    [TestMethod]
    public async Task UnavailableWinGetInventoryCannotBecomeMissingOrCurrent()
    {
        var catalog = new CatalogParser(new DateOnly(2026, 8, 28)).NormalizeManagedCatalog(
            [new("Standard", "Fixture", "Fixture.Managed", "Fixture", "None", "fixture", null, null)], "NeverMatch");
        var coordinator = new WorkstationPlanningCoordinator(
            catalog,
            new InstalledProvider(new(ProviderQuality.Unavailable, ProviderFailureKind.ProviderUnavailable, [], "unavailable", string.Empty)),
            new UpdateProvider(new(ProviderQuality.Unavailable, ProviderFailureKind.ProviderUnavailable, [], "unavailable", string.Empty)),
            new RegistryProvider(Registry(ProviderQuality.Complete)),
            new RebootProvider(new(false, [], ProviderQuality.Complete, ProviderFailureKind.None, "clear")));

        var plan = await coordinator.RefreshAsync();

        Assert.AreEqual(PackageStatus.Error, plan.Packages[0].Status);
        Assert.AreEqual("WingetInventoryUnavailable", plan.Packages[0].ReasonCode);
        Assert.HasCount(2, plan.Providers.Warnings);
    }

    [TestMethod]
    public async Task PartialExternalInventoryProducesExplicitIncompleteState()
    {
        const string externalJson = """
        {"SchemaVersion":3,"Packages":[{"Profile":"Field","Name":"Fixture external","Id":"Fixture.External","Risk":"None","Note":"fixture","Deployment":"ManualHold","Maintenance":"Hold","KnownVersion":"1.0","Detection":{"RegistryDisplayNamePattern":"^Fixture External$","RegistryVersionPattern":"(?<Version>\\d+\\.\\d+)","VersionPolicy":"AtLeast"},"Release":{"Mode":"VendorPage","Channel":"Current","Uri":"https://example.com/tool","VersionPattern":"(?<Version>\\d+\\.\\d+)"},"Delivery":{"Mode":"VendorPage","Uri":"https://example.com/tool"},"Metadata":{"Vendor":"Fixture","ProductFamily":"Fixture","ApplicationType":["FieldUtility"],"Priority":"P2","Roles":["FieldService"],"DeploymentClass":"ManualHandoff","MaintenancePolicy":"VendorManaged","VersionRule":"Latest","CurrentOrLegacy":"Current","LicensingModel":["FREE"],"DownloadAccess":["PUBLIC-PAGE"],"SupportedOS":["Windows"],"OfficialProductUri":"https://example.com/tool","ValidationMethod":["Registry"]}}]}
        """;
        var catalog = new CatalogParser(new DateOnly(2026, 8, 28)).ParseExternalCatalog(externalJson);
        var registry = new RegistryInventoryResult(
            ProviderQuality.Partial, ProviderFailureKind.PartialInventory, [],
            [new(RegistryInventorySource.Hklm64, true, 0, "ok"), new(RegistryInventorySource.Hklm32, true, 0, "ok"), new(RegistryInventorySource.Hkcu, false, 0, "failed")],
            "1 of 3 registry sources unavailable.");
        var coordinator = new WorkstationPlanningCoordinator(
            catalog,
            new InstalledProvider(new(ProviderQuality.Complete, ProviderFailureKind.None, [], "ok", string.Empty)),
            new UpdateProvider(new(ProviderQuality.Complete, ProviderFailureKind.None, [], "ok", string.Empty)),
            new RegistryProvider(registry),
            new RebootProvider(new(false, [], ProviderQuality.Complete, ProviderFailureKind.None, "clear")));

        var plan = await coordinator.RefreshAsync();

        Assert.AreEqual(PackageStatus.InventoryIncomplete, plan.Packages[0].Status);
        Assert.AreEqual(InventoryQuality.Partial, plan.Packages[0].InventoryQuality);
        Assert.HasCount(1, plan.Providers.Warnings);
    }

    private static RegistryInventoryResult Registry(ProviderQuality quality) => new(
        quality, ProviderFailureKind.None, [],
        [new(RegistryInventorySource.Hklm64, true, 0, "ok"), new(RegistryInventorySource.Hklm32, true, 0, "ok"), new(RegistryInventorySource.Hkcu, true, 0, "ok")], "ok");

    private sealed class InstalledProvider(InstalledPackageInventoryResult result) : IInstalledPackageInventory
    {
        public Task<InstalledPackageInventoryResult> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(result);
    }
    private sealed class UpdateProvider(AvailableUpdateInventoryResult result) : IAvailableUpdateInventory
    {
        public Task<AvailableUpdateInventoryResult> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(result);
    }
    private sealed class RegistryProvider(RegistryInventoryResult result) : IExternalApplicationInventory
    {
        public Task<RegistryInventoryResult> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(result);
    }
    private sealed class RebootProvider(RebootDetectionResult result) : IRebootStateProvider
    {
        public Task<RebootDetectionResult> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(result);
    }
}
