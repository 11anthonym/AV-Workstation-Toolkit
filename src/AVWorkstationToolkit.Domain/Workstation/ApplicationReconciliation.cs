using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Domain.Policies;
using AVWorkstationToolkit.Domain.Versions;

namespace AVWorkstationToolkit.Domain.Workstation;

public enum ChecklistStatus
{
    /// <summary>Detected on this workstation.</summary>
    Installed,
    /// <summary>A technician marked it done; this workstation can't confirm it.</summary>
    ConfirmedManually,
    /// <summary>The managed catalog allows an automatic installation here.</summary>
    ReadyToInstall,
    Installing,
    /// <summary>The last installation attempt failed, or reported success without the app being detected.</summary>
    InstallFailed,
    /// <summary>A known catalog application that must be installed manually.</summary>
    ManualInstall,
    /// <summary>Not in the catalog. Install it manually; a later scan recognizes it.</summary>
    UnknownApplication,
    /// <summary>The identity is ambiguous or only suggested by a similar name.</summary>
    NeedsReview,
    /// <summary>This workstation's inventory was incomplete, so the state can't be decided.</summary>
    CheckUnavailable,
    Excluded
}

public enum VersionDifference { Unknown, Same, Newer, Older, Different }

public sealed record InstallAttempt(DateTimeOffset AtUtc, bool Succeeded, string Message);

/// <summary>One application a migration or profile wants, with its locally resolved identity and the technician's choices.</summary>
public sealed record DesiredApplication(
    string ItemId,
    DesiredApplicationSpec Spec,
    IdentityResolution Identity,
    bool Included,
    DateTimeOffset? ConfirmedAtUtc = null,
    InstallAttempt? LastAttempt = null)
{
    public string DisplayName => Identity.IsConfident ? Identity.Application!.Name : Spec.DisplayName;
}

/// <summary>This workstation as reconciliation sees it: the trusted catalog plan and the observed inventory.</summary>
public sealed record ReconciliationTarget(
    IReadOnlyList<PackageState> CatalogStates,
    RebootState Reboot,
    WorkstationInventory Inventory);

public sealed record ReconciledApplication(
    DesiredApplication Desired,
    ChecklistStatus Status,
    string Detail,
    string InstalledVersion,
    VersionDifference VersionDifference,
    string MatchEvidence,
    PackageState? CatalogState,
    bool AutomaticInstallAllowed)
{
    public string ItemId => Desired.ItemId;
    public string DisplayName => Desired.DisplayName;
    public bool Satisfied => Status is ChecklistStatus.Installed or ChecklistStatus.ConfirmedManually;

    /// <summary>
    /// True only when this workstation's plan offers the exact managed install. The request still goes through the
    /// action coordinator's authorization and the worker's independent revalidation.
    /// </summary>
    public bool CanInstallAutomatically => AutomaticInstallAllowed && Desired.Included &&
        Status is ChecklistStatus.ReadyToInstall or ChecklistStatus.InstallFailed &&
        CatalogState is { Action: PackageAction.Install } state && state.Package.HasManagedExecutionAuthority;

    public bool CanConfirmManually => Desired.Included && !Satisfied && Status != ChecklistStatus.Installing;
}

public sealed record ChecklistSummary(
    int Included,
    int Satisfied,
    int Ready,
    int Manual,
    int Unknown,
    int Review,
    int CheckUnavailable,
    int Failed,
    int Installing,
    int Excluded)
{
    public int Remaining => Included - Satisfied;

    public static ChecklistSummary From(IEnumerable<ReconciledApplication> items)
    {
        var list = items.ToArray();
        int Count(ChecklistStatus status) => list.Count(item => item.Status == status);
        return new ChecklistSummary(
            list.Count(item => item.Desired.Included),
            list.Count(item => item.Desired.Included && item.Satisfied),
            Count(ChecklistStatus.ReadyToInstall),
            Count(ChecklistStatus.ManualInstall),
            Count(ChecklistStatus.UnknownApplication),
            Count(ChecklistStatus.NeedsReview),
            Count(ChecklistStatus.CheckUnavailable),
            Count(ChecklistStatus.InstallFailed),
            Count(ChecklistStatus.Installing),
            Count(ChecklistStatus.Excluded));
    }
}

/// <summary>
/// The one reconciliation engine for imported inventories and deployment profiles. Identity is matched in priority
/// order (catalog, exact WinGet ID, Windows installer identity, publisher and name). Installation capability comes only
/// from this workstation's catalog plan; nothing a desired application carries can make an app installable.
/// </summary>
public sealed class ApplicationReconciliationService
{
    private readonly SelectionPolicy selectionPolicy = new();

    public IReadOnlyList<ReconciledApplication> Reconcile(
        IEnumerable<DesiredApplication> desired,
        ReconciliationTarget target,
        IReadOnlySet<string>? installingItemIds = null)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(target);
        var index = new TargetIndex(target);
        return desired.Select(item => Reconcile(item, index, target, installingItemIds)).ToArray();
    }

    private ReconciledApplication Reconcile(DesiredApplication desired, TargetIndex index, ReconciliationTarget target, IReadOnlySet<string>? installing)
    {
        var known = desired.Identity.IsConfident ? desired.Identity.Application : null;
        var state = known is null ? null : index.State(known.Id);
        var detection = known is null ? index.DetectUncatalogued(desired.Spec) : index.DetectCatalogued(known, state);
        var registryComplete = target.Inventory.Sources.Registry == EvidenceQuality.Complete;

        ReconciledApplication Result(ChecklistStatus status, string detail, PackageState? catalogState = null, bool allowed = false) => new(
            desired, status, detail, detection.Version, detection.Found ? CompareVersions(desired.Spec.Version, detection.Version) : VersionDifference.Unknown,
            detection.Evidence, catalogState ?? state, allowed);

        if (!desired.Included)
            return Result(ChecklistStatus.Excluded, detection.Found
                ? "Excluded from this migration. It's installed on this PC."
                : "Excluded from this migration.");
        if (installing?.Contains(desired.ItemId) == true)
            return Result(ChecklistStatus.Installing, "Installation is running.");
        if (detection.Found)
            return Result(ChecklistStatus.Installed, InstalledDetail(desired, detection));
        if (desired.ConfirmedAtUtc is { } confirmed)
            return Result(ChecklistStatus.ConfirmedManually,
                $"Marked done by a technician on {confirmed.ToLocalTime():yyyy-MM-dd HH:mm}. This PC can't confirm it.");

        var outcome = known is null ? Uncatalogued(desired, registryComplete) : Catalogued(known, state, target, registryComplete);
        if (desired.LastAttempt is { } attempt && outcome.Status is ChecklistStatus.ReadyToInstall or ChecklistStatus.ManualInstall or ChecklistStatus.CheckUnavailable)
        {
            var message = attempt.Succeeded
                ? "The installer reported success, but the app isn't detected on this PC yet. Rescan, or restart Windows if the installer asked for it."
                : $"The last installation attempt failed: {attempt.Message}";
            return Result(ChecklistStatus.InstallFailed, message, outcome.State, outcome.Allowed);
        }
        return Result(outcome.Status, outcome.Detail, outcome.State, outcome.Allowed);
    }

    private (ChecklistStatus Status, string Detail, PackageState? State, bool Allowed) Catalogued(
        KnownApplication known, PackageState? state, ReconciliationTarget target, bool registryComplete)
    {
        if (known.Management == ApplicationManagement.ManagedWinGet)
        {
            if (state is null)
                return (ChecklistStatus.CheckUnavailable, "This app isn't in the managed catalog loaded on this PC.", null, false);
            if (state.Status == PackageStatus.Missing && state.Action == PackageAction.Install)
            {
                var decision = selectionPolicy.Evaluate(state, target.Reboot, riskAcknowledged: true);
                if (decision.Disposition == PolicyDisposition.Blocked)
                    return (ChecklistStatus.ReadyToInstall,
                        $"Restart Windows before installing: this app {RiskEffect(state.Package.Risk)} and Windows is waiting for a restart.", state, false);
                var risk = state.Package.Risk == PackageRisk.None ? string.Empty : $" It {RiskEffect(state.Package.Risk)}, so you'll confirm that first.";
                return (ChecklistStatus.ReadyToInstall, $"Approved for automatic installation through WinGet.{risk}", state, decision.IsAllowed ||
                    decision.Disposition == PolicyDisposition.RequiresAcknowledgement);
            }
            if (state.Status == PackageStatus.Manual)
                return (ChecklistStatus.ManualInstall, $"Held from automatic installation by catalog policy. {state.Package.Note}".Trim(), state, false);
            return (ChecklistStatus.CheckUnavailable, string.IsNullOrWhiteSpace(state.StatusDetail)
                ? "Couldn't confirm whether this app is installed."
                : $"Couldn't confirm whether this app is installed. {state.StatusDetail}", state, false);
        }

        if (!known.Detectable)
            return (ChecklistStatus.ManualInstall,
                "Install this manually. AV Workstation Toolkit can't detect it, so mark it done when it's installed.", state, false);
        if (!registryComplete)
            return (ChecklistStatus.CheckUnavailable, "Some installed-app registrations on this PC couldn't be read, so this app's state is unknown.", state, false);
        return (ChecklistStatus.ManualInstall, ManualDetail(known.Package), state, false);
    }

    private static (ChecklistStatus Status, string Detail, PackageState? State, bool Allowed) Uncatalogued(DesiredApplication desired, bool registryComplete)
    {
        if (!registryComplete)
            return (ChecklistStatus.CheckUnavailable, "Some installed-app registrations on this PC couldn't be read, so this app's state is unknown.", null, false);
        if (desired.Identity.Confidence is IdentityConfidence.Probable or IdentityConfidence.Ambiguous)
            return (ChecklistStatus.NeedsReview,
                $"Couldn't identify this app confidently ({desired.Identity.Describe()}). Install it manually if it's needed, or exclude it.", null, false);
        if (desired.Spec.WinGetId.Length > 0)
            return (ChecklistStatus.UnknownApplication,
                $"WinGet knows this app as {desired.Spec.WinGetId}, but it isn't in the approved managed catalog, so AV Workstation Toolkit won't install it. Install it manually.", null, false);
        var publisher = desired.Spec.Publisher.Length > 0 ? $" from {desired.Spec.Publisher}" : string.Empty;
        return (ChecklistStatus.UnknownApplication,
            $"Not in the AV Workstation Toolkit catalog. Install it manually{publisher}; a rescan recognizes it once it's installed.", null, false);
    }

    private static string ManualDetail(PackageDefinition package) => package.DeliveryMode switch
    {
        DeliveryMode.VendorPage => "Install this manually from the vendor page. Get package in the main window opens it.",
        DeliveryMode.DirectDownload or DeliveryMode.AuthenticatedSftp or DeliveryMode.ParentProvider =>
            "Download this with Get package in the main window, then run the installer yourself.",
        DeliveryMode.Bundled => "Use the approved bundled installer from Get package in the main window, then run it yourself.",
        _ => "Install this manually from the vendor."
    };

    private static string InstalledDetail(DesiredApplication desired, Detection detection)
    {
        var version = CompareVersions(desired.Spec.Version, detection.Version) switch
        {
            VersionDifference.Newer => $" Version {detection.Version} is newer than {desired.Spec.Version} on the source.",
            VersionDifference.Older => $" Version {detection.Version} is older than {desired.Spec.Version} on the source.",
            VersionDifference.Different => $" Version {detection.Version} differs from {desired.Spec.Version} on the source.",
            _ => string.Empty
        };
        return $"Installed. {detection.Evidence}.{version}{detection.Note}";
    }

    private static string RiskEffect(PackageRisk risk) => risk switch
    {
        PackageRisk.Driver => "installs a driver",
        PackageRisk.Service => "adds a background service",
        PackageRisk.Listener => "accepts network connections",
        _ => "changes this PC"
    };

    public static VersionDifference CompareVersions(string desired, string installed)
    {
        if (string.IsNullOrWhiteSpace(desired) || string.IsNullOrWhiteSpace(installed)) return VersionDifference.Unknown;
        if (WorkstationInventoryBuilder.SameVersion(desired, installed)) return VersionDifference.Same;
        if (VersionValue.TryParse(desired.Trim(), out var wanted) && VersionValue.TryParse(installed.Trim(), out var actual))
            return actual.CompareTo(wanted) > 0 ? VersionDifference.Newer : VersionDifference.Older;
        return VersionDifference.Different;
    }

    private readonly record struct Detection(bool Found, string Version, string Evidence, string Note = "")
    {
        public static Detection None { get; } = new(false, string.Empty, string.Empty);
    }

    private sealed class TargetIndex
    {
        private readonly Dictionary<string, PackageState> states;
        private readonly ILookup<string, ObservedApplication> byCatalogId;
        private readonly ILookup<string, ObservedApplication> byWinGetId;
        private readonly ILookup<string, ObservedApplication> byUpgradeCode;
        private readonly ILookup<string, ObservedApplication> byUninstallKey;
        private readonly ILookup<string, ObservedApplication> byName;

        public TargetIndex(ReconciliationTarget target)
        {
            states = target.CatalogStates.GroupBy(item => item.Package.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var applications = target.Inventory.Applications;
            byCatalogId = applications.Where(item => item.CatalogId.Length > 0).ToLookup(item => item.CatalogId, StringComparer.OrdinalIgnoreCase);
            byWinGetId = applications.Where(item => item.WinGetId.Length > 0).ToLookup(item => item.WinGetId, StringComparer.OrdinalIgnoreCase);
            byUpgradeCode = applications.Where(item => item.MsiUpgradeCode.Length > 0).ToLookup(item => item.MsiUpgradeCode, StringComparer.OrdinalIgnoreCase);
            byUninstallKey = applications.SelectMany(item => item.UninstallKeys.Select(key => (key, item)))
                .ToLookup(pair => pair.key, pair => pair.item, StringComparer.OrdinalIgnoreCase);
            byName = applications.ToLookup(item => ApplicationNames.Compact(item.NameKey), StringComparer.Ordinal);
        }

        public PackageState? State(string id) => states.TryGetValue(id, out var state) ? state : null;

        public Detection DetectCatalogued(KnownApplication known, PackageState? state)
        {
            if (state is { Installed: true })
                return new(true, FirstNonEmpty(state.InstalledVersion, byCatalogId[known.Id].FirstOrDefault()?.DisplayVersion),
                    known.Management == ApplicationManagement.ManagedWinGet ? "Detected by WinGet" : "Detected by the catalog detector");
            if (byCatalogId[known.Id].FirstOrDefault() is { } observed)
            {
                var note = known.Management == ApplicationManagement.ManagedWinGet && state is not null
                    ? " WinGet doesn't recognize this installation, so AV Workstation Toolkit won't manage its updates."
                    : string.Empty;
                return new(true, observed.DisplayVersion, $"Detected as {observed.DisplayName} in Windows installed apps", note);
            }
            return Detection.None;
        }

        public Detection DetectUncatalogued(DesiredApplicationSpec spec)
        {
            if (spec.WinGetId.Length > 0 && byWinGetId[spec.WinGetId].FirstOrDefault() is { } byPackage)
                return new(true, byPackage.DisplayVersion, $"Same WinGet package ID ({spec.WinGetId})");
            if (spec.MsiUpgradeCode.Length > 0 && byUpgradeCode[spec.MsiUpgradeCode].FirstOrDefault() is { } byUpgrade)
                return new(true, byUpgrade.DisplayVersion, "Same Windows Installer upgrade code");
            foreach (var key in spec.UninstallKeys)
                if (byUninstallKey[key].FirstOrDefault() is { } byKey)
                    return new(true, byKey.DisplayVersion, "Same Windows uninstall registration");
            var name = ApplicationNames.CompactKey(spec.DisplayName);
            if (name.Length >= 3)
            {
                var candidates = byName[name].ToArray();
                var match = spec.Publisher.Length == 0
                    ? candidates.FirstOrDefault()
                    : candidates.FirstOrDefault(item => item.Publisher.Length == 0 || ApplicationNames.PublishersAgree(item.Publisher, spec.Publisher));
                if (match is not null)
                    return new(true, match.DisplayVersion, $"Matched {match.DisplayName} by name and publisher");
            }
            return Detection.None;
        }

        private static string FirstNonEmpty(string? first, string? second) =>
            !string.IsNullOrWhiteSpace(first) ? first : second ?? string.Empty;
    }
}
