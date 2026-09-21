using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AVWorkstationToolkit.Domain.Catalog;

public sealed record ManagedCatalogFileHash(string Sha256);

/// <summary>Signed metadata for the one approved managed WinGet catalog payload.</summary>
public sealed record ManagedCatalogBundleManifest(
    string CatalogId,
    string CatalogVersion,
    long Revision,
    long PreviousRevision,
    int SchemaVersion,
    DateTimeOffset CreatedUtc,
    string MinimumAppVersion,
    string SigningKeyId,
    int PackageCount,
    IReadOnlyDictionary<string, ManagedCatalogFileHash> Files);

public static class ManagedCatalogBundleNames
{
    public const string CatalogId = "avwt-managed";
    public const string Payload = "managed-applications.json";
    public const string Manifest = "managed-catalog-manifest.json";
    public const string Signature = "managed-catalog-manifest.sig";
    public const string ChannelMetadata = "managed-catalog-channel.json";
    public const string ChannelSignature = "managed-catalog-channel.sig";
    public const string BundleExtension = ".avwtmanaged";
    public const string BundleNamePrefix = "AVWT-Managed-Catalog-";

    // This phase deliberately preserves the reviewed application policy as an exact contract.
    // A later runtime phase may move its authority into compiled runtime policy.
    public const string RequiredForbiddenPattern =
        "(?i)BitLocker|CrowdStrike|Falcon|Defender|Sentinel|Sophos|McAfee|Symantec|CarbonBlack|Cylance|Forti(Client)?|AnyConnect|SecureClient|Intune|CompanyPortal|SCCM|TeamViewer|TightVNC";

    public static readonly IReadOnlySet<string> BundleFiles = new HashSet<string>(StringComparer.Ordinal)
    {
        Payload,
        Manifest,
        Signature
    };
}

public sealed class ManagedCatalogManifestParser
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

    public ManagedCatalogBundleManifest Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new CatalogValidationException("The managed catalog manifest is empty.");
        ManifestDocument raw;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            RejectDuplicateProperties(document.RootElement);
            raw = JsonSerializer.Deserialize<ManifestDocument>(json, Options)
                ?? throw new CatalogValidationException("The managed catalog manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new CatalogValidationException($"The managed catalog manifest JSON is invalid: {exception.Message}");
        }

        if (!string.Equals(raw.CatalogId, ManagedCatalogBundleNames.CatalogId, StringComparison.Ordinal))
            throw new CatalogValidationException("Managed catalog manifest has an unsupported CatalogId.");
        if (raw.SchemaVersion != 1)
            throw new CatalogValidationException("Managed catalog manifest schema is unsupported.");
        if (raw.Revision <= 0 || raw.PreviousRevision < 0 || raw.PreviousRevision >= raw.Revision)
            throw new CatalogValidationException("Managed catalog revision chain is invalid.");
        RequireVersion(raw.CatalogVersion, "CatalogVersion");
        RequireVersion(raw.MinimumAppVersion, "MinimumAppVersion");
        RequireText(raw.SigningKeyId, "SigningKeyId", 128);
        if (raw.CreatedUtc == default || raw.CreatedUtc.Offset != TimeSpan.Zero)
            throw new CatalogValidationException("Managed catalog CreatedUtc must use UTC.");
        if (raw.PackageCount <= 0)
            throw new CatalogValidationException("Managed catalog PackageCount must be positive.");
        if (raw.Files is null || raw.Files.Count != 1 || !raw.Files.ContainsKey(ManagedCatalogBundleNames.Payload))
            throw new CatalogValidationException("Managed catalog manifest must hash exactly managed-applications.json.");
        var payload = raw.Files[ManagedCatalogBundleNames.Payload];
        if (payload is null || !HashPattern.IsMatch(payload.Sha256 ?? string.Empty))
            throw new CatalogValidationException("Managed catalog payload hash is invalid.");

        return new(
            raw.CatalogId!, raw.CatalogVersion!, raw.Revision, raw.PreviousRevision, raw.SchemaVersion,
            raw.CreatedUtc, raw.MinimumAppVersion!, raw.SigningKeyId!, raw.PackageCount,
            new Dictionary<string, ManagedCatalogFileHash>(StringComparer.Ordinal)
            {
                [ManagedCatalogBundleNames.Payload] = new(payload.Sha256!.ToUpperInvariant())
            });
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new CatalogValidationException($"Managed catalog manifest repeats JSON property '{property.Name}'.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static void RequireVersion(string? value, string field)
    {
        RequireText(value, field, 32);
        if (!VersionPattern.IsMatch(value!)) throw new CatalogValidationException($"Managed catalog {field} is invalid.");
    }

    private static void RequireText(string? value, string field, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new CatalogValidationException($"Managed catalog {field} is invalid.");
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ManifestDocument
    {
        public string? CatalogId { get; init; }
        public string? CatalogVersion { get; init; }
        public long Revision { get; init; }
        public long PreviousRevision { get; init; }
        public int SchemaVersion { get; init; }
        public DateTimeOffset CreatedUtc { get; init; }
        public string? MinimumAppVersion { get; init; }
        public string? SigningKeyId { get; init; }
        public int PackageCount { get; init; }
        public Dictionary<string, FileDocument?>? Files { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class FileDocument
    {
        public string? Sha256 { get; init; }
    }
}
