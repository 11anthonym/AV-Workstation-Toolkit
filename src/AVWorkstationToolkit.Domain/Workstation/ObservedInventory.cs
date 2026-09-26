using System.Text.RegularExpressions;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Versions;

namespace AVWorkstationToolkit.Domain.Workstation;

public enum UninstallHive { Machine64, Machine32, User }
public enum InstallScope { Unknown, Machine, User }
public enum EvidenceQuality { Complete, Partial, Unavailable }

/// <summary>Whether an observed application belongs on a migration checklist by default. It never affects authority.</summary>
public enum MigrationRelevance
{
    /// <summary>A user-facing application a technician would miss on a replacement workstation.</summary>
    Application,
    /// <summary>A runtime, driver package, or Windows-supplied component that the applications needing it install.</summary>
    SupportComponent,
    /// <summary>A registration Windows hides from Installed apps (SystemComponent).</summary>
    SystemComponent,
    /// <summary>An update or patch registered beneath a parent product.</summary>
    Update
}

/// <summary>How a WinGet export identity came to be attached to an observed application.</summary>
public enum WinGetCorrelation
{
    None,
    /// <summary>WinGet reported the package and no uninstall registration corresponds to it (for example an MSIX-only app).</summary>
    ExportOnly,
    /// <summary>A managed catalog detector recognized the registration, and WinGet reported that exact managed ID.</summary>
    CatalogIdentity,
    /// <summary>The registered name equals the package ID's name, with publisher agreement where the ID alone is not enough.</summary>
    RegisteredName,
    /// <summary>The publisher agrees and the registration carries exactly the version WinGet reported.</summary>
    RegisteredVersion
}

/// <summary>The strongest identity an observed application carries, in reconciliation priority order.</summary>
public enum IdentityKind { Catalog, WinGet, Installer, Name, Unresolved }

/// <summary>Raw evidence: one Windows uninstall registration as recorded, with bounded and cleaned text.</summary>
public sealed record UninstallRegistration(
    UninstallHive Hive,
    string KeyName,
    string DisplayName,
    string DisplayVersion,
    string Publisher,
    bool SystemComponent = false,
    bool WindowsInstaller = false,
    string ParentKeyName = "",
    string ReleaseType = "",
    string MsiUpgradeCode = "")
{
    private static readonly HashSet<string> UpdateReleaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Update", "Hotfix", "Security Update", "Update Rollup", "Service Pack", "Critical Update"
    };

    public bool IsUpdate => ParentKeyName.Length > 0 || UpdateReleaseTypes.Contains(ReleaseType);

    public string View => Hive switch
    {
        UninstallHive.Machine64 => "HKLM64",
        UninstallHive.Machine32 => "HKLM32",
        _ => "HKCU"
    };
}

/// <summary>Raw evidence: one package WinGet's export reported for its winget source.</summary>
public sealed record WinGetPackageEvidence(string Id, string Version);

public sealed record WorkstationMachine(
    string ComputerName,
    string WindowsEdition,
    string WindowsVersion,
    string OsBuild,
    string Architecture)
{
    public static WorkstationMachine Unknown { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
}

public sealed record InventorySourceQuality(EvidenceQuality Registry, EvidenceQuality WinGet, string Detail)
{
    public bool Complete => Registry == EvidenceQuality.Complete && WinGet == EvidenceQuality.Complete;
}

/// <summary>
/// One logical application observed on a workstation. The raw registrations and WinGet record that produced it are
/// kept alongside the catalog and WinGet identities that enrich it, so identity never replaces evidence.
/// </summary>
public sealed record ObservedApplication(
    string ObservationKey,
    string DisplayName,
    string DisplayVersion,
    IReadOnlyList<string> Versions,
    string Publisher,
    InstallScope Scope,
    string Architecture,
    IReadOnlyList<UninstallRegistration> Registrations,
    WinGetPackageEvidence? WinGet,
    WinGetCorrelation WinGetCorrelation,
    IdentityResolution CatalogIdentity,
    string MsiUpgradeCode,
    MigrationRelevance Relevance)
{
    public string CatalogId => CatalogIdentity.IsConfident ? CatalogIdentity.Application!.Id : string.Empty;
    public string WinGetId => WinGet?.Id ?? string.Empty;
    public string NameKey => ApplicationNames.NameKey(DisplayName);
    public string PublisherKey => ApplicationNames.PublisherKey(Publisher);

    public IReadOnlyList<string> UninstallKeys => Registrations.Select(item => item.KeyName)
        .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public IdentityKind IdentityKind =>
        CatalogId.Length > 0 ? IdentityKind.Catalog
        : WinGetId.Length > 0 ? IdentityKind.WinGet
        : MsiUpgradeCode.Length > 0 || UninstallKeys.Count > 0 ? IdentityKind.Installer
        : ApplicationNames.Compact(NameKey).Length > 0 ? IdentityKind.Name
        : IdentityKind.Unresolved;
}

public sealed record WorkstationInventory(
    DateTimeOffset CapturedAtUtc,
    WorkstationMachine Machine,
    InventorySourceQuality Sources,
    IReadOnlyList<ObservedApplication> Applications)
{
    public int ApplicationCount => Applications.Count(item => item.Relevance == MigrationRelevance.Application);
}

/// <summary>
/// Classifies runtimes, driver packages, and Windows-supplied components. The classification only decides whether a
/// migration includes an item by default; the item stays in the inventory as evidence.
/// </summary>
public static partial class ApplicationComponentRules
{
    private static readonly string[] SupportWinGetPrefixes =
    [
        "Microsoft.VCRedist.", "Microsoft.VCLibs.", "Microsoft.UI.Xaml.", "Microsoft.WindowsAppRuntime.",
        "Microsoft.DotNet.Native.", "Microsoft.DotNet.Runtime.", "Microsoft.DotNet.DesktopRuntime.",
        "Microsoft.DotNet.AspNetCore.", "Microsoft.DotNet.HostingBundle.", "Microsoft.DirectX"
    ];

    private static readonly HashSet<string> SupportWinGetIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.Edge", "Microsoft.EdgeWebView2Runtime", "Microsoft.AppInstaller"
    };

    [GeneratedRegex(@"^(?:Microsoft Visual C\+\+ .*(?:Redistributable|Runtime)|Microsoft (?:ASP\.NET Core|\.NET|Windows Desktop)\b.*\b(?:Runtime|Shared Framework|Host|Host FX Resolver|Targeting Pack|AppHost Pack|Templates)\b|Microsoft Edge(?: Update| WebView2 Runtime)?$|Microsoft Update Health Tools$|Windows Driver Package - |Microsoft Windows Application Compatibility Fix Database$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex SupportNamePattern();

    public static bool IsSupportComponent(string displayName, string winGetId)
    {
        if (winGetId.Length > 0 &&
            (SupportWinGetIds.Contains(winGetId) || SupportWinGetPrefixes.Any(prefix => winGetId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
            return true;
        return displayName.Length > 0 && SupportNamePattern().IsMatch(displayName);
    }
}

/// <summary>
/// Builds the unified observed inventory. Windows uninstall registrations are the base: each is enriched with catalog
/// identity through the shared catalog detectors, grouped with its duplicates, and then enriched with an exact WinGet
/// identity where the export correlates with it. WinGet never removes or gates a registration; a package WinGet reports
/// without any registration (such as an MSIX-only app) is added as its own observation.
/// </summary>
public sealed class WorkstationInventoryBuilder
{
    private readonly ApplicationIdentityCatalog identities;

    public WorkstationInventoryBuilder(ApplicationIdentityCatalog identities) =>
        this.identities = identities ?? throw new ArgumentNullException(nameof(identities));

    public WorkstationInventory Build(
        IEnumerable<UninstallRegistration> registrations,
        EvidenceQuality registryQuality,
        IEnumerable<WinGetPackageEvidence> winGetPackages,
        EvidenceQuality winGetQuality,
        WorkstationMachine machine,
        DateTimeOffset capturedAtUtc,
        string sourceDetail = "")
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(winGetPackages);
        ArgumentNullException.ThrowIfNull(machine);
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        var ordered = new List<Group>();

        foreach (var registration in registrations
                     .Where(item => item.DisplayName.Length > 0)
                     .OrderBy(item => item.Hive)
                     .ThenBy(item => item.KeyName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.DisplayName, StringComparer.Ordinal))
        {
            var identity = identities.ResolveDisplayName(registration.DisplayName);
            var key = GroupKey(registration, identity);
            if (!groups.TryGetValue(key, out var group))
            {
                groups.Add(key, group = new Group(key, identity));
                ordered.Add(group);
            }
            group.Registrations.Add(registration);
        }

        foreach (var package in winGetPackages
                     .Where(item => item.Id.Length > 0)
                     .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                     .Select(item => item.First())
                     .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (identities.FindManaged(package.Id) is { } managed)
            {
                var key = CatalogKey(managed.Id);
                var exact = new IdentityResolution(managed, IdentityConfidence.Exact, IdentityEvidence.WinGetId, [managed]);
                if (groups.TryGetValue(key, out var existing))
                {
                    existing.Identity = exact;
                    existing.Attach(package, WinGetCorrelation.CatalogIdentity);
                }
                else
                {
                    var added = new Group(key, exact);
                    added.Attach(package, WinGetCorrelation.ExportOnly);
                    groups.Add(key, added);
                    ordered.Add(added);
                }
                continue;
            }

            var candidates = ordered.Where(group => group.WinGet is null && group.Registrations.Count > 0 &&
                group.Identity.Application?.Management != ApplicationManagement.ManagedWinGet).ToArray();
            var correlated = Unique(candidates, group => NameCorrelates(package.Id, group));
            var correlation = WinGetCorrelation.RegisteredName;
            if (correlated is null)
            {
                correlated = Unique(candidates, group => VersionCorrelates(package, group));
                correlation = WinGetCorrelation.RegisteredVersion;
            }
            if (correlated is not null)
            {
                correlated.Attach(package, correlation);
                continue;
            }
            var standalone = new Group("winget:" + package.Id.ToLowerInvariant(), IdentityResolution.Unidentified);
            standalone.Attach(package, WinGetCorrelation.ExportOnly);
            groups.Add(standalone.Key, standalone);
            ordered.Add(standalone);
        }

        var applications = ordered.Select(ToObserved)
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ObservationKey, StringComparer.Ordinal)
            .ToArray();
        var detail = sourceDetail.Length > 0 ? sourceDetail : (registryQuality, winGetQuality) switch
        {
            (EvidenceQuality.Complete, EvidenceQuality.Complete) => "Windows installed-app registrations and WinGet identities were read completely.",
            (EvidenceQuality.Complete, _) => "Windows installed-app registrations were read completely; WinGet identities are incomplete.",
            _ => "Some Windows installed-app registrations couldn't be read; this inventory may be incomplete."
        };
        return new WorkstationInventory(capturedAtUtc, machine, new InventorySourceQuality(registryQuality, winGetQuality, detail), applications);
    }

    private static string CatalogKey(string catalogId) => "catalog:" + catalogId.ToLowerInvariant();

    private static string GroupKey(UninstallRegistration registration, IdentityResolution identity)
    {
        if (identity.IsConfident) return CatalogKey(identity.Application!.Id);
        if (registration.MsiUpgradeCode.Length > 0) return "msi:" + registration.MsiUpgradeCode.ToUpperInvariant();
        var name = ApplicationNames.CompactKey(registration.DisplayName);
        // Only exact duplicates merge: one product registered in two views or scopes. Side-by-side versions stay apart.
        return name.Length == 0
            ? $"key:{registration.View}|{registration.KeyName.ToLowerInvariant()}"
            : $"name:{name}|{ApplicationNames.PublisherKey(registration.Publisher)}|{registration.DisplayVersion.Trim().ToLowerInvariant()}";
    }

    private static Group? Unique(IReadOnlyList<Group> candidates, Func<Group, bool> predicate)
    {
        // A listed application outranks a hidden component that happens to share its name or version.
        var matches = candidates.Where(predicate).ToArray();
        var listed = matches.Where(group => group.HasListedRegistration).ToArray();
        if (listed.Length == 1) return listed[0];
        return listed.Length == 0 && matches.Length == 1 ? matches[0] : null;
    }

    private static bool NameCorrelates(string winGetId, Group group)
    {
        var segments = winGetId.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return false;
        var whole = ApplicationNames.Compact(string.Concat(segments));
        var product = ApplicationNames.Compact(string.Concat(segments.Skip(1)));
        var last = ApplicationNames.Compact(segments[^1]);
        return group.Registrations.Any(registration =>
        {
            var name = ApplicationNames.CompactKey(registration.DisplayName);
            if (name.Length < 3) return false;
            if (name == whole) return true;
            return (name == product || name == last) && PublisherMatchesSegment(registration.Publisher, segments[0]);
        });
    }

    private static bool VersionCorrelates(WinGetPackageEvidence package, Group group)
    {
        var version = package.Version.Trim();
        var publisherSegment = package.Id.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (version.Length < 3 || !version.Contains('.', StringComparison.Ordinal)) return false;
        return group.Registrations.Any(registration =>
            PublisherMatchesSegment(registration.Publisher, publisherSegment) &&
            (SameVersion(registration.DisplayVersion, version) || ContainsVersion(registration.DisplayName, version)));
    }

    private static bool PublisherMatchesSegment(string publisher, string segment)
    {
        var compactSegment = ApplicationNames.Compact(segment);
        var lead = ApplicationNames.PublisherLead(publisher);
        var key = ApplicationNames.PublisherKey(publisher);
        return compactSegment.Length >= 2 && lead.Length >= 3 &&
            (compactSegment == lead || compactSegment.StartsWith(lead, StringComparison.Ordinal) || key.StartsWith(compactSegment, StringComparison.Ordinal));
    }

    internal static bool SameVersion(string left, string right)
    {
        var a = left.Trim();
        var b = right.Trim();
        if (a.Length == 0 || b.Length == 0) return false;
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
        return VersionValue.TryParse(a, out var first) && VersionValue.TryParse(b, out var second) && first.CompareTo(second) == 0;
    }

    private static bool ContainsVersion(string displayName, string version)
    {
        for (var index = displayName.IndexOf(version, StringComparison.Ordinal); index >= 0;
             index = displayName.IndexOf(version, index + 1, StringComparison.Ordinal))
        {
            var before = index == 0 ? ' ' : displayName[index - 1];
            var afterIndex = index + version.Length;
            var after = afterIndex >= displayName.Length ? ' ' : displayName[afterIndex];
            if (!char.IsDigit(before) && before != '.' && !char.IsDigit(after)) return true;
        }
        return false;
    }

    private ObservedApplication ToObserved(Group group)
    {
        var registrations = group.Registrations
            .OrderBy(item => item.SystemComponent || item.IsUpdate)
            .ThenBy(item => item.Hive)
            .ThenBy(item => item.KeyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var primary = registrations.FirstOrDefault();
        var catalogApplication = group.Identity.IsConfident ? group.Identity.Application : null;
        var versionPattern = catalogApplication?.Package.DetectionVersionPattern ?? string.Empty;
        var versions = registrations
            .Select(item => catalogApplication is null
                ? ApplicationNames.Clean(item.DisplayVersion)
                : CatalogRegistryDetection.InstalledVersion(item.DisplayVersion, item.DisplayName, versionPattern) is { Length: > 0 } version
                    ? version : ApplicationNames.Clean(item.DisplayVersion))
            .Append(group.WinGet?.Version ?? string.Empty)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var displayVersion = versions.Length == 0 ? string.Empty : versions.Aggregate((left, right) =>
            VersionValue.TryParse(left, out var a) && VersionValue.TryParse(right, out var b) && b.CompareTo(a) > 0 ? right : left);
        var publisher = registrations.Select(item => item.Publisher).FirstOrDefault(value => value.Length > 0)
            ?? catalogApplication?.Vendor ?? string.Empty;
        var scope = registrations.Any(item => item.Hive != UninstallHive.User) ? InstallScope.Machine
            : registrations.Length > 0 ? InstallScope.User : InstallScope.Unknown;
        var architecture = string.Join(", ", registrations
            .Select(item => ApplicationNames.Architecture(item.DisplayName, item.Hive))
            .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal));
        var displayName = primary?.DisplayName ?? catalogApplication?.Name ?? group.WinGet?.Id ?? string.Empty;
        return new ObservedApplication(
            group.Key,
            displayName,
            displayVersion,
            versions,
            publisher,
            scope,
            architecture,
            registrations,
            group.WinGet,
            group.Correlation,
            group.Identity,
            registrations.Select(item => item.MsiUpgradeCode).FirstOrDefault(value => value.Length > 0) ?? string.Empty,
            Classify(group, registrations, displayName));
    }

    private static MigrationRelevance Classify(Group group, IReadOnlyList<UninstallRegistration> registrations, string displayName)
    {
        // A recognized catalog application is migration-relevant even when its installer hides the registration.
        if (group.Identity.IsConfident) return MigrationRelevance.Application;
        if (registrations.Count > 0)
        {
            var visible = registrations.Where(item => !item.SystemComponent).ToArray();
            if (visible.Length == 0) return MigrationRelevance.SystemComponent;
            if (visible.All(item => item.IsUpdate)) return MigrationRelevance.Update;
        }
        return ApplicationComponentRules.IsSupportComponent(displayName, group.WinGet?.Id ?? string.Empty)
            ? MigrationRelevance.SupportComponent
            : MigrationRelevance.Application;
    }

    private sealed class Group(string key, IdentityResolution identity)
    {
        public string Key { get; } = key;
        public IdentityResolution Identity { get; set; } = identity;
        public List<UninstallRegistration> Registrations { get; } = [];
        public WinGetPackageEvidence? WinGet { get; private set; }
        public WinGetCorrelation Correlation { get; private set; }
        public bool HasListedRegistration => Registrations.Any(item => !item.SystemComponent && !item.IsUpdate);

        public void Attach(WinGetPackageEvidence package, WinGetCorrelation correlation)
        {
            WinGet = package;
            Correlation = correlation;
        }
    }
}
