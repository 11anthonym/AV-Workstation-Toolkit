using System.Text.Json.Serialization;
using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;
using AVWorkstationToolkit.Application.Details;
using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;

namespace AVWorkstationToolkit.IntegrationTests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReadOnlySurfacesFixture(int SchemaVersion, string ScenarioId,
    IReadOnlyList<ReadOnlyPackageFixture> Packages, IReadOnlyList<ReadOnlyRegistrySourceFixture> RegistrySources,
    IReadOnlyList<string> RebootReasons);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReadOnlyPackageFixture(string Id, string Status, bool Installed, string InstalledVersion, string AvailableVersion, string InventoryQuality);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReadOnlyRegistrySourceFixture(string Name, bool Available, int EntryCount);

public static class ReadOnlySurfacesParityEvaluator
{
    public static async Task<object> EvaluateAsync(ReadOnlySurfacesFixture fixture)
    {
        if (fixture.SchemaVersion != 1) throw new InvalidDataException($"Unsupported read-only surface parity schema: {fixture.SchemaVersion}.");
        var catalog = new RepositoryCatalogLoader().Load(RepositoryRootLocator.Find());
        var states = fixture.Packages.Select(input =>
        {
            var package = catalog.GetRequired(input.Id);
            return new PackageState(package, input.Installed, input.InstalledVersion,
                input.Installed ? [input.InstalledVersion] : [], input.AvailableVersion, false,
                CatalogTokens.Parse<PackageStatus>(input.Status, $"{input.Id}.Status"), input.Status, input.Status,
                PackageAction.None, CatalogTokens.Parse<InventoryQuality>(input.InventoryQuality, $"{input.Id}.InventoryQuality"));
        }).ToArray();
        var summary = Summarize(states);
        var sources = fixture.RegistrySources.Select(source => new RegistrySourceStatus(
            Enum.Parse<RegistryInventorySource>(source.Name, false), source.Available, source.EntryCount, string.Empty)).ToArray();
        var reasons = fixture.RebootReasons.Select(value => CatalogTokens.Parse<RebootReason>(value, "RebootReason")).ToArray();
        var plan = new WorkstationPlan(states, summary, new(reasons.Length > 0, reasons, string.Join(", ", reasons)),
            new(ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Partial, ProviderQuality.Complete,
                ["One source unavailable."], ExternalSources: sources, ExternalInventoryDetail: "One source unavailable."));
        var diagnostics = await new ReadOnlyDiagnosticsService(new FixtureRuntimeProvider(),
            new("fixture", "Source", "C:\\fixture\\data", "C:\\fixture\\logs")).ComposeAsync(plan).ConfigureAwait(false);
        var detailService = new CatalogDetailService();
        return new
        {
            SchemaVersion = 1,
            fixture.ScenarioId,
            Details = states.Select(state =>
            {
                var detail = detailService.Create(state, catalog);
                var package = state.Package;
                return new
                {
                    package.Id,
                    package.Name,
                    package.Vendor,
                    package.ProductFamily,
                    Purpose = package.Note,
                    Priority = package.Priority.ToToken(),
                    Lifecycle = package.Lifecycle.ToToken(),
                    package.ParentProviderId,
                    package.MetadataDetails.OfficialProductUri,
                    package.MetadataDetails.OfficialDownloadUri,
                    VerificationState = package.MetadataDetails.MetadataVerificationState.ToString(),
                    Quarantined = package.MetadataDetails.MetadataQuarantined,
                    SideBySide = package.MetadataDetails.SideBySideSupported,
                    Status = state.Status.ToToken(),
                    InventoryQuality = state.InventoryQuality.ToToken()
                };
            }).ToArray(),
            Diagnostics = new
            {
                diagnostics.Catalog.Total,
                diagnostics.Catalog.OperationalExternal,
                diagnostics.Catalog.Awareness,
                diagnostics.Catalog.InventoryWarnings,
                diagnostics.RebootPending,
                WindowsUpdate = diagnostics.RebootReasons.Contains(RebootReason.WindowsUpdate),
                ComponentBasedServicing = diagnostics.RebootReasons.Contains(RebootReason.ComponentBasedServicing),
                RegistrySources = diagnostics.RegistrySources.Select(source => new
                {
                    source.Name,
                    Status = source.State == DiagnosticEvidenceState.Available ? "OK" : "Failed",
                    source.EntryCount
                }).ToArray()
            }
        };
    }

    private static WorkstationPlanSummary Summarize(IReadOnlyList<PackageState> states)
    {
        int Count(PackageStatus status) => states.Count(item => item.Status == status);
        return new(states.Count, Count(PackageStatus.Current), Count(PackageStatus.Missing), Count(PackageStatus.UpdateAvailable),
            Count(PackageStatus.Manual), Count(PackageStatus.ManualUpdate), Count(PackageStatus.Held), Count(PackageStatus.Inventory),
            Count(PackageStatus.NotDetected), Count(PackageStatus.InventoryIncomplete), Count(PackageStatus.InventoryUnavailable),
            Count(PackageStatus.CheckUnavailable), Count(PackageStatus.Awareness), Count(PackageStatus.Error));
    }

    private sealed class FixtureRuntimeProvider : IRuntimeDiagnosticsProvider
    {
        public Task<RuntimeDiagnosticFacts> ReadAsync(CancellationToken cancellationToken = default)
        {
            var value = new DiagnosticValue(DiagnosticEvidenceState.Available, "fixture");
            return Task.FromResult(new RuntimeDiagnosticFacts(value, value, value, value, value, value, value));
        }
    }
}
