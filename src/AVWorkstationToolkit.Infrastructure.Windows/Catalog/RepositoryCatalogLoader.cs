using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Infrastructure.Windows.Catalog;

/// <summary>Loads the reviewed source catalogs without invoking PowerShell.</summary>
public sealed class RepositoryCatalogLoader
{
    private static readonly JsonSerializerOptions ManagedCatalogJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public PackageCatalog Load(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        var managedPath = Path.Combine(root, "manifests", "managed-applications.json");
        var externalPath = Path.Combine(root, "manifests", "external-applications.json");
        var awarenessPath = Path.Combine(root, "manifests", "commercial-av-catalog.json");
        foreach (var path in new[] { managedPath, externalPath, awarenessPath })
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Required catalog source is unavailable.", path);
        }

        var parser = new CatalogParser(DateOnly.FromDateTime(DateTime.UtcNow));
        var managedDocument = ParseManagedCatalog(File.ReadAllText(managedPath));
        var managed = parser.NormalizeManagedCatalog(managedDocument.Packages, managedDocument.ForbiddenPattern);
        var external = parser.ParseExternalCatalog(File.ReadAllText(externalPath));
        var awareness = parser.ParseExternalCatalog(File.ReadAllText(awarenessPath), CatalogAuthority.AwarenessOnly);
        return new PackageCatalog(managed.Items.Concat(external.Items).Concat(awareness.Items));
    }

    internal static ManagedCatalogData ParseManagedCatalog(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            RejectDuplicateProperties(parsed.RootElement);
            var document = JsonSerializer.Deserialize<ManagedCatalogDocument>(json, ManagedCatalogJsonOptions)
                ?? throw new InvalidDataException("Managed catalog JSON is empty.");
            if (document.SchemaVersion != 1)
                throw new InvalidDataException($"Managed catalog schema version is unsupported: {document.SchemaVersion}.");
            if (string.IsNullOrWhiteSpace(document.ForbiddenPattern))
                throw new InvalidDataException("Managed catalog ForbiddenPattern is missing.");
            if (document.Packages is null || document.Packages.Count == 0)
                throw new InvalidDataException("Managed catalog contains no package entries.");

            var packages = document.Packages.Select((item, index) => new ManagedPackageInput(
                Required(item.Profile, "Profile", index),
                Optional(item.Name),
                Required(item.Id, "Id", index),
                Optional(item.Vendor),
                Optional(item.Risk),
                Required(item.Note, "Note", index),
                Optional(item.Deployment),
                Optional(item.Maintenance),
                Optional(item.InstallerMode))).ToArray();
            return new ManagedCatalogData(document.ForbiddenPattern, packages);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Managed catalog JSON is malformed or violates its strict schema.", exception);
        }
    }

    private static string Required(string? value, string field, int index) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException($"Managed catalog package {index} is missing '{field}'.");

    private static string? Optional(string? value) => value is null ? null : value.Trim();

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException($"Managed catalog repeats JSON property '{property.Name}'.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    internal sealed record ManagedCatalogData(string ForbiddenPattern, IReadOnlyList<ManagedPackageInput> Packages);

    private sealed record ManagedCatalogDocument(
        int SchemaVersion,
        string? ForbiddenPattern,
        IReadOnlyList<ManagedCatalogPackage>? Packages);

    private sealed record ManagedCatalogPackage(
        string? Profile,
        string? Name,
        string? Id,
        string? Vendor,
        string? Risk,
        string? Note,
        string? Deployment,
        string? Maintenance,
        string? InstallerMode);
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
                    File.Exists(Path.Combine(current.FullName, "manifests", "managed-applications.json")) &&
                    File.Exists(Path.Combine(current.FullName, "manifests", "external-applications.json")))
                    return current.FullName;
                current = current.Parent;
            }
        }
        throw new DirectoryNotFoundException("A source checkout containing the AV Workstation Toolkit catalogs could not be located.");
    }
}
