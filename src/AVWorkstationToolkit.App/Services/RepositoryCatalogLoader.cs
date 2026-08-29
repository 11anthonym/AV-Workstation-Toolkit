using System.IO;
using System.Text.RegularExpressions;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.App.Services;

/// <summary>Loads the reviewed source catalogs without invoking PowerShell.</summary>
public sealed partial class RepositoryCatalogLoader
{
    private static readonly HashSet<string> ManagedKeys = new(StringComparer.Ordinal)
    {
        "Profile", "Name", "Id", "Vendor", "Risk", "Note", "Deployment", "Maintenance"
    };

    public PackageCatalog Load(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        var profilesPath = Path.Combine(root, "scripts", "AppProfiles.psd1");
        var externalPath = Path.Combine(root, "manifests", "external-applications.json");
        var awarenessPath = Path.Combine(root, "manifests", "commercial-av-catalog.json");
        foreach (var path in new[] { profilesPath, externalPath, awarenessPath })
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Required catalog source is unavailable.", path);
        }

        var profileText = File.ReadAllText(profilesPath);
        var parser = new CatalogParser(DateOnly.FromDateTime(DateTime.UtcNow));
        var managed = parser.NormalizeManagedCatalog(ParseManagedPackages(profileText), ParseForbiddenPattern(profileText));
        var external = parser.ParseExternalCatalog(File.ReadAllText(externalPath));
        var awareness = parser.ParseExternalCatalog(File.ReadAllText(awarenessPath), CatalogAuthority.AwarenessOnly);
        return new PackageCatalog(managed.Items.Concat(external.Items).Concat(awareness.Items));
    }

    internal static IReadOnlyList<ManagedPackageInput> ParseManagedPackages(string dataFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFile);
        var packagesStart = dataFile.IndexOf("Packages = @(", StringComparison.Ordinal);
        var forbiddenStart = dataFile.IndexOf("ForbiddenPattern", StringComparison.Ordinal);
        if (packagesStart < 0 || forbiddenStart <= packagesStart)
            throw new InvalidDataException("Managed catalog does not contain the expected Packages and ForbiddenPattern sections.");

        var packageSection = dataFile[packagesStart..forbiddenStart];
        var results = new List<ManagedPackageInput>();
        foreach (var line in packageSection.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = ManagedEntryLine().Match(line);
            if (!match.Success) continue;
            var body = match.Groups["body"].Value;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match field in ManagedField().Matches(body))
            {
                var key = field.Groups["key"].Value;
                if (!ManagedKeys.Contains(key)) throw new InvalidDataException($"Managed catalog contains unsupported field '{key}'.");
                if (!fields.TryAdd(key, field.Groups["value"].Value.Replace("''", "'", StringComparison.Ordinal)))
                    throw new InvalidDataException($"Managed catalog entry repeats field '{key}'.");
            }
            var remainder = ManagedField().Replace(body, string.Empty).Replace(";", string.Empty, StringComparison.Ordinal).Trim();
            if (remainder.Length > 0) throw new InvalidDataException("Managed catalog entry contains syntax outside the supported deterministic data shape.");
            results.Add(new ManagedPackageInput(
                Required("Profile"),
                Optional("Name"),
                Required("Id"),
                Optional("Vendor"),
                Optional("Risk"),
                Required("Note"),
                Optional("Deployment"),
                Optional("Maintenance")));

            string Required(string key) => fields.TryGetValue(key, out var value) && value.Length > 0
                ? value : throw new InvalidDataException($"Managed catalog entry is missing '{key}'.");
            string? Optional(string key) => fields.TryGetValue(key, out var value) ? value : null;
        }
        if (results.Count == 0) throw new InvalidDataException("Managed catalog contains no package entries.");
        return results;
    }

    private static string ParseForbiddenPattern(string dataFile)
    {
        var match = ForbiddenPatternLine().Match(dataFile);
        if (!match.Success) throw new InvalidDataException("Managed catalog ForbiddenPattern is missing or unsupported.");
        return match.Groups["value"].Value.Replace("''", "'", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^\s*@\{\s*(?<body>Profile='.*)\}\s*$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ManagedEntryLine();

    [GeneratedRegex(@"(?<key>[A-Za-z][A-Za-z0-9]*)='(?<value>(?:''|[^'])*)'", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ManagedField();

    [GeneratedRegex(@"(?m)^\s*ForbiddenPattern\s*=\s*'(?<value>(?:''|[^'])*)'\s*$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ForbiddenPatternLine();
}

public static class RepositoryRootLocator
{
    public static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var current = new DirectoryInfo(Path.GetFullPath(start));
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "VERSION")) &&
                    File.Exists(Path.Combine(current.FullName, "scripts", "AppProfiles.psd1")) &&
                    File.Exists(Path.Combine(current.FullName, "manifests", "external-applications.json")))
                    return current.FullName;
                current = current.Parent;
            }
        }
        throw new DirectoryNotFoundException("A source checkout containing the AV Workstation Toolkit catalogs could not be located.");
    }
}
