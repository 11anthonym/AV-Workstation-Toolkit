using System.Text.Json;
using System.Text.Json.Serialization;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Domain.Versions;

namespace AVWorkstationToolkit.IntegrationTests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CoreParityFixture(
    int SchemaVersion,
    string ScenarioId,
    string AsOfDate,
    IReadOnlyList<VersionParityCase> Versions,
    IReadOnlyList<FilterPackageFixture> Packages,
    IReadOnlyList<FilterQueryFixture> Filters,
    IReadOnlyList<ExternalStateFixture> ExternalStates,
    IReadOnlyList<CatalogParityCase> Catalogs);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VersionParityCase(string Id, string Operation, string Value, string? Other);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FilterPackageFixture(
    string Id, string Name, string Vendor, string Profile, string Priority,
    IReadOnlyList<string> ApplicationTypes, IReadOnlyList<string> Roles,
    string Status, bool Installed, string AvailableVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FilterQueryFixture(
    string Id, IReadOnlyList<string> Profiles, IReadOnlyList<string> Priorities, string Vendor,
    string Discipline, IReadOnlyList<string> Roles, string Search, string QuickView);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogParityCase(
    string Id, string Source, string ForbiddenPattern,
    IReadOnlyList<ManagedPackageInput>? Applications, JsonElement? Document);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExternalStateFixture(
    string Id, string DetectionMode, string DetectionVersionPolicy, string ReleaseMode, string ReleaseChannel,
    string KnownVersion, string DeliveryMode, bool InventoryPresent, bool Reliable, string InventoryQuality,
    bool Installed, string InstalledVersion, IReadOnlyList<string> InstalledVersions, string AvailableVersion,
    string InventoryDetail);

public sealed record CoreParityResult(
    int SchemaVersion, string ScenarioId,
    IReadOnlyList<VersionParityResult> Versions,
    IReadOnlyList<FilterParityResult> Filters,
    IReadOnlyList<ExternalStateResult> ExternalStates,
    IReadOnlyList<CatalogParityResult> Catalogs);

public sealed record VersionParityResult(string Id, string Outcome, string Value);
public sealed record FilterParityResult(string Id, IReadOnlyList<string> Matches);
public sealed record CatalogParityResult(string Id, string Outcome, int PackageCount, IReadOnlyList<string> Authorities);
public sealed record ExternalStateResult(
    string Id, bool Installed, string InstalledVersion, string AvailableVersion, string Status,
    string StatusDetail, string ReasonCode, string Action, string InventoryQuality, bool WorkerEligible);

public static class CoreParityEvaluator
{
    public static CoreParityResult Evaluate(CoreParityFixture fixture)
    {
        if (fixture.SchemaVersion != 2) throw new InvalidDataException($"Unsupported core parity schema: {fixture.SchemaVersion}.");
        return new(2, fixture.ScenarioId,
            fixture.Versions.Select(EvaluateVersion).ToArray(),
            EvaluateFilters(fixture),
            fixture.ExternalStates.Select(EvaluateExternalState).ToArray(),
            fixture.Catalogs.Select(item => EvaluateCatalog(item, fixture.AsOfDate)).ToArray());
    }

    private static VersionParityResult EvaluateVersion(VersionParityCase item)
    {
        try
        {
            var value = item.Operation switch
            {
                "Normalize" => VersionValue.Parse(item.Value).Normalized,
                "Compare" => Math.Sign(VersionValue.Parse(item.Value).CompareTo(VersionValue.Parse(item.Other!))).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "SortKey" => VersionSortKey.Create(item.Value),
                _ => throw new InvalidDataException($"Unknown version operation: {item.Operation}.")
            };
            return new(item.Id, "Accepted", value);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            return new(item.Id, "Rejected", string.Empty);
        }
    }

    private static IReadOnlyList<FilterParityResult> EvaluateFilters(CoreParityFixture fixture)
    {
        var items = fixture.Packages.Select(item => new CatalogQueryItem(Definition(item),
            CatalogTokens.Parse<PackageStatus>(item.Status, $"{item.Id}.Status"), item.Installed, item.AvailableVersion)).ToArray();
        var service = new CatalogQueryService();
        return fixture.Filters.Select(filter =>
        {
            var query = new CatalogQuery(
                filter.Profiles.Select(value => CatalogTokens.Parse<PackageProfile>(value, $"{filter.Id}.Profiles")).ToHashSet(),
                filter.Priorities.Select(value => CatalogTokens.Parse<PackagePriority>(value, $"{filter.Id}.Priorities")).ToHashSet(),
                filter.Vendor, CatalogTokens.Parse<CatalogDiscipline>(filter.Discipline, $"{filter.Id}.Discipline"),
                filter.Roles.Select(value => CatalogTokens.Parse<PackageRole>(value, $"{filter.Id}.Roles")).ToHashSet(),
                filter.Search, CatalogTokens.Parse<QuickView>(filter.QuickView, $"{filter.Id}.QuickView"));
            return new FilterParityResult(filter.Id, service.Apply(items, query).Select(value => value.Package.Id).ToArray());
        }).ToArray();
    }

    private static CatalogParityResult EvaluateCatalog(CatalogParityCase item, string asOfDate)
    {
        try
        {
            var parser = new CatalogParser(DateOnly.ParseExact(asOfDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            var catalog = item.Source switch
            {
                "Managed" => parser.NormalizeManagedCatalog(item.Applications ?? throw new InvalidDataException("Managed catalog case requires Applications."), item.ForbiddenPattern),
                "External" => parser.ParseExternalCatalog(item.Document?.GetRawText() ?? throw new InvalidDataException("External catalog case requires Document.")),
                _ => throw new InvalidDataException($"Unknown catalog source: {item.Source}.")
            };
            return new(item.Id, "Accepted", catalog.Items.Count, catalog.Items.Select(value => value.Authority.ToToken()).OrderBy(value => value, StringComparer.Ordinal).ToArray());
        }
        catch (CatalogValidationException)
        {
            return new(item.Id, "Rejected", 0, []);
        }
    }

    private static ExternalStateResult EvaluateExternalState(ExternalStateFixture item)
    {
        var package = new PackageDefinition(item.Id, item.Id, "Fixture", string.Empty, "Fixture", ProviderKind.External,
            item.DeliveryMode == "Awareness" ? CatalogAuthority.AwarenessOnly : CatalogAuthority.OperationalExternal,
            PackageProfile.Optional, PackagePriority.P2, PackageRisk.None, DeploymentPolicy.ManualHold, MaintenancePolicy.Hold,
            item.DeliveryMode == "Awareness" ? DeploymentClass.AwarenessOnly : DeploymentClass.ManualHandoff,
            CatalogMaintenancePolicy.Manual, VersionRule.Unknown, VersionCouplingMode.Independent, string.Empty, Lifecycle.Current,
            [ApplicationType.FieldUtility], [], [], [LicensingModel.UnknownCost], ["UNKNOWN-ACCESS"], DistributionPolicy.LinkOnly,
            [], [SupportedOperatingSystem.Windows], CatalogTokens.Parse<DeliveryMode>(item.DeliveryMode, $"{item.Id}.DeliveryMode"),
            CatalogTokens.Parse<ReleaseMode>(item.ReleaseMode, $"{item.Id}.ReleaseMode"),
            CatalogTokens.Parse<DetectionMode>(item.DetectionMode, $"{item.Id}.DetectionMode"),
            CatalogTokens.Parse<DetectionVersionPolicy>(item.DetectionVersionPolicy, $"{item.Id}.DetectionVersionPolicy"),
            item.ReleaseChannel, item.KnownVersion, string.Empty, string.Empty, [], null, null, null, null, null, null, null, null, null, string.Empty, []);
        var evidence = new PackageEvidence(true, item.InventoryPresent, item.Reliable,
            CatalogTokens.Parse<InventoryQuality>(item.InventoryQuality, $"{item.Id}.InventoryQuality"), item.Installed,
            item.InstalledVersion, item.InstalledVersions, false, item.AvailableVersion, item.InventoryDetail, string.Empty);
        var state = new AVWorkstationToolkit.Domain.Planning.PlanningService().Evaluate(package, evidence);
        return new(item.Id, state.Installed, state.InstalledVersion, state.AvailableVersion, state.Status.ToToken(),
            state.StatusDetail, state.ReasonCode, state.Action.ToToken(), state.InventoryQuality.ToToken(), false);
    }

    private static PackageDefinition Definition(FilterPackageFixture item) =>
        new(item.Id, item.Name, item.Vendor, string.Empty, "Fixture", ProviderKind.WinGet, CatalogAuthority.ManagedWinGet,
            CatalogTokens.Parse<PackageProfile>(item.Profile, $"{item.Id}.Profile"), CatalogTokens.Parse<PackagePriority>(item.Priority, $"{item.Id}.Priority"),
            PackageRisk.None, DeploymentPolicy.Allowlisted, MaintenancePolicy.Allowlisted, DeploymentClass.Managed,
            CatalogMaintenancePolicy.Managed, VersionRule.Latest, VersionCouplingMode.Independent, string.Empty, Lifecycle.Current,
            item.ApplicationTypes.Select(value => CatalogTokens.Parse<ApplicationType>(value, $"{item.Id}.ApplicationTypes")).ToArray(),
            item.Roles.Select(value => CatalogTokens.Parse<PackageRole>(value, $"{item.Id}.Roles")).ToArray(), [], [LicensingModel.Free],
            ["PUBLIC-DL"], DistributionPolicy.PackageManagerOnly, [InstallationForm.WinGet], [SupportedOperatingSystem.Windows],
            DeliveryMode.None, ReleaseMode.None, DetectionMode.WinGet, DetectionVersionPolicy.None, string.Empty, string.Empty,
            string.Empty, string.Empty, [], null, null, null, null, null, null, null, null, null, string.Empty, []);
}
