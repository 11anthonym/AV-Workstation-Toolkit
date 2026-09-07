using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Application.Compatibility;

/// <summary>
/// Supplies local installation evidence for descriptive compatibility products. Implementations do not decide
/// package currentness or grant execution authority.
/// </summary>
public interface IInstalledVersionEvidenceProvider
{
    Task<IReadOnlyList<InstalledVersion>> GetInstalledVersionsAsync(
        SoftwareProductId productId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Conservative production provider used until a product has an evidence-backed multi-install detector.
/// </summary>
public sealed class UnresolvedInstalledVersionEvidenceProvider : IInstalledVersionEvidenceProvider
{
    public Task<IReadOnlyList<InstalledVersion>> GetInstalledVersionsAsync(
        SoftwareProductId productId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<InstalledVersion> evidence =
        [
            new(
                $"Runtime.{productId.Value}.Unknown",
                productId,
                InstalledVersionEvidenceState.Unknown,
                string.Empty,
                string.Empty,
                null,
                [],
                string.Empty,
                string.Empty,
                "Authoritative multi-install detection has not been established for this compatibility product.")
        ];
        return Task.FromResult(evidence);
    }
}

public sealed record CompatibilityProductSummary(
    SoftwareProductId Id,
    string Vendor,
    string Name,
    Lifecycle Lifecycle,
    Uri OfficialSourceUri,
    IReadOnlyList<string> Aliases);

public sealed record CompatibilityReleaseFamilySummary(
    ReleaseFamilyId Id,
    ReleaseFamilyKind Kind,
    string Branch,
    Lifecycle Lifecycle,
    Uri EvidenceUri,
    CompatibilityEvidenceKind EvidenceKind,
    string Constraints);

public enum CompatibilitySearchMatchKind
{
    ExactModelOrAlias,
    ExactDeviceFamily,
    NormalizedExact,
    PrefixOrToken,
    Substring
}

public enum CompatibilitySearchOutcome
{
    ExactVerifiedRelationship,
    KnownFamilyOrAliasMatch,
    NoVerifiedRelationshipInCurrentCatalog,
    NoDeviceOrCatalogMatch
}

/// <summary>
/// A read-only device lookup result. MatchedRelationIds is the authoritative scope selected by the search;
/// presentation text must never be used to repeat or broaden that lookup.
/// </summary>
public sealed record CompatibilityDeviceSearchResult(
    string DeviceFamilyId,
    IReadOnlyList<string> ExactModelIds,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> MatchedRelationIds,
    CompatibilitySearchMatchKind MatchKind,
    string MatchedTerm);

public sealed record RelevantSoftwareSummary(
    SoftwareProductId ProductId,
    string ProductName,
    DeviceSoftwarePurpose Purpose,
    RelationApplicability Applicability,
    Lifecycle Lifecycle,
    ReleaseFamilyId? ReleaseFamilyId,
    Uri EvidenceUri,
    CompatibilityEvidenceKind EvidenceKind,
    CompatibilityEvidenceConfidence Confidence,
    string Constraints);

public sealed record RelevantSoftwarePurposeGroup(
    DeviceSoftwarePurpose Purpose,
    IReadOnlyList<RelevantSoftwareSummary> Software);

public sealed record ApplicableDeviceSummary(
    string DeviceFamilyId,
    IReadOnlyList<string> ExactModelIds,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<ApplicableDevicePurposeSummary> Purposes);

public sealed record ApplicableDevicePurposeSummary(
    DeviceSoftwarePurpose Purpose,
    RelationApplicability Applicability,
    Lifecycle Lifecycle,
    ReleaseFamilyId? ReleaseFamilyId,
    Uri EvidenceUri,
    CompatibilityEvidenceKind EvidenceKind,
    CompatibilityEvidenceConfidence Confidence,
    string Constraints);

/// <summary>
/// Read-only application queries over the compatibility evidence graph. This service is deliberately separate
/// from PackageCatalog, planning, selection, delivery, and worker authorization.
/// </summary>
public sealed class CompatibilityCatalogQueryService
{
    private readonly SoftwareCompatibilityCatalog catalog;
    private readonly IInstalledVersionEvidenceProvider installedVersionEvidence;

    public CompatibilityCatalogQueryService(
        SoftwareCompatibilityCatalog catalog,
        IInstalledVersionEvidenceProvider installedVersionEvidence)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.installedVersionEvidence = installedVersionEvidence ?? throw new ArgumentNullException(nameof(installedVersionEvidence));
    }

    public IReadOnlyList<CompatibilityProductSummary> SearchProducts(string? productOrAlias = null)
    {
        var query = productOrAlias?.Trim();
        return catalog.Products
            .Where(product => string.IsNullOrEmpty(query) || ProductTerms(product).Any(term => MatchDeviceTerm(term, query, isDeviceFamily: false) is not null))
            .OrderBy(product => string.IsNullOrEmpty(query)
                ? CompatibilitySearchMatchKind.Substring
                : ProductTerms(product).Select(term => MatchDeviceTerm(term, query, isDeviceFamily: false))
                    .Where(rank => rank is not null).Select(rank => rank!.Value).DefaultIfEmpty(CompatibilitySearchMatchKind.Substring).Min())
            .ThenBy(product => product.Vendor, StringComparer.OrdinalIgnoreCase)
            .ThenBy(product => product.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToSummary)
            .ToArray();
    }

    public CompatibilityProductSummary GetProduct(SoftwareProductId productId) =>
        ToSummary(catalog.GetRequiredProduct(productId));

    public IReadOnlyList<CompatibilityReleaseFamilySummary> GetReleaseFamilies(SoftwareProductId productId)
    {
        _ = catalog.GetRequiredProduct(productId);
        return catalog.ReleaseFamilies
            .Where(family => family.ProductId == productId)
            .OrderBy(family => family.Kind)
            .ThenBy(family => family.Branch, StringComparer.OrdinalIgnoreCase)
            .Select(family => new CompatibilityReleaseFamilySummary(
                family.Id,
                family.Kind,
                family.Branch,
                family.Lifecycle,
                family.EvidenceUri,
                family.EvidenceKind,
                family.Constraints))
            .ToArray();
    }

    public async Task<IReadOnlyList<InstalledVersion>> GetInstalledVersionsAsync(
        SoftwareProductId productId,
        CancellationToken cancellationToken = default)
    {
        _ = catalog.GetRequiredProduct(productId);
        var runtimeEvidence = await installedVersionEvidence.GetInstalledVersionsAsync(productId, cancellationToken).ConfigureAwait(false);
        if (runtimeEvidence.Any(item => item.ProductId != productId))
            throw new InvalidOperationException("An installed-version provider returned evidence for the wrong product.");

        var combined = catalog.GetInstalledVersions(productId).Concat(runtimeEvidence).ToArray();
        var duplicate = combined.GroupBy(item => item.EvidenceId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Installed-version evidence ID '{duplicate.Key}' was returned more than once.");
        return combined.OrderBy(item => item.EvidenceId, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<CompatibilityDeviceSearchResult> SearchDevices(string? deviceOrAlias = null)
    {
        var query = deviceOrAlias?.Trim();
        var grouped = catalog.DeviceSoftwareRelations
            .GroupBy(relation => relation.DeviceFamilyId, StringComparer.OrdinalIgnoreCase)
            .Select(group => ToDeviceSearchResult(group, query))
            .Where(result => result is not null)
            .Cast<CompatibilityDeviceSearchResult>();
        return string.IsNullOrEmpty(query)
            ? grouped.OrderBy(device => device.DeviceFamilyId, StringComparer.OrdinalIgnoreCase).ToArray()
            : grouped.OrderBy(device => device.MatchKind)
                .ThenBy(device => device.DeviceFamilyId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    public CompatibilitySearchOutcome GetSearchOutcome(string? search)
    {
        var query = search?.Trim() ?? string.Empty;
        if (query.Length == 0) return CompatibilitySearchOutcome.NoDeviceOrCatalogMatch;
        var devices = SearchDevices(query);
        if (devices.Any(device => device.MatchKind is CompatibilitySearchMatchKind.ExactModelOrAlias or CompatibilitySearchMatchKind.ExactDeviceFamily or CompatibilitySearchMatchKind.NormalizedExact))
            return CompatibilitySearchOutcome.ExactVerifiedRelationship;
        if (devices.Count > 0 || SearchProducts(query).Count > 0)
            return CompatibilitySearchOutcome.KnownFamilyOrAliasMatch;
        return LooksLikeModel(query)
            ? CompatibilitySearchOutcome.NoVerifiedRelationshipInCurrentCatalog
            : CompatibilitySearchOutcome.NoDeviceOrCatalogMatch;
    }

    public IReadOnlyList<RelevantSoftwarePurposeGroup> GetSoftwareForDevice(CompatibilityDeviceSearchResult device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var selectedIds = device.MatchedRelationIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedIds.Count == 0) throw new InvalidOperationException("A device search result has no relationship scope.");
        var relations = catalog.DeviceSoftwareRelations.Where(relation => selectedIds.Contains(relation.Id)).ToArray();
        if (relations.Length != selectedIds.Count || relations.Any(relation => !relation.DeviceFamilyId.Equals(device.DeviceFamilyId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A device search result has an invalid relationship scope.");
        return ToRelevantSoftwareGroups(relations);
    }

    public IReadOnlyList<RelevantSoftwarePurposeGroup> GetSoftwareForDevice(string deviceModelFamilyOrAlias)
    {
        var relations = catalog.GetSoftwareForDevice(deviceModelFamilyOrAlias);
        return ToRelevantSoftwareGroups(relations);
    }

    private IReadOnlyList<RelevantSoftwarePurposeGroup> ToRelevantSoftwareGroups(IEnumerable<DeviceSoftwareRelation> relations)
    {
        return relations
            .Select(relation =>
            {
                var product = catalog.GetRequiredProduct(relation.ProductId);
                return new RelevantSoftwareSummary(
                    product.Id,
                    product.Name,
                    relation.Purpose,
                    relation.Applicability,
                    relation.Lifecycle,
                    relation.ReleaseFamilyId,
                    relation.EvidenceUri,
                    relation.EvidenceKind,
                    relation.Confidence,
                    relation.Constraints);
            })
            .GroupBy(item => item.Purpose)
            .OrderBy(group => group.Key)
            .Select(group => new RelevantSoftwarePurposeGroup(
                group.Key,
                group.OrderBy(item => item.ProductName, StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArray();
    }

    public IReadOnlyList<ApplicableDeviceSummary> GetDevicesForProduct(SoftwareProductId productId)
    {
        _ = catalog.GetRequiredProduct(productId);
        return catalog.GetDevicesForSoftware(productId)
            .GroupBy(relation => relation.DeviceFamilyId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ApplicableDeviceSummary(
                group.Key,
                DistinctSorted(group.SelectMany(item => item.ExactModelIds)),
                DistinctSorted(group.SelectMany(item => item.DeviceAliases)),
                group.OrderBy(item => item.Purpose).Select(item => new ApplicableDevicePurposeSummary(
                    item.Purpose,
                    item.Applicability,
                    item.Lifecycle,
                    item.ReleaseFamilyId,
                    item.EvidenceUri,
                    item.EvidenceKind,
                    item.Confidence,
                    item.Constraints)).ToArray()))
            .OrderBy(device => device.DeviceFamilyId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static CompatibilityProductSummary ToSummary(Product product) => new(
        product.Id,
        product.Vendor,
        product.Name,
        product.Lifecycle,
        product.OfficialSourceUri,
        product.Aliases);

    private static CompatibilityDeviceSearchResult? ToDeviceSearchResult(
        IGrouping<string, DeviceSoftwareRelation> relations,
        string? query)
    {
        var models = DistinctSorted(relations.SelectMany(item => item.ExactModelIds));
        var aliases = DistinctSorted(relations.SelectMany(item => item.DeviceAliases));
        if (string.IsNullOrEmpty(query))
            return new CompatibilityDeviceSearchResult(relations.Key, models, aliases,
                relations.Select(item => item.Id).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                CompatibilitySearchMatchKind.Substring, relations.Key);

        var candidates = new List<(DeviceSoftwareRelation Relation, string Term, CompatibilitySearchMatchKind? Rank)>();
        foreach (var relation in relations)
        {
            candidates.Add((relation, relations.Key, MatchDeviceTerm(relations.Key, query, isDeviceFamily: true)));
            candidates.AddRange(relation.ExactModelIds.Concat(relation.DeviceAliases)
                .Select(term => (relation, term, MatchDeviceTerm(term, query, isDeviceFamily: false))));
        }
        var matched = candidates.Where(candidate => candidate.Rank is not null).Select(candidate =>
            (candidate.Relation, candidate.Term, Rank: candidate.Rank!.Value)).ToArray();
        if (matched.Length == 0) return null;
        var rank = matched.Min(candidate => candidate.Rank);
        var scopedRelations = rank == CompatibilitySearchMatchKind.ExactDeviceFamily
            ? relations.ToArray()
            : matched.Where(candidate => candidate.Rank == rank).Select(candidate => candidate.Relation)
                .DistinctBy(relation => relation.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        return new CompatibilityDeviceSearchResult(relations.Key, models, aliases,
            scopedRelations.Select(item => item.Id).OrderBy(value => value, StringComparer.Ordinal).ToArray(), rank,
            matched.Where(candidate => candidate.Rank == rank).Select(candidate => candidate.Term)
                .OrderBy(term => term, StringComparer.OrdinalIgnoreCase).First());
    }

    private static IEnumerable<string> ProductTerms(Product product) =>
        new[] { product.Id.Value, product.Name, product.Vendor }.Concat(product.Aliases);

    private static CompatibilitySearchMatchKind? MatchDeviceTerm(string term, string query, bool isDeviceFamily)
    {
        if (term.Equals(query, StringComparison.OrdinalIgnoreCase))
            return isDeviceFamily ? CompatibilitySearchMatchKind.ExactDeviceFamily : CompatibilitySearchMatchKind.ExactModelOrAlias;
        var normalizedTerm = NormalizeSearch(term);
        var normalizedQuery = NormalizeSearch(query);
        if (normalizedTerm.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase)) return CompatibilitySearchMatchKind.NormalizedExact;
        if (normalizedTerm.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
            Tokenize(term).Any(token => token.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase)))
            return CompatibilitySearchMatchKind.PrefixOrToken;
        return normalizedTerm.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
            ? CompatibilitySearchMatchKind.Substring
            : null;
    }

    private static string NormalizeSearch(string value) => string.Concat(value.Trim().Where(character => !char.IsWhiteSpace(character) && character is not '-' and not '_'));

    private static IEnumerable<string> Tokenize(string value) => value.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool LooksLikeModel(string value) => value.Any(char.IsLetter) && value.Any(char.IsDigit);

    private static IReadOnlyList<string> DistinctSorted(IEnumerable<string> values) =>
        values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
}
