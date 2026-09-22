using System.Text.Json;
using System.Text.Json.Serialization;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Catalog;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Application.Workers;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Processes;

namespace AVWorkstationToolkit.Worker.DevHost;

internal sealed record ManagedCatalogWorkerOptions(
    int SchemaVersion,
    string ApplicationRoot,
    string DataRoot,
    string ApplicationVersion,
    string SigningKeyId,
    string PublicKeyPath)
{
    private const int MaximumFixtureBytes = 64 * 1024;

    internal static ManagedCatalogWorkerOptions Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length is <= 0 or > MaximumFixtureBytes ||
            (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The managed-catalog worker fixture is missing or unsafe.");
        var options = JsonSerializer.Deserialize<ManagedCatalogWorkerOptions>(File.ReadAllBytes(fullPath), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new InvalidDataException("The managed-catalog worker fixture is empty.");
        if (options.SchemaVersion != 1 || string.IsNullOrWhiteSpace(options.ApplicationVersion) ||
            string.IsNullOrWhiteSpace(options.SigningKeyId))
            throw new InvalidDataException("The managed-catalog worker fixture is invalid.");
        _ = RequireRegularFile(options.PublicKeyPath, "public key");
        _ = RequireDirectory(options.ApplicationRoot, "application root");
        _ = RequireDirectory(options.DataRoot, "data root");
        return options;
    }

    internal string ReadPublicKey() => File.ReadAllText(RequireRegularFile(PublicKeyPath, "public key"));

    private static string RequireRegularFile(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidDataException($"The managed-catalog {label} path must be absolute.");
        var path = Path.GetFullPath(value);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaximumFixtureBytes ||
            (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException($"The managed-catalog {label} is missing or unsafe.");
        return path;
    }

    private static string RequireDirectory(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidDataException($"The managed-catalog {label} must be absolute.");
        var path = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"The managed-catalog {label} is missing or unsafe.");
        return path;
    }
}

internal static class ManagedCatalogWorkerComposition
{
    internal static ProductionWorkerServices Create(ManagedCatalogWorkerOptions options, ActionRequest request)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [options.SigningKeyId] = options.ReadPublicKey()
        };
        var verifier = new ManagedCatalogVerifier(new(options.ApplicationVersion, keys));
        var managed = new ManagedCatalogStore(options.ApplicationRoot, options.DataRoot, verifier,
            channel: null, requireSignedBaseline: true).LoadActiveOrEmbedded();
        var plans = new VerifiedManagedCatalogPlanProvider(managed.Source.Revision, managed.Catalog, request);
        return new(plans, new RefusingManagedCatalogExecutor(), managed.Source.Revision);
    }
}

internal sealed class VerifiedManagedCatalogPlanProvider : IActionWorkerPlanProvider
{
    private readonly WorkstationPlan plan;

    internal VerifiedManagedCatalogPlanProvider(long revision, PackageCatalog catalog, ActionRequest request)
    {
        ManagedCatalogRevision = revision;
        var states = request.PackageIds
            .Select(id => catalog.Items.SingleOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .Where(item => item is not null && item.HasManagedExecutionAuthority)
            .Select(item => State(item!, request.Action))
            .ToArray();
        plan = new(states,
            new WorkstationPlanSummary(states.Length, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            RebootState.Clear,
            new ProviderRefreshSummary(ProviderQuality.Complete, ProviderQuality.Complete,
                ProviderQuality.Complete, ProviderQuality.Complete, []));
    }

    public long ManagedCatalogRevision { get; }

    public ValueTask<WorkstationPlan> ReadFreshPlanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(plan);
    }

    private static PackageState State(PackageDefinition package, ManagedRequestAction action) => action switch
    {
        ManagedRequestAction.Install => new(package, false, string.Empty, [], string.Empty, false,
            PackageStatus.Missing, "Missing", "Missing", PackageAction.Install, InventoryQuality.Complete),
        ManagedRequestAction.Update => new(package, true, "1.0", ["1.0"], "2.0", true,
            PackageStatus.UpdateAvailable, "Update available", "UpdateAvailable", PackageAction.Update, InventoryQuality.Complete),
        _ => throw new InvalidOperationException("The managed-catalog worker action is unsupported.")
    };
}

internal sealed class RefusingManagedCatalogExecutor : IPackageActionExecutor
{
    public ValueTask<PackageExecutionResult> ExecuteAsync(
        PackageExecutionRequest request,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The managed-catalog binary proof permits dry-run requests only.");
}
