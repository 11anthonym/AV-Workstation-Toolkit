using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AVWorkstationToolkit.Domain.Versions;

namespace AVWorkstationToolkit.Domain.Catalog;

/// <summary>Stable descriptive identity for a software product; this is not a package execution identifier.</summary>
public readonly record struct SoftwareProductId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Stable identity for a product release branch, rather than an individual patch.</summary>
public readonly record struct ReleaseFamilyId(string Value)
{
    public override string ToString() => Value;
}

public enum ReleaseFamilyKind { Current, Lts, Archived, Legacy, Other }
public enum InstalledVersionEvidenceState { Observed, Unknown, Incomplete, Unavailable }
public enum DeviceSoftwarePurpose { Programming, Configuration, Commissioning, Discovery, Diagnostics, Firmware, Monitoring, LegacyService }
public enum RelationApplicability { Required, Applicable, Conditional }
public enum CompatibilityEvidenceKind { VendorProductPage, VendorReleaseNotes, VendorCompatibilityMatrix, VendorSupportArticle, PhysicalInstallRecord }
public enum CompatibilityEvidenceConfidence { VendorDocumented, PhysicalInstallVerified, Unresolved }

public sealed record Product(
    SoftwareProductId Id,
    string Vendor,
    string Name,
    Lifecycle Lifecycle,
    Uri OfficialSourceUri,
    IReadOnlyList<string> Aliases);

public sealed record ReleaseFamily(
    ReleaseFamilyId Id,
    SoftwareProductId ProductId,
    ReleaseFamilyKind Kind,
    string Branch,
    VersionValue? MinimumVersion,
    VersionValue? MaximumVersion,
    Lifecycle Lifecycle,
    Uri EvidenceUri,
    CompatibilityEvidenceKind EvidenceKind,
    string Constraints);

/// <summary>
/// One local-install observation. Non-observed states preserve unknown or incomplete inventory without implying absence.
/// </summary>
public sealed record InstalledVersion(
    string EvidenceId,
    SoftwareProductId ProductId,
    InstalledVersionEvidenceState State,
    string RawDisplayName,
    string RawVersion,
    VersionValue? NormalizedVersion,
    IReadOnlyList<string> Sources,
    string InstallLocation,
    string Architecture,
    string Detail);

/// <summary>
/// The sole software-to-device mapping authority. Both query directions are derived from these records.
/// </summary>
public sealed record DeviceSoftwareRelation(
    string Id,
    string DeviceFamilyId,
    IReadOnlyList<string> ExactModelIds,
    IReadOnlyList<string> DeviceAliases,
    SoftwareProductId ProductId,
    ReleaseFamilyId? ReleaseFamilyId,
    DeviceSoftwarePurpose Purpose,
    RelationApplicability Applicability,
    Lifecycle Lifecycle,
    Uri EvidenceUri,
    CompatibilityEvidenceKind EvidenceKind,
    CompatibilityEvidenceConfidence Confidence,
    string Constraints);

public sealed class SoftwareCompatibilityCatalog
{
    private readonly IReadOnlyDictionary<SoftwareProductId, Product> productsById;
    private readonly IReadOnlyDictionary<ReleaseFamilyId, ReleaseFamily> releaseFamiliesById;
    private readonly IReadOnlyDictionary<SoftwareProductId, IReadOnlyList<DeviceSoftwareRelation>> relationsByProduct;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<DeviceSoftwareRelation>> relationsByDeviceTerm;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<Product>> productsByLookupTerm;

    public SoftwareCompatibilityCatalog(
        IEnumerable<Product> products,
        IEnumerable<ReleaseFamily> releaseFamilies,
        IEnumerable<InstalledVersion> installedVersions,
        IEnumerable<DeviceSoftwareRelation> deviceSoftwareRelations)
    {
        ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(releaseFamilies);
        ArgumentNullException.ThrowIfNull(installedVersions);
        ArgumentNullException.ThrowIfNull(deviceSoftwareRelations);

        Products = products.ToArray();
        ReleaseFamilies = releaseFamilies.ToArray();
        InstalledVersions = installedVersions.ToArray();
        DeviceSoftwareRelations = deviceSoftwareRelations.ToArray();

        ValidateProducts(Products);
        ValidateReleaseFamilies(ReleaseFamilies, Products);
        ValidateInstalledVersions(InstalledVersions, Products);
        ValidateRelations(DeviceSoftwareRelations, Products, ReleaseFamilies);

        productsById = Products.ToDictionary(item => item.Id);
        releaseFamiliesById = ReleaseFamilies.ToDictionary(item => item.Id);
        relationsByProduct = DeviceSoftwareRelations
            .GroupBy(item => item.ProductId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<DeviceSoftwareRelation>)group.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray());
        relationsByDeviceTerm = DeviceSoftwareRelations
            .SelectMany(item => DeviceTerms(item).Select(term => (Term: term, Relation: item)))
            .GroupBy(item => item.Term, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<DeviceSoftwareRelation>)group.Select(item => item.Relation).DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(), StringComparer.OrdinalIgnoreCase);
        productsByLookupTerm = Products
            .SelectMany(item => ProductTerms(item).Select(term => (Term: term, Product: item)))
            .GroupBy(item => item.Term, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<Product>)group.Select(item => item.Product).DistinctBy(item => item.Id).OrderBy(item => item.Id.Value, StringComparer.Ordinal).ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Product> Products { get; }
    public IReadOnlyList<ReleaseFamily> ReleaseFamilies { get; }
    public IReadOnlyList<InstalledVersion> InstalledVersions { get; }
    public IReadOnlyList<DeviceSoftwareRelation> DeviceSoftwareRelations { get; }

    public Product GetRequiredProduct(SoftwareProductId id) =>
        productsById.TryGetValue(id, out var product) ? product : throw new KeyNotFoundException($"Compatibility product is not known: {id}.");

    public ReleaseFamily GetRequiredReleaseFamily(ReleaseFamilyId id) =>
        releaseFamiliesById.TryGetValue(id, out var family) ? family : throw new KeyNotFoundException($"Compatibility release family is not known: {id}.");

    public IReadOnlyList<InstalledVersion> GetInstalledVersions(SoftwareProductId productId) =>
        InstalledVersions.Where(item => item.ProductId == productId).OrderBy(item => item.EvidenceId, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<DeviceSoftwareRelation> GetDevicesForSoftware(SoftwareProductId productId) =>
        relationsByProduct.TryGetValue(productId, out var relations) ? relations : [];

    public IReadOnlyList<DeviceSoftwareRelation> GetSoftwareForDevice(string deviceOrAlias)
    {
        var lookup = NormalizeLookup(deviceOrAlias, "Device lookup");
        return relationsByDeviceTerm.TryGetValue(lookup, out var relations) ? relations : [];
    }

    public IReadOnlyList<Product> FindProducts(string productOrAlias)
    {
        var lookup = NormalizeLookup(productOrAlias, "Product lookup");
        return productsByLookupTerm.TryGetValue(lookup, out var products) ? products : [];
    }

    private static void ValidateProducts(IReadOnlyList<Product> products)
    {
        if (products.Count == 0) throw new CatalogValidationException("Compatibility catalog contains no products.");
        EnsureUnique(products, item => item.Id.Value, "product ID");
        foreach (var product in products)
        {
            ValidateId(product.Id.Value, "Product.Id");
            ValidateText(product.Vendor, "Product.Vendor", 128);
            ValidateText(product.Name, "Product.Name", 256);
            ValidateHttps(product.OfficialSourceUri, "Product.OfficialSourceUri");
            ValidateTokenList(product.Aliases, "Product.Aliases", 128, true);
        }
    }

    private static void ValidateReleaseFamilies(IReadOnlyList<ReleaseFamily> families, IReadOnlyList<Product> products)
    {
        EnsureUnique(families, item => item.Id.Value, "release-family ID");
        EnsureUnique(families, item => $"{item.ProductId.Value}\u001f{item.Kind}\u001f{item.Branch}", "release-family branch");
        var productIds = products.Select(item => item.Id).ToHashSet();
        foreach (var family in families)
        {
            ValidateId(family.Id.Value, "ReleaseFamily.Id");
            if (!productIds.Contains(family.ProductId)) throw new CatalogValidationException($"Release family '{family.Id}' references an unknown product '{family.ProductId}'.");
            ValidateText(family.Branch, "ReleaseFamily.Branch", 128);
            if (family.Kind == ReleaseFamilyKind.Other && family.Branch.Length == 0)
                throw new CatalogValidationException($"Release family '{family.Id}' with kind Other requires an evidence-backed branch.");
            if (family.MinimumVersion is { } minimum && family.MaximumVersion is { } maximum && minimum.CompareTo(maximum) > 0)
                throw new CatalogValidationException($"Release family '{family.Id}' has a minimum version greater than its maximum version.");
            ValidateHttps(family.EvidenceUri, "ReleaseFamily.EvidenceUri");
            ValidateText(family.Constraints, "ReleaseFamily.Constraints", 1024, true);
        }
    }

    private static void ValidateInstalledVersions(IReadOnlyList<InstalledVersion> versions, IReadOnlyList<Product> products)
    {
        EnsureUnique(versions, item => item.EvidenceId, "installed-version evidence ID");
        var productIds = products.Select(item => item.Id).ToHashSet();
        foreach (var version in versions)
        {
            ValidateId(version.EvidenceId, "InstalledVersion.EvidenceId");
            if (!productIds.Contains(version.ProductId)) throw new CatalogValidationException($"Installed-version evidence '{version.EvidenceId}' references an unknown product '{version.ProductId}'.");
            ValidateText(version.RawDisplayName, "InstalledVersion.RawDisplayName", 256, version.State != InstalledVersionEvidenceState.Observed);
            ValidateText(version.RawVersion, "InstalledVersion.RawVersion", 128, version.State != InstalledVersionEvidenceState.Observed);
            ValidateTokenList(version.Sources, "InstalledVersion.Sources", 128, version.State != InstalledVersionEvidenceState.Observed);
            ValidateText(version.InstallLocation, "InstalledVersion.InstallLocation", 1024, true);
            ValidateText(version.Architecture, "InstalledVersion.Architecture", 32, true);
            ValidateText(version.Detail, "InstalledVersion.Detail", 1024, true);
            if (version.State == InstalledVersionEvidenceState.Observed)
            {
                if (version.NormalizedVersion is null) throw new CatalogValidationException($"Observed installed-version evidence '{version.EvidenceId}' requires a normalized numeric version.");
                if (!VersionValue.TryParse(version.RawVersion, out var parsed) || parsed.CompareTo(version.NormalizedVersion.Value) != 0)
                    throw new CatalogValidationException($"Observed installed-version evidence '{version.EvidenceId}' has an invalid or inconsistent raw version.");
            }
            else if (version.NormalizedVersion is not null || version.RawVersion.Length != 0)
            {
                throw new CatalogValidationException($"Non-observed installed-version evidence '{version.EvidenceId}' cannot claim a version.");
            }
        }
    }

    private static void ValidateRelations(IReadOnlyList<DeviceSoftwareRelation> relations, IReadOnlyList<Product> products, IReadOnlyList<ReleaseFamily> families)
    {
        EnsureUnique(relations, item => item.Id, "device/software relation ID");
        EnsureUnique(relations, item => $"{item.DeviceFamilyId}\u001f{item.ProductId.Value}\u001f{item.ReleaseFamilyId?.Value ?? string.Empty}\u001f{item.Purpose}", "device/software relation");
        var productIds = products.Select(item => item.Id).ToHashSet();
        var familiesById = families.ToDictionary(item => item.Id);
        foreach (var relation in relations)
        {
            ValidateId(relation.Id, "DeviceSoftwareRelation.Id");
            ValidateId(relation.DeviceFamilyId, "DeviceSoftwareRelation.DeviceFamilyId");
            ValidateTokenList(relation.ExactModelIds, "DeviceSoftwareRelation.ExactModelIds", 128, true);
            ValidateTokenList(relation.DeviceAliases, "DeviceSoftwareRelation.DeviceAliases", 128, true);
            if (!productIds.Contains(relation.ProductId)) throw new CatalogValidationException($"Relation '{relation.Id}' references an unknown product '{relation.ProductId}'.");
            if (relation.ReleaseFamilyId is { } familyId)
            {
                if (!familiesById.TryGetValue(familyId, out var family)) throw new CatalogValidationException($"Relation '{relation.Id}' references an unknown release family '{familyId}'.");
                if (family.ProductId != relation.ProductId) throw new CatalogValidationException($"Relation '{relation.Id}' release family does not belong to its product.");
            }
            ValidateHttps(relation.EvidenceUri, "DeviceSoftwareRelation.EvidenceUri");
            ValidateText(relation.Constraints, "DeviceSoftwareRelation.Constraints", 1024, true);
        }
    }

    private static IEnumerable<string> DeviceTerms(DeviceSoftwareRelation relation) =>
        new[] { relation.DeviceFamilyId }.Concat(relation.ExactModelIds).Concat(relation.DeviceAliases).Select(value => NormalizeLookup(value, "Device relation term"));

    private static IEnumerable<string> ProductTerms(Product product) =>
        new[] { product.Id.Value, product.Name }.Concat(product.Aliases).Select(value => NormalizeLookup(value, "Product relation term"));

    private static void EnsureUnique<T>(IReadOnlyList<T> values, Func<T, string> key, string description)
    {
        var duplicate = values.GroupBy(key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new CatalogValidationException($"Compatibility catalog contains duplicate {description} '{duplicate.Key}'.");
    }

    private static void ValidateId(string value, string field)
    {
        ValidateText(value, field, 128);
        if (!CompatibilityCatalogParser.IdentifierPattern.IsMatch(value)) throw new CatalogValidationException($"{field} is invalid.");
    }

    private static void ValidateText(string? value, string field, int maximumLength, bool allowEmpty = false)
    {
        if (value is null || (!allowEmpty && string.IsNullOrWhiteSpace(value))) throw new CatalogValidationException($"{field} is required.");
        if (value is not null && (value.Length > maximumLength || value.Any(char.IsControl))) throw new CatalogValidationException($"{field} is invalid.");
    }

    private static void ValidateTokenList(IReadOnlyList<string>? values, string field, int maximumLength, bool allowEmpty)
    {
        if (values is null || (!allowEmpty && values.Count == 0)) throw new CatalogValidationException($"{field} is required.");
        if (values is null) return;
        foreach (var value in values) ValidateText(value, field, maximumLength);
        EnsureUnique(values, value => NormalizeLookup(value, field), field);
    }

    private static void ValidateHttps(Uri uri, string field)
    {
        if (!uri.IsAbsoluteUri || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(uri.DnsSafeHost))
            throw new CatalogValidationException($"{field} must be an absolute HTTPS URI.");
    }

    private static string NormalizeLookup(string? value, string field)
    {
        ValidateText(value, field, 128);
        return value!.Trim();
    }
}

/// <summary>Strict parser for the additive, standalone compatibility-catalog schema.</summary>
public sealed class CompatibilityCatalogParser
{
    internal static readonly Regex IdentifierPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{1,127}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    public SoftwareCompatibilityCatalog Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        CompatibilityDocument document;
        try
        {
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
            RejectDuplicateProperties(parsed.RootElement);
            document = JsonSerializer.Deserialize<CompatibilityDocument>(json, JsonOptions) ?? throw new CatalogValidationException("Compatibility catalog is empty.");
        }
        catch (JsonException exception)
        {
            throw new CatalogValidationException($"Compatibility catalog JSON is invalid: {exception.Message}");
        }

        if (document.SchemaVersion != 1) throw new CatalogValidationException("Compatibility catalog SchemaVersion must be the integer 1.");
        if (document.Products is null || document.ReleaseFamilies is null || document.InstalledVersions is null || document.DeviceSoftwareRelations is null)
            throw new CatalogValidationException("Compatibility catalog requires Products, ReleaseFamilies, InstalledVersions, and DeviceSoftwareRelations arrays.");

        return new SoftwareCompatibilityCatalog(
            document.Products.Select(ParseProduct).ToArray(),
            document.ReleaseFamilies.Select(ParseReleaseFamily).ToArray(),
            document.InstalledVersions.Select(ParseInstalledVersion).ToArray(),
            document.DeviceSoftwareRelations.Select(ParseRelation).ToArray());
    }

    private static Product ParseProduct(ProductRaw raw) => new(
        new SoftwareProductId(Required(raw.Id, "Product.Id")),
        Required(raw.Vendor, "Product.Vendor"),
        Required(raw.Name, "Product.Name"),
        ParseEnum<Lifecycle>(Required(raw.Lifecycle, "Product.Lifecycle"), "Product.Lifecycle"),
        ParseHttps(Required(raw.OfficialSourceUri, "Product.OfficialSourceUri"), "Product.OfficialSourceUri"),
        Strings(raw.Aliases, "Product.Aliases"));

    private static ReleaseFamily ParseReleaseFamily(ReleaseFamilyRaw raw) => new(
        new ReleaseFamilyId(Required(raw.Id, "ReleaseFamily.Id")),
        new SoftwareProductId(Required(raw.ProductId, "ReleaseFamily.ProductId")),
        ParseReleaseFamilyKind(Required(raw.Kind, "ReleaseFamily.Kind")),
        Required(raw.Branch, "ReleaseFamily.Branch"),
        ParseOptionalVersion(raw.MinimumVersion, "ReleaseFamily.MinimumVersion"),
        ParseOptionalVersion(raw.MaximumVersion, "ReleaseFamily.MaximumVersion"),
        ParseEnum<Lifecycle>(Required(raw.Lifecycle, "ReleaseFamily.Lifecycle"), "ReleaseFamily.Lifecycle"),
        ParseHttps(Required(raw.EvidenceUri, "ReleaseFamily.EvidenceUri"), "ReleaseFamily.EvidenceUri"),
        ParseEnum<CompatibilityEvidenceKind>(Required(raw.EvidenceKind, "ReleaseFamily.EvidenceKind"), "ReleaseFamily.EvidenceKind"),
        Optional(raw.Constraints));

    private static InstalledVersion ParseInstalledVersion(InstalledVersionRaw raw)
    {
        var state = ParseEnum<InstalledVersionEvidenceState>(Required(raw.State, "InstalledVersion.State"), "InstalledVersion.State");
        var rawVersion = Optional(raw.RawVersion);
        return new InstalledVersion(
            Required(raw.EvidenceId, "InstalledVersion.EvidenceId"),
            new SoftwareProductId(Required(raw.ProductId, "InstalledVersion.ProductId")),
            state,
            Optional(raw.RawDisplayName),
            rawVersion,
            rawVersion.Length == 0 ? null : ParseOptionalVersion(rawVersion, "InstalledVersion.RawVersion"),
            Strings(raw.Sources, "InstalledVersion.Sources"),
            Optional(raw.InstallLocation),
            Optional(raw.Architecture),
            Optional(raw.Detail));
    }

    private static DeviceSoftwareRelation ParseRelation(DeviceSoftwareRelationRaw raw) => new(
        Required(raw.Id, "DeviceSoftwareRelation.Id"),
        Required(raw.DeviceFamilyId, "DeviceSoftwareRelation.DeviceFamilyId"),
        Strings(raw.ExactModelIds, "DeviceSoftwareRelation.ExactModelIds"),
        Strings(raw.DeviceAliases, "DeviceSoftwareRelation.DeviceAliases"),
        new SoftwareProductId(Required(raw.ProductId, "DeviceSoftwareRelation.ProductId")),
        string.IsNullOrWhiteSpace(raw.ReleaseFamilyId) ? null : new ReleaseFamilyId(raw.ReleaseFamilyId.Trim()),
        ParseEnum<DeviceSoftwarePurpose>(Required(raw.Purpose, "DeviceSoftwareRelation.Purpose"), "DeviceSoftwareRelation.Purpose"),
        ParseEnum<RelationApplicability>(Required(raw.Applicability, "DeviceSoftwareRelation.Applicability"), "DeviceSoftwareRelation.Applicability"),
        ParseEnum<Lifecycle>(Required(raw.Lifecycle, "DeviceSoftwareRelation.Lifecycle"), "DeviceSoftwareRelation.Lifecycle"),
        ParseHttps(Required(raw.EvidenceUri, "DeviceSoftwareRelation.EvidenceUri"), "DeviceSoftwareRelation.EvidenceUri"),
        ParseEnum<CompatibilityEvidenceKind>(Required(raw.EvidenceKind, "DeviceSoftwareRelation.EvidenceKind"), "DeviceSoftwareRelation.EvidenceKind"),
        ParseEnum<CompatibilityEvidenceConfidence>(Required(raw.Confidence, "DeviceSoftwareRelation.Confidence"), "DeviceSoftwareRelation.Confidence"),
        Optional(raw.Constraints));

    private static ReleaseFamilyKind ParseReleaseFamilyKind(string value) => value switch
    {
        "Current" => ReleaseFamilyKind.Current,
        "LTS" => ReleaseFamilyKind.Lts,
        "Archived" => ReleaseFamilyKind.Archived,
        "Legacy" => ReleaseFamilyKind.Legacy,
        "Other" => ReleaseFamilyKind.Other,
        _ => throw new CatalogValidationException($"ReleaseFamily.Kind contains unsupported value '{value}'.")
    };

    private static T ParseEnum<T>(string value, string field) where T : struct, Enum
    {
        if (!Enum.TryParse<T>(value, false, out var parsed) || !Enum.IsDefined(parsed))
            throw new CatalogValidationException($"{field} contains unsupported value '{value}'.");
        return parsed;
    }

    private static VersionValue? ParseOptionalVersion(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return VersionValue.Parse(value.Trim()); }
        catch (FormatException exception) { throw new CatalogValidationException($"{field} must be a supported numeric version: {exception.Message}"); }
    }

    private static Uri ParseHttps(string value, string field)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(uri.DnsSafeHost))
            throw new CatalogValidationException($"{field} must be an absolute HTTPS URI.");
        return uri;
    }

    private static IReadOnlyList<string> Strings(List<string>? values, string field) => values is null
        ? []
        : values.Select(value => value?.Trim() ?? throw new CatalogValidationException($"{field} cannot contain null.")).ToArray();

    private static string Required(string? value, string field) => !string.IsNullOrWhiteSpace(value)
        ? value.Trim()
        : throw new CatalogValidationException($"{field} is required.");

    private static string Optional(string? value) => value?.Trim() ?? string.Empty;

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new CatalogValidationException($"Compatibility catalog repeats JSON property '{property.Name}'.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class CompatibilityDocument
    {
        public int SchemaVersion { get; init; }
        public List<ProductRaw>? Products { get; init; }
        public List<ReleaseFamilyRaw>? ReleaseFamilies { get; init; }
        public List<InstalledVersionRaw>? InstalledVersions { get; init; }
        public List<DeviceSoftwareRelationRaw>? DeviceSoftwareRelations { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ProductRaw
    {
        public string? Id { get; init; }
        public string? Vendor { get; init; }
        public string? Name { get; init; }
        public string? Lifecycle { get; init; }
        public string? OfficialSourceUri { get; init; }
        public List<string>? Aliases { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ReleaseFamilyRaw
    {
        public string? Id { get; init; }
        public string? ProductId { get; init; }
        public string? Kind { get; init; }
        public string? Branch { get; init; }
        public string? MinimumVersion { get; init; }
        public string? MaximumVersion { get; init; }
        public string? Lifecycle { get; init; }
        public string? EvidenceUri { get; init; }
        public string? EvidenceKind { get; init; }
        public string? Constraints { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class InstalledVersionRaw
    {
        public string? EvidenceId { get; init; }
        public string? ProductId { get; init; }
        public string? State { get; init; }
        public string? RawDisplayName { get; init; }
        public string? RawVersion { get; init; }
        public List<string>? Sources { get; init; }
        public string? InstallLocation { get; init; }
        public string? Architecture { get; init; }
        public string? Detail { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class DeviceSoftwareRelationRaw
    {
        public string? Id { get; init; }
        public string? DeviceFamilyId { get; init; }
        public List<string>? ExactModelIds { get; init; }
        public List<string>? DeviceAliases { get; init; }
        public string? ProductId { get; init; }
        public string? ReleaseFamilyId { get; init; }
        public string? Purpose { get; init; }
        public string? Applicability { get; init; }
        public string? Lifecycle { get; init; }
        public string? EvidenceUri { get; init; }
        public string? EvidenceKind { get; init; }
        public string? Confidence { get; init; }
        public string? Constraints { get; init; }
    }
}
