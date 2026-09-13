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

public sealed record CompatibilityCatalogSearchResult(
    IReadOnlyList<CompatibilityDeviceSearchResult> Devices,
    IReadOnlyList<CompatibilityProductSummary> Products,
    CompatibilitySearchOutcome Outcome);

public enum HardwareLookupState
{
    RelationOnly,
    KnownExactModelWithVerifiedRelationships,
    KnownExactModelWithNoVerifiedRelationshipsYet,
    KnownFamilyWithVerifiedRelationships,
    KnownFamilyWithUnresolvedCoverage
}

public sealed record HardwareIdentitySummary(
    string Id,
    string Manufacturer,
    HardwareDeviceCategory Category,
    string Family,
    string ExactModel,
    IReadOnlyList<string> Aliases,
    Lifecycle Lifecycle,
    HardwareCoverageState CoverageState);

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
    string MatchedTerm,
    HardwareIdentitySummary? Hardware = null,
    HardwareLookupState LookupState = HardwareLookupState.RelationOnly);

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
    private readonly HardwareIdentityCatalog? hardwareCatalog;
    private readonly ProductSearchEntry[] productSearchIndex;
    private readonly RelationSearchGroup[] relationSearchIndex;
    private readonly HardwareSearchEntry[] hardwareModelSearchIndex;
    private readonly HardwareSearchEntry[] hardwareFamilySearchIndex;

    public CompatibilityCatalogQueryService(
        SoftwareCompatibilityCatalog catalog,
        IInstalledVersionEvidenceProvider installedVersionEvidence,
        HardwareIdentityCatalog? hardwareCatalog = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.installedVersionEvidence = installedVersionEvidence ?? throw new ArgumentNullException(nameof(installedVersionEvidence));
        this.hardwareCatalog = hardwareCatalog;
        productSearchIndex = catalog.Products.Select(product =>
            new ProductSearchEntry(product, ProductTerms(product).Select(IndexedSearchTerm.Create).ToArray())).ToArray();
        relationSearchIndex = catalog.DeviceSoftwareRelations
            .GroupBy(relation => relation.DeviceFamilyId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RelationSearchGroup(
                group.Key,
                group.ToArray(),
                IndexedSearchTerm.Create(group.Key),
                group.SelectMany(relation => relation.ExactModelIds.Concat(relation.DeviceAliases)
                    .Select(term => new RelationSearchTerm(relation, IndexedSearchTerm.Create(term)))).ToArray()))
            .ToArray();
        hardwareModelSearchIndex = hardwareCatalog?.Models.Select(model =>
        {
            var family = hardwareCatalog.GetRequiredFamily(model.FamilyId);
            return new HardwareSearchEntry(family, model,
                new[] { model.Name }.Concat(model.Aliases).Select(IndexedSearchTerm.Create).ToArray());
        }).ToArray() ?? [];
        hardwareFamilySearchIndex = hardwareCatalog?.Families.Select(family =>
            new HardwareSearchEntry(family, null,
                new[] { family.Id.Value, family.Name }.Concat(family.Aliases).Select(IndexedSearchTerm.Create).ToArray())).ToArray() ?? [];
        if (hardwareCatalog is not null) ValidateHardwareCoverage(hardwareCatalog);
    }

    public IReadOnlyList<CompatibilityProductSummary> SearchProducts(string? productOrAlias = null)
    {
        var query = productOrAlias?.Trim();
        var queryIndex = SearchQuery.Create(query);
        return productSearchIndex
            .Where(entry => queryIndex is null || entry.Terms.Any(term => term.Match(queryIndex, isDeviceFamily: false) is not null))
            .OrderBy(entry => queryIndex is null
                ? CompatibilitySearchMatchKind.Substring
                : entry.Terms.Select(term => term.Match(queryIndex, isDeviceFamily: false))
                    .Where(rank => rank is not null).Select(rank => rank!.Value).DefaultIfEmpty(CompatibilitySearchMatchKind.Substring).Min())
            .ThenBy(entry => entry.Product.Vendor, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Product.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => ToSummary(entry.Product))
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
        var relationMatches = SearchRelationDevices(query);
        if (hardwareCatalog is null || string.IsNullOrEmpty(query)) return relationMatches;

        var hardwareMatches = SearchHardware(query)
            .Select(ToHardwareDeviceResult)
            .ToArray();
        var identifiedFamilies = hardwareMatches.Select(match => match.DeviceFamilyId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return hardwareMatches.Concat(relationMatches.Where(match => !identifiedFamilies.Contains(match.DeviceFamilyId)))
            .OrderBy(match => match.MatchKind)
            .ThenBy(match => match.DeviceFamilyId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<CompatibilityDeviceSearchResult> SearchRelationDevices(string? query)
    {
        var grouped = relationSearchIndex
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
        return Search(search).Outcome;
    }

    public CompatibilityCatalogSearchResult Search(string? search, CancellationToken cancellationToken = default)
    {
        var query = search?.Trim() ?? string.Empty;
        if (query.Length == 0)
            return new CompatibilityCatalogSearchResult([], [], CompatibilitySearchOutcome.NoDeviceOrCatalogMatch);
        cancellationToken.ThrowIfCancellationRequested();
        var devices = SearchDevices(query);
        cancellationToken.ThrowIfCancellationRequested();
        var products = SearchProducts(query);
        cancellationToken.ThrowIfCancellationRequested();
        var outcome = GetSearchOutcome(query, devices, products);
        return new CompatibilityCatalogSearchResult(devices, products, outcome);
    }

    private static CompatibilitySearchOutcome GetSearchOutcome(
        string query,
        IReadOnlyList<CompatibilityDeviceSearchResult> devices,
        IReadOnlyList<CompatibilityProductSummary> products)
    {
        if (devices.Any(device =>
            device.LookupState is (HardwareLookupState.RelationOnly or HardwareLookupState.KnownExactModelWithVerifiedRelationships or HardwareLookupState.KnownFamilyWithVerifiedRelationships) &&
            device.MatchKind is (CompatibilitySearchMatchKind.ExactModelOrAlias or CompatibilitySearchMatchKind.ExactDeviceFamily or CompatibilitySearchMatchKind.NormalizedExact)))
            return CompatibilitySearchOutcome.ExactVerifiedRelationship;
        if (devices.Count > 0 || products.Count > 0)
            return CompatibilitySearchOutcome.KnownFamilyOrAliasMatch;
        return LooksLikeModel(query)
            ? CompatibilitySearchOutcome.NoVerifiedRelationshipInCurrentCatalog
            : CompatibilitySearchOutcome.NoDeviceOrCatalogMatch;
    }

    public HardwareCoverageSummary? GetHardwareCoverageSummary() => hardwareCatalog?.GetCoverageSummary();

    public IReadOnlyList<RelevantSoftwarePurposeGroup> GetSoftwareForDevice(CompatibilityDeviceSearchResult device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var selectedIds = device.MatchedRelationIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedIds.Count == 0) return [];
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

    private IEnumerable<HardwareSearchMatch> SearchHardware(string query)
    {
        if (hardwareCatalog is null) return [];
        var queryIndex = SearchQuery.Create(query)!;
        var modelMatches = hardwareModelSearchIndex.Select(entry =>
        {
            var match = BestMatch(entry.Terms, queryIndex, isDeviceFamily: false);
            return match is null ? null : new HardwareSearchMatch(entry.Family, entry.Model, match.Value.Kind, match.Value.Term);
        }).Where(match => match is not null).Cast<HardwareSearchMatch>().ToArray();
        var familyMatches = hardwareFamilySearchIndex.Select(entry =>
        {
            var match = BestMatch(entry.Terms, queryIndex, isDeviceFamily: true);
            return match is null ? null : new HardwareSearchMatch(entry.Family, null, match.Value.Kind, match.Value.Term);
        }).Where(match => match is not null).Cast<HardwareSearchMatch>()
            .Where(familyMatch => !modelMatches.Any(modelMatch => modelMatch.Family.Id == familyMatch.Family.Id && modelMatch.MatchKind <= familyMatch.MatchKind))
            .ToArray();
        return modelMatches.Concat(familyMatches)
            .OrderBy(match => match.MatchKind)
            .ThenBy(match => match.Model is null ? 1 : 0)
            .ThenBy(match => match.Model?.Name ?? match.Family.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private CompatibilityDeviceSearchResult ToHardwareDeviceResult(HardwareSearchMatch match)
    {
        var identity = match.Model is null
            ? new HardwareIdentitySummary(match.Family.Id.Value, match.Family.Manufacturer, match.Family.Category, match.Family.Name,
                string.Empty, match.Family.Aliases, match.Family.Lifecycle, match.Family.CoverageState)
            : new HardwareIdentitySummary(match.Model.Id.Value, match.Family.Manufacturer, match.Family.Category, match.Family.Name,
                match.Model.Name, match.Model.Aliases, match.Model.Lifecycle, match.Model.CoverageState);
        var isVerified = identity.CoverageState == HardwareCoverageState.VerifiedSoftwareRelationships;
        var state = match.Model is null
            ? isVerified ? HardwareLookupState.KnownFamilyWithVerifiedRelationships : HardwareLookupState.KnownFamilyWithUnresolvedCoverage
            : isVerified ? HardwareLookupState.KnownExactModelWithVerifiedRelationships : HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet;
        var scope = isVerified ? GetHardwareRelationScope(match) : null;
        if (isVerified && scope is null)
            throw new InvalidOperationException($"Verified hardware identity '{identity.Id}' has no matching DeviceSoftwareRelation scope.");
        return new CompatibilityDeviceSearchResult(
            match.Family.CompatibilityDeviceFamilyId.Length == 0 ? match.Family.Id.Value : match.Family.CompatibilityDeviceFamilyId,
            match.Model is null ? [] : [match.Model.Name],
            match.Model is null ? match.Family.Aliases : match.Model.Aliases,
            scope?.MatchedRelationIds ?? [],
            match.MatchKind,
            match.MatchedTerm,
            identity,
            state);
    }

    private CompatibilityDeviceSearchResult? GetHardwareRelationScope(HardwareSearchMatch match)
    {
        var compatibilityFamily = match.Family.CompatibilityDeviceFamilyId;
        if (compatibilityFamily.Length == 0) return null;
        if (match.Model is null)
            return SearchRelationDevices(compatibilityFamily).SingleOrDefault(result => result.DeviceFamilyId.Equals(compatibilityFamily, StringComparison.OrdinalIgnoreCase));
        var candidates = new[] { match.Model.Name }.Concat(match.Model.Aliases)
            .SelectMany(SearchRelationDevices)
            .Where(result => result.DeviceFamilyId.Equals(compatibilityFamily, StringComparison.OrdinalIgnoreCase))
            .OrderBy(result => result.MatchKind)
            .ToArray();
        return candidates.FirstOrDefault();
    }

    private void ValidateHardwareCoverage(HardwareIdentityCatalog hardware)
    {
        foreach (var family in hardware.Families.Where(family => family.CoverageState == HardwareCoverageState.VerifiedSoftwareRelationships))
        {
            if (GetHardwareRelationScope(new HardwareSearchMatch(family, null, CompatibilitySearchMatchKind.ExactDeviceFamily, family.Id.Value)) is null)
                throw new CatalogValidationException($"Verified hardware family '{family.Id}' has no DeviceSoftwareRelation scope.");
        }
        foreach (var model in hardware.Models.Where(model => model.CoverageState == HardwareCoverageState.VerifiedSoftwareRelationships))
        {
            var family = hardware.GetRequiredFamily(model.FamilyId);
            if (GetHardwareRelationScope(new HardwareSearchMatch(family, model, CompatibilitySearchMatchKind.ExactModelOrAlias, model.Name)) is null)
                throw new CatalogValidationException($"Verified hardware model '{model.Id}' has no DeviceSoftwareRelation scope.");
        }
    }

    private static (CompatibilitySearchMatchKind Kind, string Term)? BestMatch(
        IEnumerable<IndexedSearchTerm> terms,
        SearchQuery query,
        bool isDeviceFamily)
    {
        var matches = terms.Select(term => (Term: term.Original, Kind: term.Match(query, isDeviceFamily)))
            .Where(match => match.Kind is not null)
            .Select(match => (match.Kind!.Value, match.Term)).ToArray();
        return matches.Length == 0 ? null : matches.OrderBy(match => match.Value).ThenBy(match => match.Term, StringComparer.OrdinalIgnoreCase).First();
    }

    private sealed record HardwareSearchMatch(
        HardwareFamily Family,
        HardwareModel? Model,
        CompatibilitySearchMatchKind MatchKind,
        string MatchedTerm);

    private static CompatibilityProductSummary ToSummary(Product product) => new(
        product.Id,
        product.Vendor,
        product.Name,
        product.Lifecycle,
        product.OfficialSourceUri,
        product.Aliases);

    private static CompatibilityDeviceSearchResult? ToDeviceSearchResult(
        RelationSearchGroup group,
        string? query)
    {
        var relations = group.Relations;
        var models = DistinctSorted(relations.SelectMany(item => item.ExactModelIds));
        var aliases = DistinctSorted(relations.SelectMany(item => item.DeviceAliases));
        if (string.IsNullOrEmpty(query))
            return new CompatibilityDeviceSearchResult(group.DeviceFamilyId, models, aliases,
                relations.Select(item => item.Id).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                CompatibilitySearchMatchKind.Substring, group.DeviceFamilyId);

        var queryIndex = SearchQuery.Create(query)!;
        var candidates = new List<(DeviceSoftwareRelation Relation, string Term, CompatibilitySearchMatchKind? Rank)>();
        var familyRank = group.FamilyTerm.Match(queryIndex, isDeviceFamily: true);
        candidates.AddRange(group.Terms.Select(term =>
            (term.Relation, term.Term.Original, Rank: term.Term.Match(queryIndex, isDeviceFamily: false))));
        var matched = candidates.Where(candidate => candidate.Rank is not null).Select(candidate =>
            (candidate.Relation, candidate.Term, Rank: candidate.Rank!.Value)).ToArray();
        if (matched.Length == 0 && familyRank is null) return null;
        var rank = matched.Select(candidate => candidate.Rank).Append(familyRank ?? CompatibilitySearchMatchKind.Substring).Min();
        var familyIsBest = familyRank == rank;
        var scopedRelations = familyIsBest
            ? relations.ToArray()
            : matched.Where(candidate => candidate.Rank == rank).Select(candidate => candidate.Relation)
                .DistinctBy(relation => relation.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        var matchedTerm = familyIsBest
            ? group.FamilyTerm.Original
            : matched.Where(candidate => candidate.Rank == rank).Select(candidate => candidate.Term)
                .OrderBy(term => term, StringComparer.OrdinalIgnoreCase).First();
        return new CompatibilityDeviceSearchResult(group.DeviceFamilyId, models, aliases,
            scopedRelations.Select(item => item.Id).OrderBy(value => value, StringComparer.Ordinal).ToArray(), rank,
            matchedTerm);
    }

    private static IEnumerable<string> ProductTerms(Product product) =>
        new[] { product.Id.Value, product.Name, product.Vendor }.Concat(product.Aliases);

    private static string NormalizeSearch(string value) => string.Concat(value.Trim().Where(character => !char.IsWhiteSpace(character) && character is not '-' and not '_'));

    private static IEnumerable<string> Tokenize(string value) => value.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool LooksLikeModel(string value) => value.Any(char.IsLetter) && value.Any(char.IsDigit);

    private static IReadOnlyList<string> DistinctSorted(IEnumerable<string> values) =>
        values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();

    private sealed record ProductSearchEntry(Product Product, IndexedSearchTerm[] Terms);
    private sealed record RelationSearchTerm(DeviceSoftwareRelation Relation, IndexedSearchTerm Term);
    private sealed record RelationSearchGroup(
        string DeviceFamilyId,
        DeviceSoftwareRelation[] Relations,
        IndexedSearchTerm FamilyTerm,
        RelationSearchTerm[] Terms);
    private sealed record HardwareSearchEntry(HardwareFamily Family, HardwareModel? Model, IndexedSearchTerm[] Terms);
    private sealed record SearchQuery(string Original, string Normalized)
    {
        public static SearchQuery? Create(string? value)
        {
            var original = value?.Trim() ?? string.Empty;
            return original.Length == 0 ? null : new SearchQuery(original, NormalizeSearch(original));
        }
    }
    private sealed record IndexedSearchTerm(string Original, string Normalized, string[] Tokens)
    {
        public static IndexedSearchTerm Create(string value) =>
            new(value, NormalizeSearch(value), Tokenize(value).ToArray());

        public CompatibilitySearchMatchKind? Match(SearchQuery query, bool isDeviceFamily)
        {
            if (Original.Equals(query.Original, StringComparison.OrdinalIgnoreCase))
                return isDeviceFamily ? CompatibilitySearchMatchKind.ExactDeviceFamily : CompatibilitySearchMatchKind.ExactModelOrAlias;
            if (Normalized.Equals(query.Normalized, StringComparison.OrdinalIgnoreCase))
                return CompatibilitySearchMatchKind.NormalizedExact;
            if (Normalized.StartsWith(query.Normalized, StringComparison.OrdinalIgnoreCase) ||
                Tokens.Any(token => token.StartsWith(query.Normalized, StringComparison.OrdinalIgnoreCase)))
                return CompatibilitySearchMatchKind.PrefixOrToken;
            return Normalized.Contains(query.Normalized, StringComparison.OrdinalIgnoreCase)
                ? CompatibilitySearchMatchKind.Substring
                : null;
        }
    }
}
