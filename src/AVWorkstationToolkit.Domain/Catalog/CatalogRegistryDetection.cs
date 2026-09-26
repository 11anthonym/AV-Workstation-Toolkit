using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AVWorkstationToolkit.Domain.Versions;

namespace AVWorkstationToolkit.Domain.Catalog;

/// <summary>
/// The single rule that recognizes a catalog application from a Windows uninstall registration: the record's validated
/// display-name pattern, and a validated version taken from DisplayVersion or, when that is unusable, from the display
/// name. The catalog plan's external inventory matcher and the workstation inventory both detect through this rule, so a
/// detector correction reaches every feature at once.
/// </summary>
public static class CatalogRegistryDetection
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly ConcurrentDictionary<string, Regex> Patterns = new(StringComparer.Ordinal);

    public static bool MatchesDisplayName(string pattern, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        return Compile(pattern).IsMatch(displayName ?? string.Empty);
    }

    /// <summary>The validated installed version, or an empty string when neither source yields one.</summary>
    public static string InstalledVersion(string displayVersion, string displayName, string versionPattern)
    {
        var version = displayVersion ?? string.Empty;
        if (!VersionValue.TryParse(version, out _) && !string.IsNullOrWhiteSpace(versionPattern))
        {
            var extracted = Compile(versionPattern).Match(displayName ?? string.Empty);
            version = extracted.Success ? extracted.Groups["Version"].Value : string.Empty;
        }
        return VersionValue.TryParse(version, out _) ? version : string.Empty;
    }

    private static Regex Compile(string pattern) =>
        Patterns.GetOrAdd(pattern, value => new Regex(value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout));
}
