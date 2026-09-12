using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AVWorkstationToolkit.Domain.Catalog;

public sealed record ReferenceCatalogCounts(
    int Manufacturers,
    int Families,
    int ExactModels,
    int ReferenceSoftwareProducts,
    int Relations);

public sealed record ReferenceCatalogFileHash(string Sha256);

/// <summary>Signed metadata for descriptive catalog bytes. It intentionally has no URI, package, or execution fields.</summary>
public sealed record ReferenceCatalogBundleManifest(
    string CatalogId,
    string CatalogVersion,
    long Revision,
    int SchemaVersion,
    int CompatibilityEpoch,
    DateTimeOffset CreatedUtc,
    long PreviousRevision,
    string MinimumAppVersion,
    string SigningKeyId,
    ReferenceCatalogCounts Counts,
    IReadOnlyDictionary<string, ReferenceCatalogFileHash> Files);

public sealed record ReferenceCatalogChangeSummary(
    int ManufacturersAdded,
    int FamiliesAdded,
    int ExactModelsAdded,
    int AliasesAdded,
    int ReferenceSoftwareAdded,
    int RelationsAdded,
    int UnresolvedToVerified,
    string Summary);

public static class ReferenceCatalogBundleNames
{
    public const string CatalogId = "avwt-reference";
    public const string Hardware = "hardware-identities.json";
    public const string Compatibility = "software-compatibility.json";
    public const string Changes = "catalog-changes.json";
    public const string Manifest = "catalog-manifest.json";
    public const string Signature = "catalog-manifest.sig";
    public static readonly IReadOnlySet<string> PayloadFiles = new HashSet<string>(StringComparer.Ordinal)
    {
        Hardware,
        Compatibility,
        Changes
    };
    public static readonly IReadOnlySet<string> BundleFiles = new HashSet<string>(PayloadFiles.Append(Manifest).Append(Signature), StringComparer.Ordinal);
}

public sealed class ReferenceCatalogManifestParser
{
    private static readonly Regex VersionPattern = new(@"^[0-9]+(?:\.[0-9]+){2,3}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex HashPattern = new(@"^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public ReferenceCatalogBundleManifest ParseManifest(string json)
    {
        var raw = DeserializeStrict<ManifestRaw>(json, "reference catalog manifest");
        if (!string.Equals(raw.CatalogId, ReferenceCatalogBundleNames.CatalogId, StringComparison.Ordinal))
            throw new CatalogValidationException("Reference catalog manifest has an unsupported CatalogId.");
        if (raw.SchemaVersion != 1 || raw.CompatibilityEpoch != 1)
            throw new CatalogValidationException("Reference catalog manifest schema or compatibility epoch is unsupported.");
        if (raw.Revision <= 0 || raw.PreviousRevision < 0 || raw.PreviousRevision >= raw.Revision)
            throw new CatalogValidationException("Reference catalog revision chain is invalid.");
        RequireVersion(raw.CatalogVersion, "CatalogVersion");
        RequireVersion(raw.MinimumAppVersion, "MinimumAppVersion");
        RequireText(raw.SigningKeyId, "SigningKeyId", 128);
        if (raw.CreatedUtc == default || raw.CreatedUtc.Offset != TimeSpan.Zero)
            throw new CatalogValidationException("Reference catalog CreatedUtc must use UTC.");
        if (raw.Counts is null || raw.Counts.Manufacturers < 1 || raw.Counts.Families < 1 || raw.Counts.ExactModels < 1 ||
            raw.Counts.ReferenceSoftwareProducts < 1 || raw.Counts.Relations < 1)
            throw new CatalogValidationException("Reference catalog counts must be positive.");
        if (raw.Files is null || raw.Files.Count != ReferenceCatalogBundleNames.PayloadFiles.Count ||
            !raw.Files.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(ReferenceCatalogBundleNames.PayloadFiles))
            throw new CatalogValidationException("Reference catalog manifest must hash exactly the approved payload files.");
        foreach (var (name, file) in raw.Files)
        {
            if (file is null || !HashPattern.IsMatch(file.Sha256 ?? string.Empty))
                throw new CatalogValidationException($"Reference catalog hash for '{name}' is invalid.");
        }
        return new(raw.CatalogId!, raw.CatalogVersion!, raw.Revision, raw.SchemaVersion, raw.CompatibilityEpoch,
            raw.CreatedUtc, raw.PreviousRevision, raw.MinimumAppVersion!, raw.SigningKeyId!,
            new(raw.Counts.Manufacturers, raw.Counts.Families, raw.Counts.ExactModels, raw.Counts.ReferenceSoftwareProducts, raw.Counts.Relations),
            raw.Files.ToDictionary(item => item.Key, item => new ReferenceCatalogFileHash(item.Value!.Sha256!.ToUpperInvariant()), StringComparer.Ordinal));
    }

    public ReferenceCatalogChangeSummary ParseChanges(string json)
    {
        var raw = DeserializeStrict<ChangesRaw>(json, "reference catalog changes");
        foreach (var value in new[] { raw.ManufacturersAdded, raw.FamiliesAdded, raw.ExactModelsAdded, raw.AliasesAdded, raw.ReferenceSoftwareAdded, raw.RelationsAdded, raw.UnresolvedToVerified })
            if (value < 0) throw new CatalogValidationException("Reference catalog change counts cannot be negative.");
        RequireText(raw.Summary, "Summary", 1024);
        return new(raw.ManufacturersAdded, raw.FamiliesAdded, raw.ExactModelsAdded, raw.AliasesAdded,
            raw.ReferenceSoftwareAdded, raw.RelationsAdded, raw.UnresolvedToVerified, raw.Summary!);
    }

    private static T DeserializeStrict<T>(string json, string description)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new CatalogValidationException($"The {description} is empty.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
            RejectDuplicateProperties(document.RootElement, description);
            return JsonSerializer.Deserialize<T>(json, Options) ?? throw new CatalogValidationException($"The {description} is empty.");
        }
        catch (JsonException exception) { throw new CatalogValidationException($"The {description} JSON is invalid: {exception.Message}"); }
    }

    private static void RejectDuplicateProperties(JsonElement element, string description)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new CatalogValidationException($"The {description} repeats JSON property '{property.Name}'.");
                RejectDuplicateProperties(property.Value, description);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item, description);
    }

    private static void RequireVersion(string? value, string field)
    {
        RequireText(value, field, 32);
        if (!VersionPattern.IsMatch(value!)) throw new CatalogValidationException($"Reference catalog {field} is invalid.");
    }

    private static void RequireText(string? value, string field, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new CatalogValidationException($"Reference catalog {field} is invalid.");
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ManifestRaw
    {
        public string? CatalogId { get; init; }
        public string? CatalogVersion { get; init; }
        public long Revision { get; init; }
        public int SchemaVersion { get; init; }
        public int CompatibilityEpoch { get; init; }
        public DateTimeOffset CreatedUtc { get; init; }
        public long PreviousRevision { get; init; }
        public string? MinimumAppVersion { get; init; }
        public string? SigningKeyId { get; init; }
        public CountsRaw? Counts { get; init; }
        public Dictionary<string, FileRaw?>? Files { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class CountsRaw
    {
        public int Manufacturers { get; init; }
        public int Families { get; init; }
        public int ExactModels { get; init; }
        public int ReferenceSoftwareProducts { get; init; }
        public int Relations { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class FileRaw { public string? Sha256 { get; init; } }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ChangesRaw
    {
        public int ManufacturersAdded { get; init; }
        public int FamiliesAdded { get; init; }
        public int ExactModelsAdded { get; init; }
        public int AliasesAdded { get; init; }
        public int ReferenceSoftwareAdded { get; init; }
        public int RelationsAdded { get; init; }
        public int UnresolvedToVerified { get; init; }
        public string? Summary { get; init; }
    }
}
