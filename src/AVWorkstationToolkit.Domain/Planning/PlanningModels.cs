using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Domain.Planning;

public sealed record RebootState(bool Pending, IReadOnlyList<RebootReason> Reasons, string Summary)
{
    public static RebootState Clear { get; } = new(false, [], "No pending reboot signals");
}

public sealed record PackageEvidence(
    bool SourceAvailable,
    bool InventoryRecordPresent,
    bool InventoryReliable,
    InventoryQuality InventoryQuality,
    bool Installed,
    string InstalledVersion,
    IReadOnlyList<string> InstalledVersions,
    bool UpgradeAvailable,
    string AvailableVersion,
    string InventoryDetail,
    string ReleaseDetail);

public sealed record PackageState(
    PackageDefinition Package,
    bool Installed,
    string InstalledVersion,
    IReadOnlyList<string> InstalledVersions,
    string AvailableVersion,
    bool UpgradeAvailable,
    PackageStatus Status,
    string StatusDetail,
    string ReasonCode,
    PackageAction Action,
    InventoryQuality InventoryQuality)
{
    public bool CanSelect => Action is PackageAction.Install or PackageAction.Update;
}

public sealed record PolicyDecision(PolicyDisposition Disposition, string ReasonCode, string Message)
{
    public bool IsAllowed => Disposition == PolicyDisposition.Allowed;
}
