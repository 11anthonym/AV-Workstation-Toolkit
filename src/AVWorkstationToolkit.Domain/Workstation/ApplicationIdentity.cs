using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Domain.Workstation;

/// <summary>How a catalogued application can reach a workstation. Derived from catalog policy, never from an inventory.</summary>
public enum ApplicationManagement { ManagedWinGet, VendorHandoff, InventoryOnly, AwarenessOnly }

/// <summary>How strongly evidence ties an application to one catalog record.</summary>
public enum IdentityConfidence
{
    /// <summary>An exact catalog ID, or the exact WinGet package ID of a managed record.</summary>
    Exact,
    /// <summary>A curated catalog detector matched the display name Windows registered.</summary>
    Catalog,
    /// <summary>Only a normalized-name comparison suggests a record. It needs review and never authorizes anything.</summary>
    Probable,
    /// <summary>More than one catalog record claims the evidence. It needs review and never authorizes anything.</summary>
    Ambiguous,
    /// <summary>No catalog record claims the evidence.</summary>
    Unidentified
}

public enum IdentityEvidence { None, CatalogId, WinGetId, CatalogDetector, NameSimilarity }

/// <summary>One catalog record viewed as an application identity: the single application database every feature shares.</summary>
public sealed class KnownApplication
{
    private readonly IReadOnlyList<string> detectors;

    internal KnownApplication(PackageDefinition package, IReadOnlyList<string> detectors)
    {
        Package = package;
        this.detectors = detectors;
        Management = package.Authority switch
        {
            CatalogAuthority.ManagedWinGet => ApplicationManagement.ManagedWinGet,
            CatalogAuthority.AwarenessOnly => ApplicationManagement.AwarenessOnly,
            _ => package.DeploymentClass == DeploymentClass.InventoryOnly ? ApplicationManagement.InventoryOnly : ApplicationManagement.VendorHandoff
        };
    }

    public PackageDefinition Package { get; }
    public string Id => Package.Id;
    public string Name => Package.Name;
    public string Vendor => Package.Vendor;
    public ApplicationManagement Management { get; }

    /// <summary>The exact WinGet package ID. Only managed WinGet records have one; the managed catalog, not this value, grants authority.</summary>
    public string WinGetId => Management == ApplicationManagement.ManagedWinGet ? Package.Id : string.Empty;

    public bool HasRegistryDetector => detectors.Count > 0;

    /// <summary>Whether a workstation scan can observe this application at all.</summary>
    public bool Detectable => Management == ApplicationManagement.ManagedWinGet || HasRegistryDetector;

    public bool MatchesDisplayName(string displayName) =>
        detectors.Any(pattern => CatalogRegistryDetection.MatchesDisplayName(pattern, displayName));
}

/// <summary>The catalog identity evidence supports. <see cref="Application"/> is set only for a confident identity.</summary>
public sealed record IdentityResolution(
    KnownApplication? Application,
    IdentityConfidence Confidence,
    IdentityEvidence Evidence,
    IReadOnlyList<KnownApplication> Candidates)
{
    public static IdentityResolution Unidentified { get; } = new(null, IdentityConfidence.Unidentified, IdentityEvidence.None, []);

    /// <summary>Only exact identities and curated catalog detectors are strong enough to act on.</summary>
    public bool IsConfident => Application is not null && Confidence is IdentityConfidence.Exact or IdentityConfidence.Catalog;

    public string Describe() => Evidence switch
    {
        IdentityEvidence.CatalogId => "Catalog application ID",
        IdentityEvidence.WinGetId => "Exact WinGet package ID",
        IdentityEvidence.CatalogDetector when Confidence == IdentityConfidence.Catalog => "Catalog detector for the registered name",
        IdentityEvidence.CatalogDetector => $"Registered name matches {Candidates.Count} catalog applications",
        IdentityEvidence.NameSimilarity when Confidence == IdentityConfidence.Probable => $"Name resembles {Candidates[0].Name}",
        IdentityEvidence.NameSimilarity => $"Name resembles {Candidates.Count} catalog applications",
        _ => "Not in the catalog"
    };
}

/// <summary>
/// Resolves application identity against the loaded catalogs. Managed records are recognized by exact WinGet ID and,
/// for registry evidence, by the curated <see cref="ManagedApplicationDetectors"/>; external and awareness records by
/// their catalog registry detectors. Name similarity is reported as <see cref="IdentityConfidence.Probable"/> and never
/// yields an application.
/// </summary>
public sealed class ApplicationIdentityCatalog
{
    private readonly Dictionary<string, KnownApplication> byId;
    private readonly Dictionary<string, List<KnownApplication>> byCompactName;
    private readonly KnownApplication[] withDetectors;

    public ApplicationIdentityCatalog(PackageCatalog catalog, IReadOnlyDictionary<string, string>? managedDetectors = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        managedDetectors ??= ManagedApplicationDetectors.Patterns;
        var applications = catalog.Items.Select(package =>
        {
            var patterns = new List<string>();
            if (package.Provider == ProviderKind.External && package.DetectionMode == DetectionMode.Registry &&
                package.DetectionDisplayNamePattern.Length > 0)
                patterns.Add(package.DetectionDisplayNamePattern);
            if (package.Authority == CatalogAuthority.ManagedWinGet && managedDetectors.TryGetValue(package.Id, out var managed))
                patterns.Add(managed);
            return new KnownApplication(package, patterns.ToArray());
        }).ToArray();
        Applications = applications;
        byId = applications.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        withDetectors = applications.Where(item => item.HasRegistryDetector).ToArray();
        byCompactName = new Dictionary<string, List<KnownApplication>>(StringComparer.Ordinal);
        foreach (var application in applications)
        {
            var key = ApplicationNames.CompactKey(application.Name);
            if (key.Length < 3) continue;
            if (!byCompactName.TryGetValue(key, out var list)) byCompactName.Add(key, list = []);
            list.Add(application);
        }
    }

    public IReadOnlyList<KnownApplication> Applications { get; }

    public KnownApplication? Find(string? id) =>
        !string.IsNullOrWhiteSpace(id) && byId.TryGetValue(id.Trim(), out var application) ? application : null;

    public KnownApplication? FindManaged(string? winGetId) =>
        Find(winGetId) is { Management: ApplicationManagement.ManagedWinGet } application ? application : null;

    /// <summary>Resolves the strongest available evidence: a catalog ID, then an exact managed WinGet ID, then the display name.</summary>
    public IdentityResolution Resolve(string? catalogId, string? winGetId, string? displayName)
    {
        if (Find(catalogId) is { } known) return new(known, IdentityConfidence.Exact, IdentityEvidence.CatalogId, [known]);
        if (FindManaged(winGetId) is { } managed) return new(managed, IdentityConfidence.Exact, IdentityEvidence.WinGetId, [managed]);
        return ResolveDisplayName(displayName);
    }

    public IdentityResolution ResolveDisplayName(string? displayName)
    {
        var name = ApplicationNames.Clean(displayName);
        if (name.Length == 0) return IdentityResolution.Unidentified;
        var detected = withDetectors.Where(application => application.MatchesDisplayName(name)).ToArray();
        if (detected.Length == 1) return new(detected[0], IdentityConfidence.Catalog, IdentityEvidence.CatalogDetector, detected);
        if (detected.Length > 1) return new(null, IdentityConfidence.Ambiguous, IdentityEvidence.CatalogDetector, detected);
        var key = ApplicationNames.CompactKey(name);
        if (key.Length >= 3 && byCompactName.TryGetValue(key, out var similar))
            return new(null, similar.Count == 1 ? IdentityConfidence.Probable : IdentityConfidence.Ambiguous,
                IdentityEvidence.NameSimilarity, similar.ToArray());
        return IdentityResolution.Unidentified;
    }
}
