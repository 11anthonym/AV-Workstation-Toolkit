using System.Text.Json;
using System.Text.Json.Serialization;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Processes;
using AVWorkstationToolkit.Infrastructure.Windows.Reboot;
using AVWorkstationToolkit.Infrastructure.Windows.Registry;
using AVWorkstationToolkit.Infrastructure.Windows.WinGet;

namespace AVWorkstationToolkit.IntegrationTests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProviderParityFixture(
    int SchemaVersion,
    string ScenarioId,
    IReadOnlyList<WinGetInstalledFixture> InstalledInventory,
    IReadOnlyList<WinGetUpdateFixture> Updates,
    IReadOnlyList<RegistryFixture> Registry,
    IReadOnlyList<RebootFixture> Reboot,
    IReadOnlyList<TrustFixture> Trust);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WinGetInstalledFixture(
    string Id, bool Available, int ExitCode, string Output, string ExportJson,
    IReadOnlyList<CatalogIdentityFixture> Catalog);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogIdentityFixture(string Id, string Name);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WinGetUpdateFixture(string Id, bool Available, int ExitCode, string Output);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryFixture(
    string Id, IReadOnlyList<RegistrySourceFixture> Sources, IReadOnlyList<RegistryPackageFixture> Packages);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistrySourceFixture(string Name, bool Available, string Detail, IReadOnlyList<RegistryEntryFixture> Entries);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryEntryFixture(string DisplayName, string DisplayVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPackageFixture(string Id, string DetectionMode, string DisplayPattern, string VersionPattern);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RebootFixture(string Id, bool WindowsUpdate, bool ComponentBasedServicing);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TrustFixture(
    string Id, string PackageName, string PackagePublisher, string Version, string InstallLocation,
    string ExecutablePath, bool FileExists, bool RegularFile, bool PathContained, bool ReparseFree,
    bool SignatureValid, string SignerSubject);

public sealed record ProviderParityResult(
    int SchemaVersion,
    string ScenarioId,
    IReadOnlyList<InventoryCanonicalResult> InstalledInventory,
    IReadOnlyList<InventoryCanonicalResult> Updates,
    IReadOnlyList<RegistryCanonicalResult> Registry,
    IReadOnlyList<RebootCanonicalResult> Reboot,
    IReadOnlyList<TrustCanonicalResult> Trust);

public sealed record InventoryCanonicalResult(string Id, string Quality, string Failure, IReadOnlyList<string> Records);
public sealed record RegistryCanonicalResult(string Id, string Quality, string Failure, IReadOnlyList<string> Sources, IReadOnlyList<string> Records, IReadOnlyList<string> Packages);
public sealed record RebootCanonicalResult(string Id, bool Pending, IReadOnlyList<string> Reasons);
public sealed record TrustCanonicalResult(string Id, bool Trusted, string Failure);

public static class ProviderParityEvaluator
{
    public static ProviderParityResult Evaluate(ProviderParityFixture fixture)
    {
        if (fixture.SchemaVersion != 1) throw new InvalidDataException($"Unsupported provider parity schema: {fixture.SchemaVersion}.");
        return new(1, fixture.ScenarioId,
            fixture.InstalledInventory.Select(EvaluateInstalled).ToArray(),
            fixture.Updates.Select(EvaluateUpdates).ToArray(),
            fixture.Registry.Select(EvaluateRegistry).ToArray(),
            fixture.Reboot.Select(item => new RebootCanonicalResult(item.Id, item.WindowsUpdate || item.ComponentBasedServicing,
                new[] { item.WindowsUpdate ? "WindowsUpdate" : string.Empty, item.ComponentBasedServicing ? "ComponentBasedServicing" : string.Empty }
                    .Where(value => value.Length > 0).ToArray())).ToArray(),
            fixture.Trust.Select(EvaluateTrust).ToArray());
    }

    private static InventoryCanonicalResult EvaluateInstalled(WinGetInstalledFixture item)
    {
        if (!item.Available) return new(item.Id, "Unavailable", "ProviderUnavailable", []);
        if (item.ExitCode != 0) return new(item.Id, "Unavailable", "ExecutionFailed", []);
        try
        {
            var packages = WinGetInventoryParsers.ParseInstalledExport(item.ExportJson);
            var partial = WinGetInventoryParsers.DiagnosticNamesMissingCatalogPackage(
                item.Output, item.Catalog.Select(package => (package.Id, package.Name)), packages);
            return new(item.Id, partial ? "Partial" : "Complete", partial ? "PartialInventory" : "None",
                packages.Select(package => $"{package.Id}|{package.InstalledVersion}").ToArray());
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            return new(item.Id, "Malformed", "MalformedOutput", []);
        }
    }

    private static InventoryCanonicalResult EvaluateUpdates(WinGetUpdateFixture item)
    {
        if (!item.Available) return new(item.Id, "Unavailable", "ProviderUnavailable", []);
        if (item.ExitCode != 0) return new(item.Id, "Unavailable", "ExecutionFailed", []);
        try
        {
            var updates = WinGetInventoryParsers.ParseAvailableUpdates(item.Output);
            return new(item.Id, "Complete", "None", updates.Select(update => $"{update.Id}|{update.InstalledVersion}|{update.AvailableVersion}").ToArray());
        }
        catch (InvalidDataException)
        {
            return new(item.Id, "Malformed", "MalformedOutput", []);
        }
    }

    private static RegistryCanonicalResult EvaluateRegistry(RegistryFixture item)
    {
        var statuses = item.Sources.Select(source => new RegistrySourceStatus(ParseSource(source.Name), source.Available,
            source.Available ? source.Entries.Count(entry => !string.IsNullOrWhiteSpace(entry.DisplayName)) : 0, source.Detail)).ToArray();
        var records = item.Sources.Where(source => source.Available).SelectMany(source => source.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.DisplayName))
            .Select(entry => new RegistryUninstallRecord(ParseSource(source.Name), entry.DisplayName, entry.DisplayVersion))).ToArray();
        var available = statuses.Count(status => status.Available);
        var quality = available == statuses.Length ? ProviderQuality.Complete : available == 0 ? ProviderQuality.Unavailable : ProviderQuality.Partial;
        var failure = quality == ProviderQuality.Complete ? ProviderFailureKind.None : quality == ProviderQuality.Partial ? ProviderFailureKind.PartialInventory : ProviderFailureKind.ProviderUnavailable;
        var inventory = new RegistryInventoryResult(quality, failure, records, statuses, string.Empty);
        var matcher = new ExternalInventoryMatcher();
        var packages = item.Packages.Select(Package).Select(package => matcher.Match(package, inventory)).ToArray();
        return new(item.Id, quality.ToString(), failure.ToString(),
            statuses.Select(status => $"{SourceToken(status.Source)}|{status.Available}|{status.EntryCount}").ToArray(),
            records.Select(record => $"{SourceToken(record.Source)}|{record.DisplayName}|{record.DisplayVersion}").ToArray(),
            packages.Select(package => $"{package.Id}|{package.Reliable}|{package.Installed}|{package.InstalledVersion}|{package.InventoryQuality}|{string.Join('/', package.InstalledVersions)}").ToArray());
    }

    private static TrustCanonicalResult EvaluateTrust(TrustFixture item)
    {
        var result = WinGetCandidatePolicy.Evaluate(new(item.PackageName, item.PackagePublisher, item.Version,
            item.InstallLocation, item.ExecutablePath, item.FileExists, item.RegularFile, item.PathContained,
            item.ReparseFree, item.SignatureValid, item.SignerSubject));
        return new(item.Id, result.Trusted, result.Failure.ToString());
    }

    private static RegistryInventorySource ParseSource(string source) => source switch
    {
        "HKLM64" => RegistryInventorySource.Hklm64,
        "HKLM32" => RegistryInventorySource.Hklm32,
        "HKCU" => RegistryInventorySource.Hkcu,
        _ => throw new InvalidDataException($"Unsupported registry source: {source}.")
    };

    private static string SourceToken(RegistryInventorySource source) => source switch
    {
        RegistryInventorySource.Hklm64 => "HKLM64",
        RegistryInventorySource.Hklm32 => "HKLM32",
        RegistryInventorySource.Hkcu => "HKCU",
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    private static PackageDefinition Package(RegistryPackageFixture item) =>
        new(item.Id, item.Id, "Fixture", string.Empty, "Fixture", ProviderKind.External, CatalogAuthority.OperationalExternal,
            PackageProfile.Optional, PackagePriority.P2, PackageRisk.None, DeploymentPolicy.ManualHold, MaintenancePolicy.Hold,
            DeploymentClass.InventoryOnly, CatalogMaintenancePolicy.Manual, VersionRule.InventoryOnly, VersionCouplingMode.Independent,
            string.Empty, Lifecycle.Current, [ApplicationType.FieldUtility], [], [], [LicensingModel.UnknownCost], ["UNKNOWN-ACCESS"],
            DistributionPolicy.LinkOnly, [], [SupportedOperatingSystem.Windows], DeliveryMode.InventoryOnly, ReleaseMode.InventoryOnly,
            item.DetectionMode == "None" ? DetectionMode.None : DetectionMode.Registry,
            item.DetectionMode == "None" ? DetectionVersionPolicy.None : DetectionVersionPolicy.AtLeast,
            "Inventory", string.Empty, string.Empty, string.Empty, [], null, null, null, null, null, null, null, null, null,
            string.Empty, [], item.DisplayPattern, item.VersionPattern);
}

public static class ProviderLiveChecks
{
    public static async Task<object> RunAsync()
    {
        var resolver = new WindowsWinGetResolver();
        var resolution = await resolver.ResolveAsync().ConfigureAwait(false);
        var runner = new WinGetReadOnlyProcessRunner(resolver, TimeSpan.FromSeconds(30));
        var installed = await new WinGetInstalledPackageInventory(runner, []).ReadAsync().ConfigureAwait(false);
        var updates = await new WinGetAvailableUpdateInventory(runner).ReadAsync().ConfigureAwait(false);
        var registry = await new WindowsUninstallRegistryInventory().ReadAsync().ConfigureAwait(false);
        var reboot = await new WindowsRebootStateProvider().ReadAsync().ConfigureAwait(false);
        return new
        {
            WinGetResolution = resolution.Trusted ? "Available" : "Unavailable",
            WinGetResolutionDetail = resolution.Detail,
            InstalledInventory = installed.Quality.ToString(),
            InstalledFailure = installed.Failure.ToString(),
            InstalledCount = installed.Packages.Count,
            UpdateInventory = updates.Quality.ToString(),
            UpdateFailure = updates.Failure.ToString(),
            UpdateCount = updates.Updates.Count,
            RegistryInventory = registry.Quality.ToString(),
            RegistrySources = registry.Sources.Select(source => new { Source = source.Source.ToString(), source.Available, source.EntryCount }),
            RebootDetection = reboot.Quality.ToString(),
            reboot.Pending,
            Reasons = reboot.Reasons.Select(reason => reason.ToString())
        };
    }
}
