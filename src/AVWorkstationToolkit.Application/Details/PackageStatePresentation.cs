using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;

namespace AVWorkstationToolkit.Application.Details;

/// <summary>
/// Maps typed package evidence to concise user-facing text. It does not change
/// package state, eligibility, or execution authority.
/// </summary>
public static class PackageStatePresentation
{
    public static string Status(PackageState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Status switch
        {
            PackageStatus.Current when state.Package.Provider == ProviderKind.WinGet => "Up to date",
            PackageStatus.Current => "Meets catalog version",
            PackageStatus.Missing => "Not installed",
            PackageStatus.UpdateAvailable => "Update available",
            PackageStatus.ManualUpdate => "Update through vendor",
            PackageStatus.Held => "Automatic updates paused",
            PackageStatus.Manual when state.Package.Provider == ProviderKind.External => "Install through vendor",
            PackageStatus.Manual => "Review required",
            PackageStatus.Inventory => "Installed",
            PackageStatus.NotDetected => "Not installed",
            PackageStatus.InventoryIncomplete => "Installation check incomplete",
            PackageStatus.InventoryUnavailable => "Couldn't check installation",
            PackageStatus.CheckUnavailable => "Couldn't check for updates",
            PackageStatus.Awareness => "Information only",
            PackageStatus.Error => "Couldn't check status",
            _ => "Status unavailable"
        };
    }

    public static string Detail(PackageState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.ReasonCode switch
        {
            "InstalledCurrent" => "Installed and up to date.",
            "WingetInventoryUnavailable" => "Couldn't check installed apps with WinGet.",
            "DeploymentManualHold" => "Review this app before installing it.",
            "AllowlistedInstallAvailable" => "Ready to install.",
            "MaintenanceHold" => "Automatic updates are paused for this app.",
            "AllowlistedUpdateAvailable" => "Ready to update.",
            "AwarenessOnly" => "Information only. AVWT doesn't check or install this item.",
            "InventoryDetected" => "Installed. AVWT won't change this inventory-only app.",
            "InventoryNotDetected" => "Not found. AVWT won't install this inventory-only app.",
            "ExternalReleaseVersionInvalid" => "Installation information may be available, but the vendor release couldn't be checked.",
            "ExternalManualInstall" => VersionSentence("Install through the vendor", state.AvailableVersion),
            "ExternalManualUpdate" => VersionSentence("A vendor-managed update is available", state.AvailableVersion),
            "ExternalCurrent" => "The installed version meets the catalog version.",
            "InstalledVersionInvalid" => "The installed version couldn't be compared.",
            "InventoryIncomplete" => "Some installation information is missing.",
            "InventoryUnavailable" => "Couldn't check this app's installation.",
            "ExternalInventoryPackageError" or "ExternalPackageError" => "Couldn't determine this app's status.",
            _ => Status(state) + "."
        };
    }

    public static string Installation(PackageState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Status == PackageStatus.Awareness || state.Package.DetectionMode == DetectionMode.None) return "Not checked";
        if (state.Installed) return "Yes";
        return state.InventoryQuality switch
        {
            InventoryQuality.Complete => "No",
            InventoryQuality.Partial => "Not confirmed",
            InventoryQuality.Unavailable or InventoryQuality.PackageError => "Couldn't check",
            _ => "Not checked"
        };
    }

    public static string InstalledVersion(PackageState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Status == PackageStatus.Awareness || state.Package.DetectionMode == DetectionMode.None) return "Not checked";
        if (state.Installed) return string.IsNullOrWhiteSpace(state.InstalledVersion) ? "Version unknown" : state.InstalledVersion;
        return state.InventoryQuality switch
        {
            InventoryQuality.Complete => "Not installed",
            InventoryQuality.Partial => "Not confirmed",
            InventoryQuality.Unavailable or InventoryQuality.PackageError => "Couldn't check",
            _ => "Not checked"
        };
    }

    public static string SelectionHint(PackageState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.CanSelect && state.Package.HasManagedExecutionAuthority)
            return state.Action == PackageAction.Update ? "Select this app to update it." : "Select this app to install it.";
        return state.Status switch
        {
            PackageStatus.Current => "No action needed.",
            PackageStatus.Held => "Automatic updates are paused for this app.",
            PackageStatus.Manual or PackageStatus.ManualUpdate => "Use the vendor's installation process.",
            PackageStatus.InventoryIncomplete or PackageStatus.InventoryUnavailable or PackageStatus.CheckUnavailable or PackageStatus.Error =>
                $"This app can't be selected because its status is uncertain. {Detail(state)}",
            PackageStatus.Awareness => "Information only. This item can't be installed or updated by AVWT.",
            PackageStatus.Inventory or PackageStatus.NotDetected => "Inventory only. AVWT won't change this app.",
            _ => "No install or update is available."
        };
    }

    private static string VersionSentence(string prefix, string version) => string.IsNullOrWhiteSpace(version)
        ? $"{prefix}."
        : $"{prefix}: {version}.";
}
