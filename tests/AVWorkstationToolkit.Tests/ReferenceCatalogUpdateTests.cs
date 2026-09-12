using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AVWorkstationToolkit.Application.Compatibility;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class ReferenceCatalogUpdateTests
{
    [TestMethod]
    public async Task SignedBundleActivatesAtomicallyAndReloadsThroughValidatedCatalogs()
    {
        using var fixture = new BundleFixture();
        var bundle = fixture.CreateBundle(1, 0);
        var store = fixture.CreateStore();

        var result = await store.ImportAsync(bundle);
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, result.State);
        Assert.AreEqual(1L, result.CurrentRevision);

        var loaded = store.LoadActiveOrEmbedded();
        Assert.IsFalse(loaded.Source.IsEmbedded);
        Assert.AreEqual(1L, loaded.Source.Revision);
        Assert.HasCount(529, loaded.Hardware.Models);
        Assert.HasCount(338, loaded.Compatibility.DeviceSoftwareRelations);
        Assert.IsTrue(Directory.Exists(Path.Combine(fixture.DataRoot, "ReferenceCatalog", "catalogs", "1")));
        Assert.HasCount(0, Directory.GetDirectories(Path.Combine(fixture.DataRoot, "ReferenceCatalog", "staging")));
    }

    [TestMethod]
    public async Task InvalidSignatureHashExtraEntryAndFutureApplicationFailClosed()
    {
        using var fixture = new BundleFixture();

        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var wrongSignature = fixture.CreateBundle(1, 0, signingKey: otherKey);
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await fixture.CreateStore().ImportAsync(wrongSignature)).State);

        var badHash = fixture.CreateBundle(2, 1, corruptPayloadAfterSigning: true);
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await fixture.CreateStore().ImportAsync(badHash)).State);

        var extra = fixture.CreateBundle(3, 2, extraEntry: true);
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await fixture.CreateStore().ImportAsync(extra)).State);

        var future = fixture.CreateBundle(4, 3, minimumAppVersion: "99.0.0");
        Assert.AreEqual(ReferenceCatalogUpdateState.RequiresNewerApp, (await fixture.CreateStore().ImportAsync(future)).State);
    }

    [TestMethod]
    public async Task RollbackUnknownFieldsAndUntrustedConfigurationAreRejected()
    {
        using var fixture = new BundleFixture();
        var store = fixture.CreateStore();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await store.ImportAsync(fixture.CreateBundle(1, 0))).State);
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await store.ImportAsync(fixture.CreateBundle(1, 0))).State);

        var unknownField = fixture.CreateBundle(2, 1, addForbiddenHardwareField: true);
        var rejected = await store.ImportAsync(unknownField);
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, rejected.State);
        StringAssert.Contains(rejected.Detail, "could not be mapped", StringComparison.OrdinalIgnoreCase);

        var untrusted = fixture.CreateStore(trusted: false);
        Assert.AreEqual(ReferenceCatalogUpdateState.NotConfigured, (await untrusted.CheckAsync()).State);
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await untrusted.ImportAsync(fixture.CreateBundle(3, 2))).State);
    }

    [TestMethod]
    public async Task StartupTamperDetectionQuarantinesActiveRevisionAndUsesEmbeddedFallback()
    {
        using var fixture = new BundleFixture();
        var store = fixture.CreateStore();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await store.ImportAsync(fixture.CreateBundle(1, 0))).State);
        var activeHardware = Path.Combine(fixture.DataRoot, "ReferenceCatalog", "catalogs", "1", ReferenceCatalogBundleNames.Hardware);
        File.AppendAllText(activeHardware, " ", Encoding.UTF8);

        var loaded = store.LoadActiveOrEmbedded();

        Assert.IsTrue(loaded.Source.IsEmbedded);
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(activeHardware)!));
        Assert.HasCount(1, Directory.GetDirectories(Path.Combine(fixture.DataRoot, "ReferenceCatalog", "quarantine")));
    }

    [TestMethod]
    public async Task OversizedBundleUnexpectedStoredFileAndMalformedStateFailClosedToEmbedded()
    {
        using var fixture = new BundleFixture();
        var oversized = Path.Combine(fixture.DataRoot, "oversized.avwtcatalog");
        using (var stream = new FileStream(oversized, FileMode.CreateNew, FileAccess.Write))
            stream.SetLength(ReferenceCatalogBundleVerifier.MaximumBundleBytes + 1);
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await fixture.CreateStore().ImportAsync(oversized)).State);

        var store = fixture.CreateStore();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await store.ImportAsync(fixture.CreateBundle(1, 0))).State);
        var catalogRoot = Path.Combine(fixture.DataRoot, "ReferenceCatalog");
        File.WriteAllText(Path.Combine(catalogRoot, "catalogs", "1", "unexpected.txt"), "not permitted", Encoding.UTF8);
        var fallback = store.LoadActiveOrEmbedded();
        Assert.IsTrue(fallback.Source.IsEmbedded);

        File.WriteAllText(Path.Combine(catalogRoot, "state.json"), "{\"ActiveRevision\":1,\"ActiveRevision\":2}", Encoding.UTF8);
        fallback = store.LoadActiveOrEmbedded();
        Assert.IsTrue(fallback.Source.IsEmbedded);
        Assert.IsFalse(File.Exists(Path.Combine(catalogRoot, "state.json")));
    }

    [TestMethod]
    public void ManifestParserRejectsDuplicatePropertiesAndExecutionShapedMetadata()
    {
        var parser = new ReferenceCatalogManifestParser();
        var valid = """
            {"CatalogId":"avwt-reference","CatalogVersion":"2026.9.12.1","Revision":1,"SchemaVersion":1,"CompatibilityEpoch":1,"CreatedUtc":"2026-09-12T00:00:00Z","PreviousRevision":0,"MinimumAppVersion":"1.1.1","SigningKeyId":"test","Counts":{"Manufacturers":1,"Families":1,"ExactModels":1,"ReferenceSoftwareProducts":1,"Relations":1},"Files":{"hardware-identities.json":{"Sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"},"software-compatibility.json":{"Sha256":"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"},"catalog-changes.json":{"Sha256":"CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC"}}}
            """;
        Assert.AreEqual(1L, parser.ParseManifest(valid).Revision);
        Assert.ThrowsExactly<CatalogValidationException>(() => parser.ParseManifest(valid.Replace("\"Revision\":1", "\"Revision\":1,\"Revision\":2", StringComparison.Ordinal)));
        Assert.ThrowsExactly<CatalogValidationException>(() => parser.ParseManifest(valid.Replace("\"SigningKeyId\":\"test\"", "\"SigningKeyId\":\"test\",\"Installer\":\"evil.exe\"", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task SignedExactChannelChecksDownloadsAndActivatesWithoutRedirectOrCallerSelectedUrl()
    {
        using var fixture = new BundleFixture();
        var bundle = fixture.CreateBundle(1, 0);
        using var channel = fixture.CreateChannel(bundle);
        var store = fixture.CreateStore(channel: channel);
        _ = store.LoadActiveOrEmbedded();

        var available = await store.CheckAsync();
        Assert.AreEqual(ReferenceCatalogUpdateState.UpdateAvailable, available.State);
        Assert.AreEqual(1L, available.AvailableRevision);

        var installed = await store.InstallAvailableAsync();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, installed.State);
        Assert.AreEqual(1L, installed.CurrentRevision);
    }

    [TestMethod]
    public async Task OfflineChannelIsNonFatalAndSignedUnapprovedBundleOriginIsRejected()
    {
        using var fixture = new BundleFixture();
        var offline = new ThrowingChannelClient();
        var offlineStore = fixture.CreateStore(channel: offline);
        _ = offlineStore.LoadActiveOrEmbedded();
        Assert.AreEqual(ReferenceCatalogUpdateState.Offline, (await offlineStore.CheckAsync()).State);

        var bundle = fixture.CreateBundle(1, 0);
        using var badOrigin = fixture.CreateChannel(bundle, bundleHost: "unapproved.example");
        var rejectedStore = fixture.CreateStore(channel: badOrigin);
        _ = rejectedStore.LoadActiveOrEmbedded();
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await rejectedStore.CheckAsync()).State);
    }

    private sealed class BundleFixture : IDisposable
    {
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly string root = Path.Combine(Path.GetTempPath(), $"awt-catalog-update-{Guid.NewGuid():N}");
        public BundleFixture()
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(DataRoot);
        }

        public string DataRoot => Path.Combine(root, "data");

        public ReferenceCatalogStore CreateStore(bool trusted = true, IReferenceCatalogChannelClient? channel = null)
        {
            IReadOnlyDictionary<string, string> keys = trusted
                ? new Dictionary<string, string>(StringComparer.Ordinal) { ["test-2026-a"] = key.ExportSubjectPublicKeyInfoPem() }
                : new Dictionary<string, string>(StringComparer.Ordinal);
            return new(RepositoryRoot(), DataRoot, new ReferenceCatalogBundleVerifier(new("1.1.1", keys)), channel);
        }

        public ReferenceCatalogChannelClient CreateChannel(string bundlePath, string bundleHost = "catalog.avwt.example")
        {
            var bundle = File.ReadAllBytes(bundlePath);
            var metadataUri = new Uri("https://catalog.avwt.example/catalog-channel.json");
            var signatureUri = new Uri("https://catalog.avwt.example/catalog-channel.sig");
            var bundleUri = new Uri($"https://{bundleHost}/catalog-1.avwtcatalog");
            var metadata = JsonSerializer.SerializeToUtf8Bytes(new
            {
                CatalogId = ReferenceCatalogBundleNames.CatalogId,
                SchemaVersion = 1,
                CatalogVersion = "2026.9.12.1",
                Revision = 1,
                PreviousRevision = 0,
                MinimumAppVersion = "1.1.1",
                CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7),
                SigningKeyId = "test-2026-a",
                BundleUri = bundleUri.AbsoluteUri,
                BundleSha256 = Convert.ToHexString(SHA256.HashData(bundle))
            });
            var signature = key.SignData(metadata, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var handler = new RoutingHandler(request => request.RequestUri == metadataUri
                ? Payload(metadata)
                : request.RequestUri == signatureUri
                    ? Payload(signature)
                    : request.RequestUri == bundleUri
                        ? Payload(bundle)
                        : new HttpResponseMessage(HttpStatusCode.NotFound));
            var policy = new ReferenceCatalogChannelPolicy(metadataUri, signatureUri,
                new HashSet<string>(["catalog.avwt.example"], StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["test-2026-a"] = key.ExportSubjectPublicKeyInfoPem() },
                TimeSpan.FromSeconds(10));
            return new(policy, handler);
        }

        public string CreateBundle(
            long revision,
            long previousRevision,
            ECDsa? signingKey = null,
            bool corruptPayloadAfterSigning = false,
            bool extraEntry = false,
            string minimumAppVersion = "1.1.1",
            bool addForbiddenHardwareField = false)
        {
            var hardware = File.ReadAllBytes(ManifestPath(ReferenceCatalogBundleNames.Hardware));
            if (addForbiddenHardwareField)
            {
                var text = Encoding.UTF8.GetString(hardware);
                hardware = Encoding.UTF8.GetBytes(text.Replace("\"SchemaVersion\": 1,", "\"SchemaVersion\": 1, \"Command\": \"cmd.exe\",", StringComparison.Ordinal));
            }
            var compatibility = File.ReadAllBytes(ManifestPath(ReferenceCatalogBundleNames.Compatibility));
            var changes = Encoding.UTF8.GetBytes("{\"ManufacturersAdded\":0,\"FamiliesAdded\":0,\"ExactModelsAdded\":1,\"AliasesAdded\":1,\"ReferenceSoftwareAdded\":0,\"RelationsAdded\":0,\"UnresolvedToVerified\":0,\"Summary\":\"Test catalog revision.\"}");
            var hardwareCatalog = new HardwareIdentityCatalogParser().Parse(addForbiddenHardwareField
                ? Encoding.UTF8.GetString(File.ReadAllBytes(ManifestPath(ReferenceCatalogBundleNames.Hardware)))
                : Encoding.UTF8.GetString(hardware));
            var softwareCatalog = new CompatibilityCatalogParser().Parse(Encoding.UTF8.GetString(compatibility));
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [ReferenceCatalogBundleNames.Hardware] = hardware,
                [ReferenceCatalogBundleNames.Compatibility] = compatibility,
                [ReferenceCatalogBundleNames.Changes] = changes
            };
            var manifestObject = new
            {
                CatalogId = ReferenceCatalogBundleNames.CatalogId,
                CatalogVersion = $"2026.9.12.{revision}",
                Revision = revision,
                SchemaVersion = 1,
                CompatibilityEpoch = 1,
                CreatedUtc = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero),
                PreviousRevision = previousRevision,
                MinimumAppVersion = minimumAppVersion,
                SigningKeyId = "test-2026-a",
                Counts = new
                {
                    Manufacturers = hardwareCatalog.Families.Select(item => item.Manufacturer).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    Families = hardwareCatalog.Families.Count,
                    ExactModels = hardwareCatalog.Models.Count,
                    ReferenceSoftwareProducts = softwareCatalog.Products.Count,
                    Relations = softwareCatalog.DeviceSoftwareRelations.Count
                },
                Files = files.ToDictionary(item => item.Key, item => new { Sha256 = Convert.ToHexString(SHA256.HashData(item.Value)) }, StringComparer.Ordinal)
            };
            var manifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifestObject));
            var signer = signingKey ?? key;
            var signature = signer.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (corruptPayloadAfterSigning) hardware[hardware.Length / 2] ^= 0x01;
            var path = Path.Combine(root, $"catalog-{Guid.NewGuid():N}.avwtcatalog");
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (name, bytes) in files) WriteEntry(archive, name, bytes);
            WriteEntry(archive, ReferenceCatalogBundleNames.Manifest, manifest);
            WriteEntry(archive, ReferenceCatalogBundleNames.Signature, signature);
            if (extraEntry) WriteEntry(archive, "../unexpected.json", [1]);
            return path;
        }

        public void Dispose()
        {
            key.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(bytes);
        }

        private static string RepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "AVWorkstationToolkit.slnx"))) current = current.Parent;
            return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        }

        private static string ManifestPath(string name) => Path.Combine(RepositoryRoot(), "manifests", name);

        private static HttpResponseMessage Payload(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(route(request));
    }

    private sealed class ThrowingChannelClient : IReferenceCatalogChannelClient
    {
        public Task<ReferenceCatalogChannelPackage?> GetLatestAsync(long currentRevision, CancellationToken cancellationToken = default) =>
            Task.FromException<ReferenceCatalogChannelPackage?>(new HttpRequestException("Fixture channel is offline."));
    }
}
