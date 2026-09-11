using System.Text.Json;
using System.Text.Json.Serialization;

namespace AVWorkstationToolkit.Domain.Catalog;

/// <summary>Stable, descriptive identity for a hardware family. It does not grant software applicability.</summary>
public readonly record struct HardwareFamilyId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Stable, descriptive identity for an exact hardware model. It does not grant software applicability.</summary>
public readonly record struct HardwareModelId(string Value)
{
    public override string ToString() => Value;
}

public enum HardwareDeviceCategory
{
    ControlProcessor,
    AudioDsp,
    Display,
    Amplifier,
    Camera,
    AvOverIp,
    AvInterface,
    SignalDistribution,
    InstalledMicrophone,
    WirelessPresentation,
    ControlPanel,
    PowerDistribution,
    Wireless,
    Intercom,
    Other
}

/// <summary>Describes catalog coverage only; only DeviceSoftwareRelation can state that software applies.</summary>
public enum HardwareCoverageState
{
    VerifiedSoftwareRelationships,
    Unresolved,
    FamilyOnly
}

public sealed record HardwareFamily(
    HardwareFamilyId Id,
    string Manufacturer,
    HardwareDeviceCategory Category,
    string Name,
    IReadOnlyList<string> Aliases,
    Lifecycle Lifecycle,
    HardwareCoverageState CoverageState,
    string CompatibilityDeviceFamilyId);

public sealed record HardwareModel(
    HardwareModelId Id,
    HardwareFamilyId FamilyId,
    string Name,
    IReadOnlyList<string> Aliases,
    Lifecycle Lifecycle,
    HardwareCoverageState CoverageState);

public sealed record HardwareCategoryCoverage(
    HardwareDeviceCategory Category,
    int Families,
    int Models,
    int VerifiedModels,
    int UnresolvedModels,
    int FamilyOnlyCoverage);

public sealed record HardwareCoverageSummary(
    int Families,
    int Models,
    int Aliases,
    int VerifiedModels,
    int UnresolvedModels,
    int FamilyOnlyCoverage,
    IReadOnlyList<HardwareCategoryCoverage> Categories);

/// <summary>
/// Read-only hardware identity and coverage catalog. DeviceSoftwareRelation remains the only software-applicability
/// authority; this catalog intentionally contains no package, provider, delivery, credential, or execution fields.
/// </summary>
public sealed class HardwareIdentityCatalog
{
    private readonly IReadOnlyDictionary<HardwareFamilyId, HardwareFamily> familiesById;
    private readonly IReadOnlyDictionary<HardwareModelId, HardwareModel> modelsById;

    public HardwareIdentityCatalog(IEnumerable<HardwareFamily> families, IEnumerable<HardwareModel> models)
    {
        ArgumentNullException.ThrowIfNull(families);
        ArgumentNullException.ThrowIfNull(models);
        Families = families.ToArray();
        Models = models.ToArray();
        ValidateFamilies(Families);
        ValidateModels(Models, Families);
        familiesById = Families.ToDictionary(item => item.Id);
        modelsById = Models.ToDictionary(item => item.Id);
    }

    public IReadOnlyList<HardwareFamily> Families { get; }
    public IReadOnlyList<HardwareModel> Models { get; }

    public HardwareFamily GetRequiredFamily(HardwareFamilyId id) =>
        familiesById.TryGetValue(id, out var family) ? family : throw new KeyNotFoundException($"Hardware family is not known: {id}.");

    public HardwareModel GetRequiredModel(HardwareModelId id) =>
        modelsById.TryGetValue(id, out var model) ? model : throw new KeyNotFoundException($"Hardware model is not known: {id}.");

    public HardwareCoverageSummary GetCoverageSummary()
    {
        var categories = Families.GroupBy(family => family.Category).OrderBy(group => group.Key)
            .Select(group =>
            {
                var familyIds = group.Select(family => family.Id).ToHashSet();
                var models = Models.Where(model => familyIds.Contains(model.FamilyId)).ToArray();
                return new HardwareCategoryCoverage(
                    group.Key,
                    group.Count(),
                    models.Length,
                    models.Count(model => model.CoverageState == HardwareCoverageState.VerifiedSoftwareRelationships),
                    models.Count(model => model.CoverageState == HardwareCoverageState.Unresolved),
                    group.Count(family => family.CoverageState == HardwareCoverageState.FamilyOnly));
            }).ToArray();
        return new HardwareCoverageSummary(
            Families.Count,
            Models.Count,
            Families.Sum(family => family.Aliases.Count) + Models.Sum(model => model.Aliases.Count),
            Models.Count(model => model.CoverageState == HardwareCoverageState.VerifiedSoftwareRelationships),
            Models.Count(model => model.CoverageState == HardwareCoverageState.Unresolved),
            Families.Count(family => family.CoverageState == HardwareCoverageState.FamilyOnly),
            categories);
    }

    private static void ValidateFamilies(IReadOnlyList<HardwareFamily> families)
    {
        if (families.Count == 0) throw new CatalogValidationException("Hardware identity catalog contains no families.");
        EnsureUnique(families, family => family.Id.Value, "hardware family ID");
        EnsureUnique(families, family => NormalizeLookup(family.Name), "hardware family name");
        foreach (var family in families)
        {
            ValidateIdentifier(family.Id.Value, "HardwareFamily.Id");
            ValidateText(family.Manufacturer, "HardwareFamily.Manufacturer", 128);
            ValidateText(family.Name, "HardwareFamily.Name", 256);
            ValidateTokens(family.Aliases, "HardwareFamily.Aliases");
            ValidateIdentifier(family.CompatibilityDeviceFamilyId, "HardwareFamily.CompatibilityDeviceFamilyId", allowEmpty: family.CoverageState == HardwareCoverageState.Unresolved);
            if (family.CoverageState == HardwareCoverageState.FamilyOnly && family.CompatibilityDeviceFamilyId.Length == 0)
                throw new CatalogValidationException($"Hardware family '{family.Id}' with FamilyOnly coverage requires a compatibility-family identity.");
        }
    }

    private static void ValidateModels(IReadOnlyList<HardwareModel> models, IReadOnlyList<HardwareFamily> families)
    {
        EnsureUnique(models, model => model.Id.Value, "hardware model ID");
        var familyIds = families.Select(family => family.Id).ToHashSet();
        var lookupTerms = new Dictionary<string, HardwareModelId>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        {
            ValidateIdentifier(model.Id.Value, "HardwareModel.Id");
            if (!familyIds.Contains(model.FamilyId)) throw new CatalogValidationException($"Hardware model '{model.Id}' references an unknown family '{model.FamilyId}'.");
            if (model.CoverageState == HardwareCoverageState.FamilyOnly)
                throw new CatalogValidationException($"Hardware model '{model.Id}' cannot use FamilyOnly coverage.");
            ValidateText(model.Name, "HardwareModel.Name", 256);
            ValidateTokens(model.Aliases, "HardwareModel.Aliases");
            foreach (var term in new[] { model.Name }.Concat(model.Aliases))
            {
                var normalized = NormalizeLookup(term);
                if (lookupTerms.TryGetValue(normalized, out var existing) && existing != model.Id)
                    throw new CatalogValidationException($"Hardware model lookup term '{term}' identifies multiple models.");
                lookupTerms[normalized] = model.Id;
            }
        }
    }

    private static void EnsureUnique<T>(IEnumerable<T> values, Func<T, string> key, string description)
    {
        var duplicate = values.GroupBy(key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new CatalogValidationException($"Hardware identity catalog contains duplicate {description} '{duplicate.Key}'.");
    }

    private static void ValidateIdentifier(string value, string field, bool allowEmpty = false)
    {
        if (allowEmpty && string.IsNullOrEmpty(value)) return;
        ValidateText(value, field, 128);
        if (!CompatibilityCatalogParser.IdentifierPattern.IsMatch(value)) throw new CatalogValidationException($"{field} is invalid.");
    }

    private static void ValidateTokens(IReadOnlyList<string>? tokens, string field)
    {
        if (tokens is null) throw new CatalogValidationException($"{field} is required.");
        EnsureUnique(tokens, NormalizeLookup, field);
        foreach (var token in tokens) ValidateText(token, field, 128);
    }

    private static void ValidateText(string? value, string field, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            throw new CatalogValidationException($"{field} is invalid.");
    }

    private static string NormalizeLookup(string value) => string.Concat(value.Trim().Where(character => !char.IsWhiteSpace(character) && character is not '-' and not '_'));
}

public sealed class HardwareIdentityCatalogParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    public HardwareIdentityCatalog Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
            RejectDuplicateProperties(document.RootElement);
            var raw = JsonSerializer.Deserialize<HardwareDocument>(json, JsonOptions) ?? throw new CatalogValidationException("Hardware identity catalog is empty.");
            if (raw.SchemaVersion != 1 || raw.Families is null || raw.Models is null)
                throw new CatalogValidationException("Hardware identity catalog requires schema version 1 plus Families and Models arrays.");
            return new HardwareIdentityCatalog(raw.Families.Select(ParseFamily), raw.Models.Select(ParseModel));
        }
        catch (JsonException exception)
        {
            throw new CatalogValidationException($"Hardware identity catalog JSON is invalid: {exception.Message}");
        }
    }

    private static HardwareFamily ParseFamily(HardwareFamilyRaw raw) => new(
        new HardwareFamilyId(Required(raw.Id, "HardwareFamily.Id")),
        Required(raw.Manufacturer, "HardwareFamily.Manufacturer"),
        ParseEnum<HardwareDeviceCategory>(Required(raw.Category, "HardwareFamily.Category"), "HardwareFamily.Category"),
        Required(raw.Name, "HardwareFamily.Name"),
        Strings(raw.Aliases, "HardwareFamily.Aliases"),
        ParseEnum<Lifecycle>(Required(raw.Lifecycle, "HardwareFamily.Lifecycle"), "HardwareFamily.Lifecycle"),
        ParseEnum<HardwareCoverageState>(Required(raw.CoverageState, "HardwareFamily.CoverageState"), "HardwareFamily.CoverageState"),
        Optional(raw.CompatibilityDeviceFamilyId));

    private static HardwareModel ParseModel(HardwareModelRaw raw) => new(
        new HardwareModelId(Required(raw.Id, "HardwareModel.Id")),
        new HardwareFamilyId(Required(raw.FamilyId, "HardwareModel.FamilyId")),
        Required(raw.Name, "HardwareModel.Name"),
        Strings(raw.Aliases, "HardwareModel.Aliases"),
        ParseEnum<Lifecycle>(Required(raw.Lifecycle, "HardwareModel.Lifecycle"), "HardwareModel.Lifecycle"),
        ParseEnum<HardwareCoverageState>(Required(raw.CoverageState, "HardwareModel.CoverageState"), "HardwareModel.CoverageState"));

    private static T ParseEnum<T>(string value, string field) where T : struct, Enum
    {
        if (!Enum.TryParse<T>(value, false, out var parsed) || !Enum.IsDefined(parsed))
            throw new CatalogValidationException($"{field} contains unsupported value '{value}'.");
        return parsed;
    }

    private static string Required(string? value, string field) => !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new CatalogValidationException($"{field} is required.");
    private static string Optional(string? value) => value?.Trim() ?? string.Empty;
    private static IReadOnlyList<string> Strings(List<string>? values, string field) => values is null
        ? throw new CatalogValidationException($"{field} is required.")
        : values.Select(value => value?.Trim() ?? throw new CatalogValidationException($"{field} cannot contain null.")).ToArray();

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new CatalogValidationException($"Hardware identity catalog repeats JSON property '{property.Name}'.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class HardwareDocument
    {
        public int SchemaVersion { get; init; }
        public List<HardwareFamilyRaw>? Families { get; init; }
        public List<HardwareModelRaw>? Models { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class HardwareFamilyRaw
    {
        public string? Id { get; init; }
        public string? Manufacturer { get; init; }
        public string? Category { get; init; }
        public string? Name { get; init; }
        public List<string>? Aliases { get; init; }
        public string? Lifecycle { get; init; }
        public string? CoverageState { get; init; }
        public string? CompatibilityDeviceFamilyId { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class HardwareModelRaw
    {
        public string? Id { get; init; }
        public string? FamilyId { get; init; }
        public string? Name { get; init; }
        public List<string>? Aliases { get; init; }
        public string? Lifecycle { get; init; }
        public string? CoverageState { get; init; }
    }
}
