using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Catalog;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.App.ViewModels;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Files;

namespace AVWorkstationToolkit.IntegrationTests;

public sealed record ManagedCatalogBinaryUpdateResult(
    string ApplicationExecutable,
    string WorkerExecutable,
    string ApplicationSha256Before,
    string ApplicationSha256After,
    string WorkerSha256Before,
    string WorkerSha256After,
    long BaselineApplicationRevision,
    long BaselineWorkerRevision,
    long ActivatedRunningRevision,
    bool RestartRequiredAfterActivation,
    long RestartedApplicationRevision,
    bool UpdatedPackagePresented,
    long UpdatedWorkerRevision,
    string UpdatedWorkerStatus,
    bool RevisionMismatchRejected,
    bool UnapprovedPackageRejected);

public sealed record ManagedCatalogAppHostResult(
    long EffectiveRevision,
    string Source,
    int PresentedPackageCount,
    bool ExpectedPackagePresented,
    string UpdateState,
    long AvailableRevision,
    bool RestartRequired,
    long RepeatedLoadRevision,
    string RecheckState,
    bool RecheckRestartRequired,
    string RepeatedActivationState,
    bool RepeatedActivationRestartRequired);

internal sealed record ManagedCatalogAppHostOptions(
    int SchemaVersion,
    string ApplicationRoot,
    string DataRoot,
    string ApplicationVersion,
    string SigningKeyId,
    string PublicKeyPath,
    string ExpectedPackageId,
    bool Activate,
    string? ChannelMetadataPath,
    string? ChannelSignaturePath,
    string? BundlePath);

internal sealed record ManagedCatalogWorkerHostOptions(
    int SchemaVersion,
    string ApplicationRoot,
    string DataRoot,
    string ApplicationVersion,
    string SigningKeyId,
    string PublicKeyPath);

public static class ManagedCatalogBinaryUpdateBoundary
{
    private const string ApplicationVersion = "1.1.1";
    private const string SigningKeyId = "managed-catalog-binary-proof-2026";
    private const string TestPackageId = "AVWT.ControlledCatalogBinaryTest";
    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<ManagedCatalogBinaryUpdateResult> RunAsync(string repositoryRoot, string workerExecutable)
    {
        var repository = RequireDirectory(repositoryRoot, "repository root");
        var worker = RequireFile(workerExecutable, "worker development host");
        var application = RequireFile(Environment.ProcessPath ?? string.Empty, "application development host");
        var publisher = RequireFile(Path.Combine(repository, "tools", "AVWorkstationToolkit.CatalogPublisher", "bin",
            "Release", "net10.0-windows", "AVWorkstationToolkit.CatalogPublisher.exe"), "catalog publisher");
        var root = Path.Combine(Path.GetTempPath(), $"avwt-managed-binary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var applicationRoot = Path.Combine(root, "application");
            var dataRoot = Path.Combine(root, "data");
            var authoringRoot = Path.Combine(root, "authoring");
            Directory.CreateDirectory(Path.Combine(applicationRoot, "manifests"));
            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(Path.Combine(authoringRoot, "manifests"));
            foreach (var name in new[]
            {
                "managed-applications.json", "external-applications.json", "commercial-av-catalog.json",
                "hardware-identities.json", "software-compatibility.json"
            })
                File.Copy(Path.Combine(repository, "manifests", name), Path.Combine(applicationRoot, "manifests", name));
            File.Copy(Path.Combine(repository, "manifests", "managed-applications.json"),
                Path.Combine(authoringRoot, "manifests", "managed-applications.json"));
            File.WriteAllText(Path.Combine(applicationRoot, "VERSION"), ApplicationVersion);
            File.WriteAllText(Path.Combine(authoringRoot, "VERSION"), ApplicationVersion);

            var updateAuthoring = JsonNode.Parse(File.ReadAllText(
                Path.Combine(authoringRoot, "manifests", "managed-applications.json")))!.AsObject();
            updateAuthoring["Packages"]!.AsArray().Add(new JsonObject
            {
                ["Profile"] = "Optional",
                ["Name"] = "Controlled Catalog Binary Test",
                ["Id"] = TestPackageId,
                ["Vendor"] = "AVWT Test",
                ["Risk"] = "None",
                ["Note"] = "Compiled process dry-run catalog update proof",
                ["Deployment"] = "Allowlisted",
                ["Maintenance"] = "Allowlisted",
                ["InstallerMode"] = "Silent"
            });
            File.WriteAllText(Path.Combine(authoringRoot, "manifests", "managed-applications.json"),
                updateAuthoring.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var privateKeyPath = Path.Combine(root, "managed-test-private.pem");
            var publicKeyPath = Path.Combine(root, "managed-test-public.pem");
            File.WriteAllText(privateKeyPath, key.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(publicKeyPath, key.ExportSubjectPublicKeyInfoPem());
            var created = DateTimeOffset.UtcNow.AddMinutes(-2);
            var baseline = await PublishAsync(publisher, repository, Path.Combine(root, "publication-1"),
                "2026.9.21.1", 1, created, privateKeyPath, previousBundle: null);
            var update = await PublishAsync(publisher, authoringRoot, Path.Combine(root, "publication-2"),
                "2026.9.21.2", 2, created.AddMinutes(1), privateKeyPath, baseline.BundlePath);
            Directory.CreateDirectory(Path.Combine(applicationRoot, "managed-catalog"));
            File.Copy(baseline.BundlePath, Path.Combine(applicationRoot,
                ManagedCatalogStore.EmbeddedBundleRelativePath.Replace('/', Path.DirectorySeparatorChar)));

            var applicationHashBefore = Hash(application);
            var workerHashBefore = Hash(worker);
            var baselineAppFixture = WriteAppFixture(root, "app-baseline.json", applicationRoot, dataRoot, publicKeyPath,
                TestPackageId, activate: false);
            var baselineApp = await RunProcessJsonAsync<ManagedCatalogAppHostResult>(application,
                ["--managed-app-host", baselineAppFixture]);
            Require(baselineApp.EffectiveRevision == 1 && !baselineApp.ExpectedPackagePresented &&
                baselineApp.PresentedPackageCount > 0, "The application development host did not present the signed revision-1 baseline.");

            var baselinePackageId = new RepositoryCatalogLoader().LoadManaged(repository).Items
                .First(item => item.HasManagedExecutionAuthority).Id;
            var workerFixture = Path.Combine(root, "worker.json");
            File.WriteAllText(workerFixture, JsonSerializer.Serialize(new ManagedCatalogWorkerHostOptions(
                1, applicationRoot, dataRoot, ApplicationVersion, SigningKeyId, publicKeyPath)));
            var baselineWorker = await RunWorkerAsync(worker, workerFixture, dataRoot, baselinePackageId, 1,
                "request-20260921-130001-00000001", expectParsableResult: true);
            Require(baselineWorker.Result!.Status == ActionResultStatus.Succeeded &&
                baselineWorker.Result.ManagedCatalogRevision == 1, "The independent worker did not authorize revision 1.");

            var activateFixture = WriteAppFixture(root, "app-activate.json", applicationRoot, dataRoot, publicKeyPath,
                TestPackageId, activate: true, update);
            var activated = await RunProcessJsonAsync<ManagedCatalogAppHostResult>(application,
                ["--managed-app-host", activateFixture]);
            Require(activated.EffectiveRevision == 1 && activated.RepeatedLoadRevision == 1 &&
                activated.RestartRequired && activated.RecheckRestartRequired && activated.RepeatedActivationRestartRequired,
                "Activation did not preserve revision 1 and the pending restart for the running application process.");

            var restartedFixture = WriteAppFixture(root, "app-restarted.json", applicationRoot, dataRoot, publicKeyPath,
                TestPackageId, activate: false);
            var restarted = await RunProcessJsonAsync<ManagedCatalogAppHostResult>(application,
                ["--managed-app-host", restartedFixture]);
            Require(restarted.EffectiveRevision == 2 && restarted.ExpectedPackagePresented,
                "The restarted application development host did not present the revision-2 package.");

            var updatedWorker = await RunWorkerAsync(worker, workerFixture, dataRoot, TestPackageId, 2,
                "request-20260921-130002-00000002", expectParsableResult: true);
            Require(updatedWorker.Result!.Status == ActionResultStatus.Succeeded &&
                updatedWorker.Result.ManagedCatalogRevision == 2 &&
                updatedWorker.Result.Packages.Single().Status == PackageOutcomeStatus.Planned,
                "The independent worker did not dry-run authorize the revision-2 package.");

            var mismatch = await RunWorkerAsync(worker, workerFixture, dataRoot, TestPackageId, 1,
                "request-20260921-130003-00000003", expectParsableResult: false);
            Require(mismatch.ExitCode == 1 && mismatch.ParseFailure == ActionProtocolFailure.RequestMismatch &&
                mismatch.RawRevision == 2, "A mismatched request revision was not rejected using the worker's verified revision.");

            var unknown = await RunWorkerAsync(worker, workerFixture, dataRoot, "AVWT.UnapprovedBinaryRequest", 2,
                "request-20260921-130004-00000004", expectParsableResult: true);
            Require(unknown.ExitCode == 1 && unknown.Result!.Status == ActionResultStatus.Rejected &&
                unknown.Result.ManagedCatalogRevision == 2,
                "An unapproved package was not rejected before mutation by the revision-2 worker.");

            var applicationHashAfter = Hash(application);
            var workerHashAfter = Hash(worker);
            Require(applicationHashBefore == applicationHashAfter && workerHashBefore == workerHashAfter,
                "An application or worker development-host executable changed during catalog activation.");
            return new(application, worker, applicationHashBefore, applicationHashAfter, workerHashBefore, workerHashAfter,
                baselineApp.EffectiveRevision, baselineWorker.Result.ManagedCatalogRevision, activated.EffectiveRevision,
                activated.RestartRequired, restarted.EffectiveRevision, restarted.ExpectedPackagePresented,
                updatedWorker.Result.ManagedCatalogRevision, updatedWorker.Result.Status.ToString(), true, true);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    public static async Task<ManagedCatalogAppHostResult> RunAppHostAsync(string fixturePath)
    {
        var options = JsonSerializer.Deserialize<ManagedCatalogAppHostOptions>(
            File.ReadAllBytes(RequireFile(fixturePath, "application-host fixture")), StrictJson)
            ?? throw new InvalidDataException("The application-host fixture is empty.");
        if (options.SchemaVersion != 1) throw new InvalidDataException("The application-host fixture schema is unsupported.");
        var keys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [options.SigningKeyId] = File.ReadAllText(RequireFile(options.PublicKeyPath, "development public key"))
        };
        var verifier = new ManagedCatalogVerifier(new(options.ApplicationVersion, keys));
        IManagedCatalogChannelClient? channel = null;
        if (options.Activate)
        {
            var publication = verifier.VerifyPublication(
                RequireFile(options.ChannelMetadataPath ?? string.Empty, "development channel metadata"),
                RequireFile(options.ChannelSignaturePath ?? string.Empty, "development channel signature"),
                RequireFile(options.BundlePath ?? string.Empty, "development catalog bundle"),
                DateTimeOffset.UtcNow);
            channel = new StaticVerifiedChannel(new(publication.Channel.Revision, publication.Channel.PreviousRevision,
                publication.Channel.CatalogVersion, publication.Channel.MinimumAppVersion, publication.Channel.CreatedUtc,
                publication.Channel.SigningKeyId, publication.Channel.BundleSha256, File.ReadAllBytes(options.BundlePath!)));
        }
        var services = CompiledAppComposition.CreateManagedCatalogDevelopment(
            RequireDirectory(options.ApplicationRoot, "application root"),
            RequireDirectory(options.DataRoot, "data root"), options.ApplicationVersion, new(verifier, channel));
        using var presentation = new MainWindowViewModel(new CatalogPresentationCoordinator(services.Catalog),
            managedCatalogUpdates: services.ManagedCatalogUpdates, searchDebounce: TimeSpan.Zero);
        await presentation.RefreshAsync();
        var updateState = services.ManagedCatalogUpdates.Status;
        var repeated = new ManagedCatalogSet(services.Catalog, new(services.ManagedCatalogRevision, string.Empty, false, string.Empty));
        var recheck = updateState;
        var repeatedActivation = updateState;
        if (options.Activate)
        {
            updateState = await services.ManagedCatalogUpdates.CheckAsync();
            Require(updateState.State == ManagedCatalogUpdateState.UpdateAvailable && updateState.Verified,
                "The application development host did not verify the revision-2 update.");
            updateState = await services.ManagedCatalogUpdates.InstallAvailableAsync();
            repeated = services.ManagedCatalogUpdates.LoadActiveOrEmbedded();
            recheck = await services.ManagedCatalogUpdates.CheckAsync();
            repeatedActivation = await services.ManagedCatalogUpdates.InstallAvailableAsync();
        }
        return new(services.ManagedCatalogRevision, services.ManagedCatalogUpdates.Status.Source,
            presentation.Packages.Count, presentation.Packages.Any(item => item.Id == options.ExpectedPackageId),
            updateState.State.ToString(), updateState.AvailableRevision, updateState.RestartRequired,
            repeated.Source.Revision, recheck.State.ToString(), recheck.RestartRequired,
            repeatedActivation.State.ToString(), repeatedActivation.RestartRequired);
    }

    private static string WriteAppFixture(string root, string name, string applicationRoot, string dataRoot,
        string publicKeyPath, string expectedPackageId, bool activate, PublishedCatalog? update = null)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, JsonSerializer.Serialize(new ManagedCatalogAppHostOptions(
            1, applicationRoot, dataRoot, ApplicationVersion, SigningKeyId, publicKeyPath, expectedPackageId, activate,
            update?.ChannelMetadataPath, update?.ChannelSignaturePath, update?.BundlePath)));
        return path;
    }

    private static async Task<PublishedCatalog> PublishAsync(string publisher, string authoringRoot, string outputRoot,
        string version, long revision, DateTimeOffset created, string privateKeyPath, string? previousBundle)
    {
        var arguments = new List<string>
        {
            "managed", "--repository-root", authoringRoot, "--output-root", outputRoot,
            "--version", version, "--revision", revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--minimum-app-version", ApplicationVersion, "--created-utc", created.ToString("o"),
            "--signing-key-id", SigningKeyId, "--private-key", privateKeyPath,
            "--public-base-uri", "https://catalog.example.test/managed/"
        };
        if (previousBundle is not null)
        {
            arguments.Add("--previous-catalog");
            arguments.Add(previousBundle);
        }
        var result = await RunProcessAsync(publisher, arguments);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Managed catalog publisher failed: {result.StandardError}");
        var bundles = Directory.GetFiles(Path.Combine(outputRoot, "catalogs", revision.ToString(
            System.Globalization.CultureInfo.InvariantCulture)), "*.avwtmanaged", SearchOption.TopDirectoryOnly);
        if (bundles.Length != 1) throw new InvalidDataException("Managed catalog publisher emitted an unexpected bundle set.");
        return new(bundles[0],
            Path.Combine(outputRoot, "stable", ManagedCatalogBundleNames.ChannelMetadata),
            Path.Combine(outputRoot, "stable", ManagedCatalogBundleNames.ChannelSignature));
    }

    private static async Task<WorkerRunEvidence> RunWorkerAsync(string worker, string fixture, string dataRoot,
        string packageId, long revision, string requestId, bool expectParsableResult)
    {
        var request = new ActionRequest(ActionRequestRules.CurrentSchemaVersion, requestId,
            ManagedRequestAction.Install, [packageId], false, true, revision);
        var store = new ActionProtocolStore(dataRoot);
        var paths = await store.PersistRequestAsync(new AuthorizedActionRequest(request, [RequestState(packageId)]));
        var process = await RunProcessAsync(worker,
            ["--managed-catalog-test", "--fixture", fixture, "--request", paths.RequestPath]);
        var payload = await store.ReadArtifactAsync(requestId, ActionArtifactKind.Result);
        ActionFinalResult? result = null;
        ActionProtocolFailure? failure = null;
        try { result = new ActionResultCodec().Parse(payload, request, paths); }
        catch (ActionProtocolValidationException exception) { failure = exception.Failure; }
        if (expectParsableResult && result is null)
            throw new InvalidDataException($"The worker result was not correlated: {failure}. {process.StandardError}");
        if (!expectParsableResult && failure is null)
            throw new InvalidDataException("The deliberately mismatched worker result was accepted.");
        var node = JsonNode.Parse(payload)!.AsObject();
        return new(process.ExitCode, result, failure, node["ManagedCatalogRevision"]!.GetValue<long>());
    }

    private static PackageState RequestState(string id)
    {
        var package = new PackageDefinition(id, id, "Integration fixture", string.Empty, "Protocol request fixture",
            ProviderKind.WinGet, CatalogAuthority.ManagedWinGet, PackageProfile.Standard, PackagePriority.P2,
            PackageRisk.None, DeploymentPolicy.Allowlisted, MaintenancePolicy.Allowlisted, DeploymentClass.Managed,
            CatalogMaintenancePolicy.Managed, VersionRule.Latest, VersionCouplingMode.Independent, string.Empty,
            Lifecycle.Current, [], [], [], [LicensingModel.Free], ["PUBLIC-DL"], DistributionPolicy.PackageManagerOnly,
            [InstallationForm.WinGet], [SupportedOperatingSystem.Windows], DeliveryMode.None, ReleaseMode.None,
            DetectionMode.WinGet, DetectionVersionPolicy.None, "Stable", string.Empty, string.Empty, string.Empty, [],
            null, null, null, null, null, false, false, false, null, string.Empty, []);
        return new(package, false, string.Empty, [], string.Empty, false, PackageStatus.Missing,
            "Missing", "Missing", PackageAction.Install, InventoryQuality.Complete);
    }

    private static async Task<T> RunProcessJsonAsync<T>(string executable, IReadOnlyList<string> arguments)
    {
        var result = await RunProcessAsync(executable, arguments);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Development host failed with exit code {result.ExitCode}: {result.StandardError}");
        var line = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
            ?? throw new InvalidDataException("Development host emitted no JSON result.");
        return JsonSerializer.Deserialize<T>(line, StrictJson)
            ?? throw new InvalidDataException("Development host emitted an empty JSON result.");
    }

    private static async Task<ProcessEvidence> RunProcessAsync(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Development host did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("Development host did not exit within 30 seconds.");
        }
        return new(process.ExitCode, await standardOutput, await standardError);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string RequireDirectory(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new DirectoryNotFoundException($"The {label} must be absolute.");
        var path = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new DirectoryNotFoundException($"The {label} is missing or unsafe.");
        return path;
    }

    private static string RequireFile(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new FileNotFoundException($"The {label} path must be absolute.");
        var path = Path.GetFullPath(value);
        if (!File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new FileNotFoundException($"The {label} is missing or unsafe.", path);
        return path;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed record ProcessEvidence(int ExitCode, string StandardOutput, string StandardError);
    private sealed record PublishedCatalog(string BundlePath, string ChannelMetadataPath, string ChannelSignaturePath);
    private sealed record WorkerRunEvidence(int ExitCode, ActionFinalResult? Result,
        ActionProtocolFailure? ParseFailure, long RawRevision);

    private sealed class StaticVerifiedChannel(ManagedCatalogChannelPackage package) : IManagedCatalogChannelClient
    {
        public Task<ManagedCatalogChannelPackage?> GetLatestAsync(long currentRevision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ManagedCatalogChannelPackage?>(package.Revision > currentRevision ? package : null);
    }

    private sealed class CatalogPresentationCoordinator : IWorkstationPlanningCoordinator
    {
        private readonly WorkstationPlan plan;

        internal CatalogPresentationCoordinator(PackageCatalog catalog)
        {
            Catalog = catalog;
            var packages = catalog.Items.Select(item => item.HasManagedExecutionAuthority
                ? new PackageState(item, false, string.Empty, [], string.Empty, false, PackageStatus.Missing,
                    "Available", "AllowlistedInstallAvailable", PackageAction.Install, InventoryQuality.Complete)
                : new PackageState(item, false, string.Empty, [], string.Empty, false, PackageStatus.Awareness,
                    "Catalog awareness", "Awareness", PackageAction.None, InventoryQuality.Complete)).ToArray();
            plan = new(packages, new WorkstationPlanSummary(packages.Length, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                RebootState.Clear,
                new ProviderRefreshSummary(ProviderQuality.Complete, ProviderQuality.Complete,
                    ProviderQuality.Complete, ProviderQuality.Complete, []));
        }

        public PackageCatalog Catalog { get; }

        public Task<WorkstationPlan> RefreshAsync(IProgress<PlanningRefreshStage>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(PlanningRefreshStage.Ready);
            return Task.FromResult(plan);
        }
    }
}
