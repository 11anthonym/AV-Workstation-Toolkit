using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Versions;

namespace AVWorkstationToolkit.Application.Inventory;

public sealed class ExternalInventoryMatcher
{
    public IReadOnlyList<ExternalPackageInventoryEvidence> Match(
        IEnumerable<PackageDefinition> packages,
        RegistryInventoryResult inventory)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(inventory);
        return packages.Where(package => package.Provider == ProviderKind.External)
            .Select(package => Match(package, inventory))
            .ToArray();
    }

    public ExternalPackageInventoryEvidence Match(PackageDefinition package, RegistryInventoryResult inventory)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(inventory);
        if (package.Provider != ProviderKind.External)
            throw new ArgumentException("Registry evidence can be matched only to an external catalog record.", nameof(package));

        if (package.DetectionMode == DetectionMode.None)
            return new(package.Id, true, false, string.Empty, [], InventoryQuality.NotApplicable,
                "Catalog-awareness record; no Windows installation detector is defined.");

        var matches = MatchingRecords(package, inventory);
        var versions = new List<string>();
        foreach (var match in matches)
        {
            var version = CatalogRegistryDetection.InstalledVersion(match.DisplayVersion, match.DisplayName, package.DetectionVersionPattern);
            if (version.Length > 0 && !versions.Contains(version, StringComparer.Ordinal))
                versions.Add(version);
        }

        var highest = versions.Count == 0
            ? string.Empty
            : versions.Aggregate((left, right) => VersionValue.Parse(left).CompareTo(VersionValue.Parse(right)) >= 0 ? left : right);
        var quality = matches.Count > 0 && versions.Count == 0
            ? InventoryQuality.PackageError
            : inventory.Quality switch
            {
                ProviderQuality.Complete => InventoryQuality.Complete,
                ProviderQuality.Partial => InventoryQuality.Partial,
                _ => InventoryQuality.Unavailable
            };
        var reliable = versions.Count > 0 || (matches.Count == 0 && inventory.Quality == ProviderQuality.Complete);
        var unavailableCount = inventory.Sources.Count(source => !source.Available);
        var sourceWarning = unavailableCount > 0 ? $" {unavailableCount} of {inventory.Sources.Count} registry sources unavailable." : string.Empty;
        var detail = matches.Count > 0 && versions.Count == 0
            ? "A matching installation was found, but its version could not be validated."
            : matches.Count > 0
                ? $"Detected {versions.Count} installed version(s).{sourceWarning}"
                : inventory.Quality == ProviderQuality.Unavailable
                    ? "Installation state is unavailable because all uninstall-registry sources failed."
                    : inventory.Quality == ProviderQuality.Partial
                        ? $"No matching installation was found in the available registry sources.{sourceWarning} Installation state may be incomplete."
                        : "No matching installation was found.";

        return new(package.Id, reliable, matches.Count > 0, highest, versions, quality, detail);
    }

    /// <summary>The uninstall registrations an external record's catalog detector recognizes.</summary>
    public IReadOnlyList<RegistryUninstallRecord> MatchingRecords(PackageDefinition package, RegistryInventoryResult inventory)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(inventory);
        if (package.Provider != ProviderKind.External)
            throw new ArgumentException("Registry evidence can be matched only to an external catalog record.", nameof(package));
        if (package.DetectionMode != DetectionMode.Registry || string.IsNullOrWhiteSpace(package.DetectionDisplayNamePattern))
            throw new ArgumentException("External registry detection requires a validated display-name pattern.", nameof(package));
        return inventory.Records
            .Where(record => CatalogRegistryDetection.MatchesDisplayName(package.DetectionDisplayNamePattern, record.DisplayName))
            .ToArray();
    }
}
