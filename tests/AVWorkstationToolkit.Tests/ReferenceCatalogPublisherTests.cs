using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AVWorkstationToolkit.CatalogPublisher;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class ReferenceCatalogPublisherTests
{
    [TestMethod]
    public void BootstrapPublishesImmutableRuntimeCompatibleBundleAndChannel()
    {
        using var fixture = new PublisherFixture();
        var result = fixture.Publish(fixture.Options(43));

        Assert.AreEqual(0L, result.Manifest.PreviousRevision);
        Assert.IsTrue(File.Exists(result.BundlePath));
        Assert.IsTrue(File.Exists(result.ChannelMetadataPath));
        Assert.IsTrue(File.Exists(result.ChannelSignaturePath));
        Assert.IsTrue(File.Exists(result.ChangeSummaryPath));
        Assert.IsFalse(Directory.EnumerateFiles(fixture.Output(43), "*.pem", SearchOption.AllDirectories).Any());
        CollectionAssert.AreEqual(ReadEntry(result.BundlePath, ReferenceCatalogBundleNames.Changes), File.ReadAllBytes(result.ChangeSummaryPath));
        Assert.ThrowsExactly<IOException>(() => fixture.Publish(fixture.Options(43)));

        var verified = fixture.BundleVerifier().VerifyFile(result.BundlePath);
        Assert.AreEqual(result.Manifest.Revision, verified.Manifest.Revision);
        Assert.HasCount(result.Manifest.Counts.ExactModels, verified.Hardware.Models);
        Assert.HasCount(result.Manifest.Counts.Relations, verified.Compatibility.DeviceSoftwareRelations);
        var channel = new ReferenceCatalogChannelVerifier(fixture.TrustedKeys).Verify(
            File.ReadAllBytes(result.ChannelMetadataPath), File.ReadAllBytes(result.ChannelSignaturePath), fixture.CreatedUtc);
        Assert.AreEqual(43L, channel.Revision);
        Assert.AreEqual(new Uri("https://catalog.example.test/avwt/catalogs/43/AVWT-Catalog-2026.9.12.43.avwtcatalog"), channel.BundleUri);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(result.BundlePath))), channel.BundleSha256);
    }

    [TestMethod]
    public void PublisherSupportsSkippedPreviousRevisionAndProducesStableUnsignedPayloads()
    {
        using var fixture = new PublisherFixture();
        var baseline = fixture.Publish(fixture.Options(43));
        var next = fixture.Publish(fixture.Options(45, previousCatalog: baseline.BundlePath));

        Assert.AreEqual(43L, next.Manifest.PreviousRevision);
        Assert.AreEqual(45L, next.Manifest.Revision);
        Assert.IsFalse(next.Analysis.RequiresAcknowledgement);

        var stable = fixture.Publish(fixture.Options(46, outputSuffix: "stable"));
        var duplicate = fixture.Publish(fixture.Options(46, outputSuffix: "duplicate"));
        foreach (var name in new[] { ReferenceCatalogBundleNames.Hardware, ReferenceCatalogBundleNames.Compatibility, ReferenceCatalogBundleNames.Changes, ReferenceCatalogBundleNames.Manifest })
            CollectionAssert.AreEqual(ReadEntry(stable.BundlePath, name), ReadEntry(duplicate.BundlePath, name));
    }

    [TestMethod]
    public void DestructiveChangeAndRelationBroadeningRequireExplicitAcknowledgement()
    {
        using var fixture = new PublisherFixture(copyRepository: true);
        var baseline = fixture.Publish(fixture.Options(10));
        fixture.RemoveOneHardwareAlias();
        fixture.BroadenOneRelationAlias();

        var rejected = Assert.ThrowsExactly<CatalogValidationException>(() =>
            fixture.Publish(fixture.Options(11, previousCatalog: baseline.BundlePath)));
        StringAssert.Contains(rejected.Message, "explicit publisher acknowledgement");

        var accepted = fixture.Publish(fixture.Options(11, previousCatalog: baseline.BundlePath, acknowledgeRisk: true, outputSuffix: "acknowledged"));
        Assert.IsTrue(accepted.Analysis.RequiresAcknowledgement);
        Assert.AreEqual(1, accepted.Analysis.AliasesRemoved);
        Assert.HasCount(1, accepted.Analysis.BroadenedRelationIds);
        Assert.IsTrue(accepted.Analysis.Warnings.Any(item => item.Contains("aliases removed", StringComparison.Ordinal)));
        _ = fixture.BundleVerifier().VerifyFile(accepted.BundlePath);
    }

    [TestMethod]
    public void OperationalFieldsAndRepositoryResidentPrivateKeysFailClosed()
    {
        using var fixture = new PublisherFixture(copyRepository: true);
        fixture.AddForbiddenWorkerField();
        var rejected = Assert.ThrowsExactly<CatalogValidationException>(() => fixture.Publish(fixture.Options(1)));
        StringAssert.Contains(rejected.Message, "prohibited operational field");

        using var cleanFixture = new PublisherFixture(copyRepository: true);
        var repositoryKey = Path.Combine(cleanFixture.RepositoryRoot, "catalog-private.pem");
        File.WriteAllText(repositoryKey, cleanFixture.PrivateKeyPem, new UTF8Encoding(false));
        var options = cleanFixture.Options(1) with { PrivateKeyPath = repositoryKey, OutputRoot = cleanFixture.Output(1, "repository-key") };
        Assert.ThrowsExactly<IOException>(() => cleanFixture.Publish(options));
    }

    private static byte[] ReadEntry(string bundlePath, string name)
    {
        using var archive = ZipFile.OpenRead(bundlePath);
        var entry = archive.GetEntry(name) ?? throw new AssertFailedException($"Bundle entry '{name}' is missing.");
        using var input = entry.Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private sealed class PublisherFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"avwt-publisher-{Guid.NewGuid():N}");
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly string privateKeyPath;
        private readonly string sourceRepository;

        public PublisherFixture(bool copyRepository = false)
        {
            Directory.CreateDirectory(root);
            sourceRepository = FindRepositoryRoot();
            RepositoryRoot = copyRepository ? CreateRepositoryCopy() : sourceRepository;
            privateKeyPath = Path.Combine(root, "external-test-key.pem");
            PrivateKeyPem = key.ExportPkcs8PrivateKeyPem();
            File.WriteAllText(privateKeyPath, PrivateKeyPem, new UTF8Encoding(false));
            TrustedKeys = new Dictionary<string, string>(StringComparer.Ordinal) { ["test-publisher-2026"] = key.ExportSubjectPublicKeyInfoPem() };
        }

        public string RepositoryRoot { get; }
        public string PrivateKeyPem { get; }
        public IReadOnlyDictionary<string, string> TrustedKeys { get; }
        public DateTimeOffset CreatedUtc { get; } = DateTimeOffset.UtcNow.AddMinutes(-1);

        public ReferenceCatalogPublishOptions Options(long revision, string? previousCatalog = null, bool acknowledgeRisk = false, string? outputSuffix = null) => new(
            RepositoryRoot,
            Output(revision, outputSuffix),
            $"2026.9.12.{revision}",
            revision,
            "1.1.1",
            CreatedUtc,
            CreatedUtc.AddDays(7),
            "test-publisher-2026",
            privateKeyPath,
            new Uri("https://catalog.example.test/avwt/"),
            previousCatalog,
            null,
            acknowledgeRisk);

        public string Output(long revision, string? suffix = null) => Path.Combine(root, $"feed-{revision}{(suffix is null ? string.Empty : $"-{suffix}")}");

        public ReferenceCatalogPublishResult Publish(ReferenceCatalogPublishOptions options) => new ReferenceCatalogPublisher().Publish(options);

        public ReferenceCatalogBundleVerifier BundleVerifier() => new(new("1.1.1", TrustedKeys));

        public void RemoveOneHardwareAlias()
        {
            var path = Path.Combine(RepositoryRoot, "manifests", ReferenceCatalogBundleNames.Hardware);
            var rootNode = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var families = rootNode["Families"]!.AsArray();
            var aliases = families.Select(item => item!.AsObject()["Aliases"]!.AsArray()).First(items => items.Count > 0);
            aliases.RemoveAt(0);
            File.WriteAllText(path, rootNode.ToJsonString(new() { WriteIndented = true }), new UTF8Encoding(false));
        }

        public void BroadenOneRelationAlias()
        {
            var path = Path.Combine(RepositoryRoot, "manifests", ReferenceCatalogBundleNames.Compatibility);
            var rootNode = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var relations = rootNode["DeviceSoftwareRelations"]!.AsArray();
            relations[0]!.AsObject()["DeviceAliases"]!.AsArray().Add("PUBLISHER-SCOPE-TEST-ALIAS");
            File.WriteAllText(path, rootNode.ToJsonString(new() { WriteIndented = true }), new UTF8Encoding(false));
        }

        public void AddForbiddenWorkerField()
        {
            var path = Path.Combine(RepositoryRoot, "manifests", ReferenceCatalogBundleNames.Hardware);
            var rootNode = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            rootNode["WorkerAction"] = "Install";
            File.WriteAllText(path, rootNode.ToJsonString(new() { WriteIndented = true }), new UTF8Encoding(false));
        }

        public void Dispose()
        {
            key.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        private string CreateRepositoryCopy()
        {
            var copy = Path.Combine(root, "repository-copy");
            var manifests = Path.Combine(copy, "manifests");
            Directory.CreateDirectory(manifests);
            File.Copy(Path.Combine(sourceRepository, "VERSION"), Path.Combine(copy, "VERSION"));
            foreach (var name in new[]
            {
                ReferenceCatalogBundleNames.Hardware,
                ReferenceCatalogBundleNames.Compatibility,
                "managed-applications.json",
                "external-applications.json",
                "process-launch-policy.json"
            }) File.Copy(Path.Combine(sourceRepository, "manifests", name), Path.Combine(manifests, name));
            return copy;
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "AVWorkstationToolkit.slnx"))) current = current.Parent;
            return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        }
    }
}
