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

        var rechecked = await baselineServices.ManagedCatalogUpdates.CheckAsync();
        Assert.AreEqual(ManagedCatalogUpdateState.Completed, rechecked.State);
        Assert.IsTrue(rechecked.RestartRequired, "Rechecking must not hide the pending restart.");
        var repeatedLoad = baselineServices.ManagedCatalogUpdates.LoadActiveOrEmbedded();
        Assert.AreEqual(1L, repeatedLoad.Source.Revision, "Repeated reads must retain the process-effective revision.");
        Assert.IsFalse(repeatedLoad.Catalog.Items.Any(item => item.Id == TestPackageId));
        var repeatedActivation = await baselineServices.ManagedCatalogUpdates.InstallAvailableAsync();
        Assert.AreEqual(ManagedCatalogUpdateState.Completed, repeatedActivation.State);
        Assert.IsTrue(repeatedActivation.RestartRequired, "Repeated activation must retain the pending restart.");
        var updates = new ManagedCatalogUpdateViewModel(baselineServices.ManagedCatalogUpdates);
        Assert.IsFalse(updates.CheckNowCommand.CanExecute(null));
        Assert.IsFalse(updates.InstallCommand.CanExecute(null));

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
        Assert.AreEqual(2L, protocol.Result!.ManagedCatalogRevision);

        var mismatchedProtocol = new MemoryProtocol();
        var mismatchedPlans = new FixedPlanProvider(2, plan);
        var mismatched = await new ActionWorkerOrchestrator(mismatchedPlans, new RefusingExecutor(), mismatchedProtocol, "FixtureHost")
            .RunAsync(Request(TestPackageId, 1));
        Assert.AreEqual(ActionResultStatus.Rejected, mismatched.Status);
        Assert.AreEqual(0, mismatchedPlans.ReadCount, "Revision mismatch must fail before package planning.");
        StringAssert.Contains(mismatchedProtocol.Result!.Message, "independently verified revision 2");
        Assert.AreEqual(2L, mismatchedProtocol.Result.ManagedCatalogRevision,
            "A rejected mismatched request must still report the worker's independently verified revision.");

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
    public void StartupKeepsARetainedRevisionThatNeedsANewerRelease()
    {
        // Every installed release shares the data root. An older release must neither delete a newer release's
        // revision during verification nor during cleanup, which removes only revisions older than its selection.
        using var fixture = RuntimeFixture.Create();
        var stored = Path.Combine(fixture.DataRoot, "ManagedCatalog", "catalogs", "2");
        Directory.CreateDirectory(stored);
        File.Copy(fixture.PublishRevision(2, "1.2.0"),
            Path.Combine(stored, Path.GetFileName(ManagedCatalogStore.EmbeddedBundleRelativePath)));

        Assert.AreEqual(1L, NewStore(fixture).LoadActiveOrEmbedded().Source.Revision);
        Assert.IsTrue(Directory.Exists(stored), "An older release deleted a revision that a newer release can use.");

        var newer = new ManagedCatalogStore(fixture.ApplicationRoot, fixture.DataRoot,
            new ManagedCatalogVerifier(new("1.2.0", fixture.TrustedKeys)), channel: null, requireSignedBaseline: true);
        Assert.AreEqual(2L, newer.LoadActiveOrEmbedded().Source.Revision);
    }

    [TestMethod]
    public async Task StartupSkipsARetainedRevisionHeldOpenByAnotherProcess()
    {
        using var fixture = RuntimeFixture.Create();
        var activating = NewStore(fixture, fixture.RuntimeServices.ChannelClient);
        _ = activating.LoadActiveOrEmbedded();
        _ = await activating.CheckAsync();
        Assert.AreEqual(ManagedCatalogUpdateState.Completed, (await activating.InstallAvailableAsync()).State);
        var retained = Directory.GetFiles(Path.Combine(fixture.DataRoot, "ManagedCatalog", "catalogs", "2"), "*.avwtmanaged").Single();

        // The handle denies reads but permits deletion, so it cannot itself stop a store that wrongly deletes the file.
        using (new FileStream(retained, FileMode.Open, FileAccess.Read, FileShare.Delete))
            Assert.AreEqual(1L, NewStore(fixture).LoadActiveOrEmbedded().Source.Revision);

        Assert.IsTrue(File.Exists(retained), "A temporarily locked retained catalog was deleted.");
        Assert.AreEqual(2L, NewStore(fixture).LoadActiveOrEmbedded().Source.Revision);
    }

    [TestMethod]
    public async Task ActivationAlreadyMadeByAnotherInstanceOnlyRequiresRestart()
    {
        using var fixture = RuntimeFixture.Create();
        var first = NewStore(fixture, fixture.RuntimeServices.ChannelClient);
        var second = NewStore(fixture, fixture.RuntimeServices.ChannelClient);
        foreach (var store in new[] { first, second })
        {
            _ = store.LoadActiveOrEmbedded();
            Assert.AreEqual(ManagedCatalogUpdateState.UpdateAvailable, (await store.CheckAsync()).State);
        }
        Assert.AreEqual(ManagedCatalogUpdateState.Completed, (await first.InstallAvailableAsync()).State);

        var status = await second.InstallAvailableAsync();

        Assert.AreEqual(ManagedCatalogUpdateState.Completed, status.State);
        Assert.IsTrue(status.RestartRequired);
        Assert.AreEqual(2L, status.AvailableRevision);
        Assert.AreEqual(1L, second.LoadActiveOrEmbedded().Source.Revision);
    }

    [TestMethod]
    public void ProductionManagedPublicKeyIsTheExactOwnerApprovedP256TrustAnchor()
    {
        var services = ProductionManagedCatalogConfiguration.Create("1.1.1");
        var anchors = ProductionManagedCatalogTrustAnchors.All;

        Assert.AreEqual(1, ProductionManagedCatalogConfiguration.TrustedPublicKeyCount);
        Assert.HasCount(1, anchors);
        Assert.AreEqual("avwt-managed-2026-a", anchors.Keys.Single());
        Assert.IsTrue(anchors.TryGetValue(ProductionManagedCatalogConfiguration.PrimarySigningKeyId, out var pem));
        Assert.IsFalse(anchors.ContainsKey(ProductionReferenceCatalogConfiguration.PrimarySigningKeyId));

        using var key = ECDsa.Create();
        key.ImportFromPem(pem);
        Assert.AreEqual(256, key.KeySize);
        Assert.AreEqual("1.2.840.10045.3.1.7", key.ExportParameters(false).Curve.Oid.Value);
        Assert.AreEqual(
            "6AF28934AAF117BA9A8967D691DDC23013EABF4F687BAE8A411D8CF6EF50F651",
            Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())),
            "The production managed SPKI fingerprint changed without an explicit owner public-key handoff.");

        using var referenceKey = ECDsa.Create();
        referenceKey.ImportFromPem(ProductionReferenceCatalogTrustAnchors.All[
            ProductionReferenceCatalogConfiguration.PrimarySigningKeyId]);
        Assert.IsFalse(CryptographicOperations.FixedTimeEquals(
            referenceKey.ExportSubjectPublicKeyInfo(), key.ExportSubjectPublicKeyInfo()));
        Assert.IsNotNull(services.ChannelClient);
        Assert.IsNotNull(services.Verifier);
    }

    [TestMethod]
    public void ProductionManagedStoreRequiresAValidSignedEmbeddedBaseline()
    {
        using var fixture = RuntimeFixture.Create();
        var verifier = ProductionManagedCatalogConfiguration.Create("1.1.1").Verifier;
        var invalid = new ManagedCatalogStore(
            fixture.ApplicationRoot, fixture.DataRoot, verifier, channel: null, requireSignedBaseline: true);
        var invalidException = Assert.ThrowsExactly<CatalogValidationException>(() => invalid.LoadActiveOrEmbedded());
        StringAssert.Contains(invalidException.Message, "signature");

        File.Delete(Path.Combine(fixture.ApplicationRoot,
            ManagedCatalogStore.EmbeddedBundleRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var missing = new ManagedCatalogStore(
            fixture.ApplicationRoot, fixture.DataRoot, verifier, channel: null, requireSignedBaseline: true);
        var missingException = Assert.ThrowsExactly<CatalogValidationException>(() => missing.LoadActiveOrEmbedded());
        StringAssert.Contains(missingException.Message, "signed embedded");
    }

    [TestMethod]
    public void ProductionManagedStoreSelectsTheCommittedOwnerSignedBaseline()
    {
        using var fixture = RuntimeFixture.Create();
        File.Copy(Path.Combine(RepositoryRootLocator.Find(), "catalog", "managed", "AVWT-Managed-Catalog.avwtmanaged"),
            Path.Combine(fixture.ApplicationRoot, ManagedCatalogStore.EmbeddedBundleRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            overwrite: true);
        var store = new ManagedCatalogStore(fixture.ApplicationRoot, fixture.DataRoot,
            ProductionManagedCatalogConfiguration.Create("1.1.1").Verifier, channel: null, requireSignedBaseline: true);

        var selected = store.LoadActiveOrEmbedded();

        Assert.IsTrue(selected.Source.IsEmbedded);
        Assert.AreEqual(1L, selected.Source.Revision, "The tracked managed baseline changed without an owner-approved revision handoff.");
        Assert.AreEqual("2026.9.22.1", selected.Source.Version);
        Assert.HasCount(30, selected.Catalog.Items);
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

    private static ManagedCatalogStore NewStore(RuntimeFixture fixture, IManagedCatalogChannelClient? channel = null) =>
        new(fixture.ApplicationRoot, fixture.DataRoot, fixture.RuntimeServices.Verifier, channel, requireSignedBaseline: true);

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
        private const string SigningKeyId = "test-managed-runtime-2026";
        private readonly string root;
        private readonly string signingKeyPath;
        private RuntimeFixture(string root, string signingKeyPath, string applicationRoot, string dataRoot, string applicationVersion,
            ManagedCatalogRuntimeServices runtimeServices, IReadOnlyDictionary<string, string> trustedKeys,
            string updateChannelMetadataPath, string updateChannelSignaturePath, string updateBundlePath, Uri updateBundleUri)
        {
            this.root = root;
            this.signingKeyPath = signingKeyPath;
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
            var publisher = new ManagedCatalogPublisher();
            var publishedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
            var baseline = publisher.Publish(new(repository, Path.Combine(root, "publication-1"), "2026.9.21.1", 1, "1.1.1",
                publishedAt, SigningKeyId, keyPath, new Uri("https://catalog.example.test/managed/")));
            var update = publisher.Publish(new(authoring, Path.Combine(root, "publication-2"), "2026.9.21.2", 2, "1.1.1",
                publishedAt.AddMinutes(1), SigningKeyId, keyPath, new Uri("https://catalog.example.test/managed/"), baseline.BundlePath));
            Directory.CreateDirectory(Path.Combine(application, "managed-catalog"));
            File.Copy(baseline.BundlePath, Path.Combine(application, ManagedCatalogStore.EmbeddedBundleRelativePath.Replace('/', Path.DirectorySeparatorChar)));

            var keys = new Dictionary<string, string>(StringComparer.Ordinal) { [SigningKeyId] = key.ExportSubjectPublicKeyInfoPem() };
            var verifier = new ManagedCatalogVerifier(new("1.1.1", keys));
            var publication = verifier.VerifyPublication(update.ChannelMetadataPath, update.ChannelSignaturePath, update.BundlePath, DateTimeOffset.UtcNow);
            var bytes = File.ReadAllBytes(update.BundlePath);
            var channel = new StaticChannel(new(publication.Channel.Revision, publication.Channel.PreviousRevision,
                publication.Channel.CatalogVersion, publication.Channel.MinimumAppVersion, publication.Channel.CreatedUtc,
                publication.Channel.SigningKeyId, publication.Channel.BundleSha256, bytes));
            return new(root, keyPath, application, data, "1.1.1", new(verifier, channel), keys,
                update.ChannelMetadataPath, update.ChannelSignaturePath, update.BundlePath, publication.Channel.BundleUri);
        }

        /// <summary>Publishes the canonical managed catalog as another signed revision with its own minimum release.</summary>
        internal string PublishRevision(long revision, string minimumAppVersion)
        {
            var authoring = Path.Combine(root, $"authoring-{revision}");
            Directory.CreateDirectory(Path.Combine(authoring, "manifests"));
            File.Copy(Path.Combine(RepositoryRootLocator.Find(), "manifests", "managed-applications.json"),
                Path.Combine(authoring, "manifests", "managed-applications.json"));
            // The publisher verifies its output as the release named in VERSION.
            File.WriteAllText(Path.Combine(authoring, "VERSION"), minimumAppVersion);
            return new ManagedCatalogPublisher().Publish(new(authoring, Path.Combine(root, $"publication-{revision}-{minimumAppVersion}"),
                $"2026.9.21.{revision}", revision, minimumAppVersion, DateTimeOffset.UtcNow.AddMinutes(-1), SigningKeyId, signingKeyPath,
                new Uri("https://catalog.example.test/managed/"))).BundlePath;
        }

        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
