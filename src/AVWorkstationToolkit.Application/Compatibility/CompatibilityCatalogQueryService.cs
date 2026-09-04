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

public sealed record CompatibilityDeviceSearchResult(
    string DeviceFamilyId,
    IReadOnlyList<string> ExactModelIds,
    IReadOnlyList<string> Aliases);

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
            .Where(product => string.IsNullOrEmpty(query) || ProductTerms(product).Any(term => Contains(term, query)))
            .OrderBy(product => product.Vendor, StringComparer.OrdinalIgnoreCase)
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
        return catalog.DeviceSoftwareRelations
            .GroupBy(relation => relation.DeviceFamilyId, StringComparer.OrdinalIgnoreCase)
            .Select(ToDeviceSearchResult)
            .Where(device => string.IsNullOrEmpty(query) || DeviceTerms(device).Any(term => Contains(term, query)))
            .OrderBy(device => device.DeviceFamilyId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<RelevantSoftwarePurposeGroup> GetSoftwareForDevice(string deviceModelFamilyOrAlias)
    {
        var relations = catalog.GetSoftwareForDevice(deviceModelFamilyOrAlias);
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

    private static CompatibilityDeviceSearchResult ToDeviceSearchResult(IGrouping<string, DeviceSoftwareRelation> relations) => new(
        relations.Key,
        DistinctSorted(relations.SelectMany(item => item.ExactModelIds)),
        DistinctSorted(relations.SelectMany(item => item.DeviceAliases)));

    private static IEnumerable<string> ProductTerms(Product product) =>
        new[] { product.Id.Value, product.Name, product.Vendor }.Concat(product.Aliases);

    private static IEnumerable<string> DeviceTerms(CompatibilityDeviceSearchResult device) =>
        new[] { device.DeviceFamilyId }.Concat(device.ExactModelIds).Concat(device.Aliases);

    private static bool Contains(string value, string query) =>
        value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> DistinctSorted(IEnumerable<string> values) =>
        values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
}
