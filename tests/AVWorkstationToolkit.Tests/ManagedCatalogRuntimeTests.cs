using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Catalog;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Application.Workers;
using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.App.ViewModels;
using AVWorkstationToolkit.CatalogPublisher;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Processes;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class ManagedCatalogRuntimeTests
{
    private const string TestPackageId = "AVWT.ControlledCatalogTest";

    [TestMethod]
    public async Task SignedUpdateChangesAppAndWorkerAuthorityWithoutChangingBinaries()
    {
        using var fixture = RuntimeFixture.Create();
        var appAssembly = typeof(CompiledAppComposition).Assembly.Location;
        var workerCompositionAssembly = typeof(ProductionWorkerComposition).Assembly.Location;
        var initialHashes = new[] { Hash(appAssembly), Hash(workerCompositionAssembly) };

        var baselineServices = CompiledAppComposition.CreateManagedCatalogDevelopment(
            fixture.ApplicationRoot, fixture.DataRoot, fixture.ApplicationVersion, fixture.RuntimeServices);
        Assert.AreEqual(1L, baselineServices.ManagedCatalogRevision);
        Assert.IsFalse(baselineServices.Catalog.Items.Any(item => item.Id == TestPackageId));

        var status = await baselineServices.ManagedCatalogUpdates.CheckAsync();
        Assert.AreEqual(ManagedCatalogUpdateState.UpdateAvailable, status.State);
        Assert.IsTrue(status.Verified);
        Assert.AreEqual(2L, status.AvailableRevision);
        status = await baselineServices.ManagedCatalogUpdates.InstallAvailableAsync();
        Assert.AreEqual(ManagedCatalogUpdateState.Completed, status.State);
        Assert.IsTrue(status.RestartRequired);
        Assert.AreEqual(1L, baselineServices.ManagedCatalogRevision, "A running application must not switch authority mid-session.");

        var restarted = CompiledAppComposition.CreateManagedCatalogDevelopment(
            fixture.ApplicationRoot, fixture.DataRoot, fixture.ApplicationVersion, fixture.RuntimeServices);
        Assert.AreEqual(2L, restarted.ManagedCatalogRevision);
        Assert.IsTrue(restarted.Catalog.Items.Any(item => item.Id == TestPackageId),
            "The restarted application did not consume the independently activated revision.");

        var workerCatalog = new ManagedCatalogStore(
            fixture.ApplicationRoot, fixture.DataRoot, fixture.RuntimeServices.Verifier, channel: null, requireSignedBaseline: true)
            .LoadActiveOrEmbedded();
        Assert.AreEqual(2L, workerCatalog.Source.Revision);
        var package = workerCatalog.Catalog.Items.Single(item => item.Id == TestPackageId);
        var plan = Plan(package);
        using (var presentation = new MainWindowViewModel(new FixedCoordinator(plan), searchDebounce: TimeSpan.Zero))
        {
            await presentation.RefreshAsync();
            Assert.IsTrue(presentation.Packages.Any(item => item.Id == TestPackageId),
                "The application presentation did not display the newly approved package.");
        }
        var protocol = new MemoryProtocol();
        var worker = new ActionWorkerOrchestrator(new FixedPlanProvider(2, plan), new RefusingExecutor(), protocol, "FixtureHost");
        var accepted = await worker.RunAsync(Request(TestPackageId, 2));
        Assert.AreEqual(ActionResultStatus.Succeeded, accepted.Status);
        Assert.AreEqual(PackageOutcomeStatus.Planned, accepted.Packages.Single().Status);

        var mismatchedProtocol = new MemoryProtocol();
        var mismatchedPlans = new FixedPlanProvider(2, plan);
        var mismatched = await new ActionWorkerOrchestrator(mismatchedPlans, new RefusingExecutor(), mismatchedProtocol, "FixtureHost")
            .RunAsync(Request(TestPackageId, 1));
        Assert.AreEqual(ActionResultStatus.Rejected, mismatched.Status);
        Assert.AreEqual(0, mismatchedPlans.ReadCount, "Revision mismatch must fail before package planning.");
        StringAssert.Contains(mismatchedProtocol.Result!.Message, "independently verified revision 2");

        var unknown = await new ActionWorkerOrchestrator(new FixedPlanProvider(2, plan), new RefusingExecutor(), new MemoryProtocol(), "FixtureHost")
            .RunAsync(Request("AVWT.UnsignedRequestSubstitution", 2));
        Assert.AreEqual(ActionResultStatus.Rejected, unknown.Status);

        CollectionAssert.AreEqual(initialHashes, new[] { Hash(appAssembly), Hash(workerCompositionAssembly) },
            "Catalog activation changed compiled application or worker-composition bytes.");
    }

    [TestMethod]
    public async Task InvalidRetainedCatalogFallsBackToBaselineAndOfflineCheckPreservesIt()
    {
        using var fixture = RuntimeFixture.Create();
        var store = new ManagedCatalogStore(fixture.ApplicationRoot, fixture.DataRoot, fixture.RuntimeServices.Verifier,
            fixture.RuntimeServices.ChannelClient, requireSignedBaseline: true);
        _ = store.LoadActiveOrEmbedded();
        _ = await store.CheckAsync();
        _ = await store.InstallAvailableAsync();

        var retained = Directory.GetFiles(Path.Combine(fixture.DataRoot, "ManagedCatalog", "catalogs", "2"), "*.avwtmanaged").Single();
        var bytes = File.ReadAllBytes(retained);
        bytes[^1] ^= 0x5A;
        File.WriteAllBytes(retained, bytes);
        var recovered = new ManagedCatalogStore(fixture.ApplicationRoot, fixture.DataRoot, fixture.RuntimeServices.Verifier,
            new ThrowingChannel(), requireSignedBaseline: true);
        var selected = recovered.LoadActiveOrEmbedded();
        Assert.AreEqual(1L, selected.Source.Revision);
        Assert.IsFalse(selected.Catalog.Items.Any(item => item.Id == TestPackageId));
        var status = await recovered.CheckAsync();
        Assert.AreEqual(ManagedCatalogUpdateState.Offline, status.State);
        Assert.AreEqual(1L, status.CurrentRevision);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.DataRoot, "ManagedCatalog", "catalogs", "2")));
    }

    [TestMethod]
    public void ProductionManagedAuthorityFailsClosedUntilOwnerProvisioned()
    {
        Assert.AreEqual(0, ProductionManagedCatalogConfiguration.TrustedPublicKeyCount);
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => ProductionManagedCatalogConfiguration.Create("1.1.1"));
        StringAssert.Contains(exception.Message, "owner-provisioned");
    }

    [TestMethod]
    public void PackagedStoreRequiresItsSignedEmbeddedBaseline()
    {
        using var fixture = RuntimeFixture.Create();
        File.Delete(Path.Combine(fixture.ApplicationRoot,
            ManagedCatalogStore.EmbeddedBundleRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var store = new ManagedCatalogStore(fixture.ApplicationRoot, fixture.DataRoot, fixture.RuntimeServices.Verifier,
            channel: null, requireSignedBaseline: true);
        var exception = Assert.ThrowsExactly<CatalogValidationException>(() => store.LoadActiveOrEmbedded());
        StringAssert.Contains(exception.Message, "signed embedded");
    }

    [TestMethod]
    public async Task ManagedChannelUsesOnlyItsFixedApprovedOriginAndVerifiedBytes()
    {
        using var fixture = RuntimeFixture.Create();
        var metadataUri = new Uri("https://catalog.example.test/managed/stable/managed-catalog-channel.json");
        var signatureUri = new Uri("https://catalog.example.test/managed/stable/managed-catalog-channel.sig");
        using var handler = new MappingHandler(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [metadataUri.AbsoluteUri] = File.ReadAllBytes(fixture.UpdateChannelMetadataPath),
            [signatureUri.AbsoluteUri] = File.ReadAllBytes(fixture.UpdateChannelSignaturePath),
            [fixture.UpdateBundleUri.AbsoluteUri] = File.ReadAllBytes(fixture.UpdateBundlePath)
        });
        using var client = new ManagedCatalogChannelClient(new(metadataUri, signatureUri,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "catalog.example.test" }, fixture.TrustedKeys,
            fixture.ApplicationVersion, TimeSpan.FromSeconds(5)), handler, ownsHandler: false);
        var package = await client.GetLatestAsync(1);
        Assert.IsNotNull(package);
        Assert.AreEqual(2L, package.Revision);
        CollectionAssert.AreEqual(new[] { metadataUri, signatureUri, fixture.UpdateBundleUri }, handler.Requests.ToArray());

        var foreignMetadata = new Uri("https://metadata.example.test/managed-catalog-channel.json");
        var foreignSignature = new Uri("https://metadata.example.test/managed-catalog-channel.sig");
        using var foreignHandler = new MappingHandler(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [foreignMetadata.AbsoluteUri] = File.ReadAllBytes(fixture.UpdateChannelMetadataPath),
            [foreignSignature.AbsoluteUri] = File.ReadAllBytes(fixture.UpdateChannelSignaturePath)
        });
        using var foreign = new ManagedCatalogChannelClient(new(foreignMetadata, foreignSignature,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "metadata.example.test" }, fixture.TrustedKeys,
            fixture.ApplicationVersion, TimeSpan.FromSeconds(5)), foreignHandler, ownsHandler: false);
        await Assert.ThrowsExactlyAsync<CatalogValidationException>(() => foreign.GetLatestAsync(1));
        Assert.HasCount(2, foreignHandler.Requests);
    }

    [TestMethod]
    public async Task RuntimeRejectsSignedChannelAndBundleMetadataMismatch()
    {
        using var fixture = RuntimeFixture.Create();
        var published = await fixture.RuntimeServices.ChannelClient!.GetLatestAsync(1)
            ?? throw new AssertFailedException("The fixture update was unavailable.");
        var mismatched = published with { CreatedUtc = published.CreatedUtc.AddSeconds(1) };
        var store = new ManagedCatalogStore(fixture.ApplicationRoot, fixture.DataRoot, fixture.RuntimeServices.Verifier,
            new StaticChannel(mismatched), requireSignedBaseline: true);
        _ = store.LoadActiveOrEmbedded();

        var status = await store.CheckAsync();

        Assert.AreEqual(ManagedCatalogUpdateState.Rejected, status.State);
        Assert.IsFalse(status.Verified);
        StringAssert.Contains(status.Detail, "does not match");
    }

    private static ActionRequest Request(string packageId, long revision) =>
        new(ActionRequestRules.CurrentSchemaVersion, "request-20260921-120000-0123abcd", ManagedRequestAction.Install,
            [packageId], false, true, revision);

    private static WorkstationPlan Plan(PackageDefinition package)
    {
        var state = new PackageState(package, false, string.Empty, [], string.Empty, false, PackageStatus.Missing,
            "Missing", "Missing", PackageAction.Install, InventoryQuality.Complete);
        return new([state], new WorkstationPlanSummary(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            RebootState.Clear,
            new ProviderRefreshSummary(ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, []));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class FixedPlanProvider(long revision, WorkstationPlan plan) : IActionWorkerPlanProvider
    {
        public long ManagedCatalogRevision { get; } = revision;
        public int ReadCount { get; private set; }
        public ValueTask<WorkstationPlan> ReadFreshPlanAsync(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return ValueTask.FromResult(plan);
        }
    }

    private sealed class FixedCoordinator(WorkstationPlan plan) : IWorkstationPlanningCoordinator
    {
        public PackageCatalog Catalog { get; } = new(plan.Packages.Select(item => item.Package));
        public Task<WorkstationPlan> RefreshAsync(IProgress<PlanningRefreshStage>? progress = null, CancellationToken cancellationToken = default)
        {
            progress?.Report(PlanningRefreshStage.Ready);
            return Task.FromResult(plan);
        }
    }

    private sealed class RefusingExecutor : IPackageActionExecutor
    {
        public ValueTask<PackageExecutionResult> ExecuteAsync(PackageExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Dry-run managed-catalog proof must not invoke a mutation provider.");
    }

    private sealed class MemoryProtocol : IActionWorkerProtocol
    {
        public ActionFinalResult? Result { get; private set; }
        public ActionArtifactPaths Paths { get; } = new("request-20260921-120000-0123abcd", "request", "progress", "result", "cancel", "log");
        public ValueTask InitializeAsync(ActionRequest request, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<bool> IsCancellationRequestedAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        public ValueTask AppendProgressAsync(ActionRequest request, ActionProgressRecord record, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask PersistFinalResultAsync(ActionRequest request, ActionFinalResult result, CancellationToken cancellationToken = default)
        {
            Result = result;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StaticChannel(ManagedCatalogChannelPackage package) : IManagedCatalogChannelClient
    {
        public Task<ManagedCatalogChannelPackage?> GetLatestAsync(long currentRevision, CancellationToken cancellationToken = default) =>
            Task.FromResult<ManagedCatalogChannelPackage?>(package.Revision > currentRevision ? package : null);
    }

    private sealed class ThrowingChannel : IManagedCatalogChannelClient
    {
        public Task<ManagedCatalogChannelPackage?> GetLatestAsync(long currentRevision, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("offline fixture");
    }

    private sealed class MappingHandler(IReadOnlyDictionary<string, byte[]> responses) : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (!responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var bytes))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class RuntimeFixture : IDisposable
    {
        private readonly string root;
        private RuntimeFixture(string root, string applicationRoot, string dataRoot, string applicationVersion,
            ManagedCatalogRuntimeServices runtimeServices, IReadOnlyDictionary<string, string> trustedKeys,
            string updateChannelMetadataPath, string updateChannelSignaturePath, string updateBundlePath, Uri updateBundleUri)
        {
            this.root = root;
            ApplicationRoot = applicationRoot;
            DataRoot = dataRoot;
            ApplicationVersion = applicationVersion;
            RuntimeServices = runtimeServices;
            TrustedKeys = trustedKeys;
            UpdateChannelMetadataPath = updateChannelMetadataPath;
            UpdateChannelSignaturePath = updateChannelSignaturePath;
            UpdateBundlePath = updateBundlePath;
            UpdateBundleUri = updateBundleUri;
        }

        internal string ApplicationRoot { get; }
        internal string DataRoot { get; }
        internal string ApplicationVersion { get; }
        internal ManagedCatalogRuntimeServices RuntimeServices { get; }
        internal IReadOnlyDictionary<string, string> TrustedKeys { get; }
        internal string UpdateChannelMetadataPath { get; }
        internal string UpdateChannelSignaturePath { get; }
        internal string UpdateBundlePath { get; }
        internal Uri UpdateBundleUri { get; }

        internal static RuntimeFixture Create()
        {
            var repository = RepositoryRootLocator.Find();
            var root = Path.Combine(Path.GetTempPath(), $"avwt-managed-runtime-{Guid.NewGuid():N}");
            var application = Path.Combine(root, "application");
            var data = Path.Combine(root, "data");
            var authoring = Path.Combine(root, "authoring");
            Directory.CreateDirectory(Path.Combine(application, "manifests"));
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(Path.Combine(authoring, "manifests"));
            foreach (var name in new[] { "managed-applications.json", "external-applications.json", "commercial-av-catalog.json", "hardware-identities.json", "software-compatibility.json" })
                File.Copy(Path.Combine(repository, "manifests", name), Path.Combine(application, "manifests", name));
            File.Copy(Path.Combine(repository, "manifests", "managed-applications.json"), Path.Combine(authoring, "manifests", "managed-applications.json"));
            File.WriteAllText(Path.Combine(application, "VERSION"), "1.1.1");
            File.WriteAllText(Path.Combine(authoring, "VERSION"), "1.1.1");

            var managed = JsonNode.Parse(File.ReadAllText(Path.Combine(authoring, "manifests", "managed-applications.json")))!.AsObject();
            managed["Packages"]!.AsArray().Add(new JsonObject
            {
                ["Profile"] = "Optional",
                ["Name"] = "Controlled Catalog Test",
                ["Id"] = TestPackageId,
                ["Vendor"] = "AVWT Test",
                ["Risk"] = "None",
                ["Note"] = "Dry-run only managed catalog update fixture",
                ["Deployment"] = "Allowlisted",
                ["Maintenance"] = "Allowlisted",
                ["InstallerMode"] = "Silent"
            });
            File.WriteAllText(Path.Combine(authoring, "manifests", "managed-applications.json"),
                managed.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var keyPath = Path.Combine(root, "test-managed-private.pem");
            File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
            const string keyId = "test-managed-runtime-2026";
            var publisher = new ManagedCatalogPublisher();
            var publishedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
            var baseline = publisher.Publish(new(repository, Path.Combine(root, "publication-1"), "2026.9.21.1", 1, "1.1.1",
                publishedAt, keyId, keyPath, new Uri("https://catalog.example.test/managed/")));
            var update = publisher.Publish(new(authoring, Path.Combine(root, "publication-2"), "2026.9.21.2", 2, "1.1.1",
                publishedAt.AddMinutes(1), keyId, keyPath, new Uri("https://catalog.example.test/managed/"), baseline.BundlePath));
            Directory.CreateDirectory(Path.Combine(application, "managed-catalog"));
            File.Copy(baseline.BundlePath, Path.Combine(application, ManagedCatalogStore.EmbeddedBundleRelativePath.Replace('/', Path.DirectorySeparatorChar)));

            var keys = new Dictionary<string, string>(StringComparer.Ordinal) { [keyId] = key.ExportSubjectPublicKeyInfoPem() };
            var verifier = new ManagedCatalogVerifier(new("1.1.1", keys));
            var publication = verifier.VerifyPublication(update.ChannelMetadataPath, update.ChannelSignaturePath, update.BundlePath, DateTimeOffset.UtcNow);
            var bytes = File.ReadAllBytes(update.BundlePath);
            var channel = new StaticChannel(new(publication.Channel.Revision, publication.Channel.PreviousRevision,
                publication.Channel.CatalogVersion, publication.Channel.MinimumAppVersion, publication.Channel.CreatedUtc,
                publication.Channel.SigningKeyId, publication.Channel.BundleSha256, bytes));
            return new(root, application, data, "1.1.1", new(verifier, channel), keys,
                update.ChannelMetadataPath, update.ChannelSignaturePath, update.BundlePath, publication.Channel.BundleUri);
        }

        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
