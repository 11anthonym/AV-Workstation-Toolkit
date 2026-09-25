using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Versions;

namespace AVWorkstationToolkit.Domain.Planning;

public sealed class PlanningService
{
    public PackageState Evaluate(PackageDefinition package, PackageEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(evidence);
        return package.Authority == CatalogAuthority.ManagedWinGet
            ? EvaluateManaged(package, evidence)
            : EvaluateExternal(package, evidence);
    }

    private static PackageState EvaluateManaged(PackageDefinition package, PackageEvidence evidence)
    {
        if (package.Provider != ProviderKind.WinGet)
        {
            throw new InvalidOperationException("Managed execution authority requires the WinGet provider.");
        }

        var status = PackageStatus.Current;
        var action = PackageAction.None;
        var detail = "Installed and current";
        var reason = "InstalledCurrent";
        if (!evidence.SourceAvailable)
        {
            status = PackageStatus.Error;
            detail = "winget inventory unavailable";
            reason = "WingetInventoryUnavailable";
        }
        else if (!evidence.Installed)
        {
            if (package.Deployment == DeploymentPolicy.ManualHold)
            {
                status = PackageStatus.Manual;
                action = PackageAction.Manual;
                detail = "Manual review required";
                reason = "DeploymentManualHold";
            }
            else
            {
                status = PackageStatus.Missing;
                action = PackageAction.Install;
                detail = "Available for approved installation";
                reason = "AllowlistedInstallAvailable";
            }
        }
        else if (evidence.UpgradeAvailable)
        {
            if (package.Maintenance == MaintenancePolicy.Hold)
            {
                status = PackageStatus.Held;
                detail = "Automated maintenance hold";
                reason = "MaintenanceHold";
            }
            else
            {
                status = PackageStatus.UpdateAvailable;
                action = PackageAction.Update;
                detail = "Allowlisted update available";
                reason = "AllowlistedUpdateAvailable";
            }
        }

        return State(package, evidence, status, detail, reason, action, evidence.SourceAvailable ? InventoryQuality.Complete : InventoryQuality.Unavailable);
    }

    private static PackageState EvaluateExternal(PackageDefinition package, PackageEvidence evidence)
    {
        if (package.Provider != ProviderKind.External || package.HasManagedExecutionAuthority)
        {
            throw new InvalidOperationException("External planning requires a non-managed external package.");
        }

        var quality = evidence.InventoryRecordPresent ? evidence.InventoryQuality : InventoryQuality.Unavailable;
        var reliable = evidence.InventoryRecordPresent && evidence.InventoryReliable;
        var installed = evidence.InventoryRecordPresent && evidence.Installed;
        var installedVersion = evidence.InventoryRecordPresent ? evidence.InstalledVersion : string.Empty;
        var installedVersions = evidence.InventoryRecordPresent ? evidence.InstalledVersions : [];
        var availableVersion = string.IsNullOrEmpty(evidence.AvailableVersion) ? package.KnownVersion : evidence.AvailableVersion;
        var status = PackageStatus.Error;
        var action = PackageAction.None;
        var detail = string.Empty;
        var reason = "ExternalPackageError";
        var upgrade = false;

        if (package.DetectionMode == DetectionMode.None)
        {
            availableVersion = package.ReleaseMode == ReleaseMode.ParentCatalog ? availableVersion : string.Empty;
            status = PackageStatus.Awareness;
            detail = "Known catalog record; this product is not treated as a detectable Windows application.";
            reason = "AwarenessOnly";
        }
        else if (package.ReleaseMode == ReleaseMode.InventoryOnly)
        {
            availableVersion = string.Empty;
            // A registry match is positive evidence even when the entry reports no usable version;
            // inventory-only records never compare versions.
            if (installed)
            {
                status = PackageStatus.Inventory;
                detail = "Installed application recorded for inventory; AV Workstation Toolkit will not change it.";
                reason = "InventoryDetected";
            }
            else if (reliable)
            {
                status = PackageStatus.NotDetected;
                detail = "No matching installation detected; AV Workstation Toolkit will not install this inventory-only item.";
                reason = "InventoryNotDetected";
            }
            else
            {
                status = QualityStatus(quality);
                detail = evidence.InventoryRecordPresent && !string.IsNullOrWhiteSpace(evidence.InventoryDetail)
                    ? evidence.InventoryDetail : "External application inventory is unavailable.";
                reason = QualityReason(quality);
            }
        }
        else if (!VersionValue.TryParse(availableVersion, out _))
        {
            status = PackageStatus.CheckUnavailable;
            detail = "External release version is invalid.";
            reason = "ExternalReleaseVersionInvalid";
        }
        else
        {
            if (reliable && package.DetectionVersionPolicy == DetectionVersionPolicy.SameMajorMinor)
            {
                var target = availableVersion.Split('.');
                var matching = installedVersions.Where(candidate =>
                {
                    var parts = candidate.Split('.');
                    return parts.Length >= 2 && parts[0] == target[0] && parts[1] == target[1];
                }).ToArray();
                installed = matching.Length > 0;
                installedVersion = string.Empty;
                foreach (var candidate in matching)
                {
                    if (installedVersion.Length == 0 || VersionValue.Parse(candidate).CompareTo(VersionValue.Parse(installedVersion)) > 0)
                    {
                        installedVersion = candidate;
                    }
                }
            }

            if (!reliable)
            {
                status = QualityStatus(quality);
                detail = evidence.InventoryRecordPresent && !string.IsNullOrWhiteSpace(evidence.InventoryDetail)
                    ? evidence.InventoryDetail : "External application inventory is unavailable.";
                reason = QualityReason(quality);
            }
            else if (!installed)
            {
                status = PackageStatus.Manual;
                action = PackageAction.Manual;
                detail = $"The {package.ReleaseChannel} channel is not installed; release {availableVersion} is available.";
                reason = "ExternalManualInstall";
            }
            else
            {
                try { upgrade = VersionValue.Parse(availableVersion).CompareTo(VersionValue.Parse(installedVersion)) > 0; }
                catch (FormatException)
                {
                    status = PackageStatus.Error;
                    detail = "Installed external application version could not be compared safely.";
                    reason = "InstalledVersionInvalid";
                    reliable = false;
                }

                if (reliable && upgrade)
                {
                    status = PackageStatus.ManualUpdate;
                    action = PackageAction.Manual;
                    detail = $"Vendor-managed update {availableVersion} is available.";
                    reason = "ExternalManualUpdate";
                }
                else if (reliable)
                {
                    status = PackageStatus.Current;
                    detail = $"Installed external application satisfies the {package.ReleaseChannel} baseline.";
                    reason = "ExternalCurrent";
                }
            }
        }

        return new PackageState(package, installed, installedVersion, installedVersions, availableVersion, upgrade, status, detail, reason, action, quality);
    }

    private static PackageState State(
        PackageDefinition package,
        PackageEvidence evidence,
        PackageStatus status,
        string detail,
        string reason,
        PackageAction action,
        InventoryQuality quality) =>
        new(package, evidence.Installed, evidence.InstalledVersion, evidence.InstalledVersions,
            evidence.AvailableVersion, evidence.UpgradeAvailable, status, detail, reason, action, quality);

    private static PackageStatus QualityStatus(InventoryQuality quality) => quality switch
    {
        InventoryQuality.Partial => PackageStatus.InventoryIncomplete,
        InventoryQuality.Unavailable => PackageStatus.InventoryUnavailable,
        _ => PackageStatus.Error
    };

    private static string QualityReason(InventoryQuality quality) => quality switch
    {
        InventoryQuality.Partial => "InventoryIncomplete",
        InventoryQuality.Unavailable => "InventoryUnavailable",
        _ => "ExternalInventoryPackageError"
    };
}
