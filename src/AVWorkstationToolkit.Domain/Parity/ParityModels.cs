using System.Text.Json.Serialization;

namespace AVWorkstationToolkit.Domain.Parity;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ParityFixture(
    int SchemaVersion,
    string ScenarioId,
    RebootFixture Reboot,
    WingetFixture Winget,
    IReadOnlyList<PackageFixture> Packages);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RebootFixture(bool Pending, IReadOnlyList<string> Reasons, string Summary);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WingetFixture(
    bool Available,
    IReadOnlyList<InstalledPackageFixture> Installed,
    IReadOnlyList<UpgradePackageFixture> Upgrades);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InstalledPackageFixture(string Id, string Version);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpgradePackageFixture(string Id, string InstalledVersion, string AvailableVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageFixture(
    string Id,
    string Provider,
    string Deployment,
    string Maintenance,
    string Risk);

public sealed record CanonicalPackageState(
    string Id,
    string Provider,
    bool Installed,
    string InstalledVersion,
    string AvailableVersion,
    string Status,
    string StatusDetail,
    string ReasonCode,
    string Risk,
    bool CanSelect,
    string Action,
    string DeliveryMode,
    string InventoryQuality,
    bool WorkerEligible);

public sealed record CanonicalParityResult(
    int SchemaVersion,
    string ScenarioId,
    IReadOnlyList<CanonicalPackageState> Packages);
