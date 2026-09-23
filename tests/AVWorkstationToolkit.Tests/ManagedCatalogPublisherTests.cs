using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVWorkstationToolkit.CatalogPublisher;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class ManagedCatalogPublisherTests
{
    [TestMethod]
    public void ValidManagedCatalogPublishesAndIndependentlyVerifies()
    {
        using var fixture = new ManagedPublisherFixture();
        var result = fixture.Publish(1);

        Assert.AreEqual(ManagedCatalogBundleNames.CatalogId, result.Manifest.CatalogId);
        Assert.AreEqual(1L, result.Manifest.Revision);
        Assert.AreEqual(0L, result.Manifest.PreviousRevision);
        Assert.AreEqual(1, result.Manifest.SchemaVersion);
        Assert.AreEqual(fixture.SourcePackageCount, result.Manifest.PackageCount);
        Assert.IsGreaterThan(0, result.Manifest.PackageCount);
        Assert.IsFalse(Directory.EnumerateFiles(fixture.Output(1), "*.pem", SearchOption.AllDirectories).Any());
        using (var archive = ZipFile.OpenRead(result.BundlePath))
            CollectionAssert.AreEquivalent(ManagedCatalogBundleNames.BundleFiles.ToArray(), archive.Entries.Select(item => item.FullName).ToArray());

        var verified = fixture.Verifier().VerifyPublication(
            result.ChannelMetadataPath,
            result.ChannelSignaturePath,
            result.BundlePath,
            fixture.CreatedUtc);
        Assert.AreEqual(result.Manifest.Revision, verified.Bundle.Manifest.Revision);
        Assert.HasCount(result.Manifest.PackageCount, verified.Bundle.Catalog.Items);
        Assert.IsTrue(verified.Bundle.Catalog.Items.All(item =>
            item.Authority == CatalogAuthority.ManagedWinGet && item.Provider == ProviderKind.WinGet));
        Assert.IsTrue(verified.Bundle.Catalog.Items.Any(item => !item.HasManagedExecutionAuthority));
        Assert.AreEqual(new Uri("https://catalog.example.test/avwt/managed/catalogs/1/AVWT-Managed-Catalog-2026.9.20.1.avwtmanaged"), verified.Channel.BundleUri);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(result.BundlePath))), verified.Channel.BundleSha256);
    }

    [TestMethod]
    public void IdenticalInputsProduceStablePayloadManifestAndChannelFields()
    {
        using var fixture = new ManagedPublisherFixture();
        var first = fixture.Publish(7, "first");
        var second = fixture.Publish(7, "second");

        CollectionAssert.AreEqual(ReadEntry(first.BundlePath, ManagedCatalogBundleNames.Payload), ReadEntry(second.BundlePath, ManagedCatalogBundleNames.Payload));
        CollectionAssert.AreEqual(ReadEntry(first.BundlePath, ManagedCatalogBundleNames.Manifest), ReadEntry(second.BundlePath, ManagedCatalogBundleNames.Manifest));

        var firstChannel = JsonNode.Parse(File.ReadAllBytes(first.ChannelMetadataPath))!.AsObject();
        var secondChannel = JsonNode.Parse(File.ReadAllBytes(second.ChannelMetadataPath))!.AsObject();
        firstChannel.Remove("BundleSha256");
        secondChannel.Remove("BundleSha256");
        Assert.AreEqual(firstChannel.ToJsonString(), secondChannel.ToJsonString());
        _ = fixture.Verifier().VerifyPublication(first.ChannelMetadataPath, first.ChannelSignaturePath, first.BundlePath, fixture.CreatedUtc);
        _ = fixture.Verifier().VerifyPublication(second.ChannelMetadataPath, second.ChannelSignaturePath, second.BundlePath, fixture.CreatedUtc);
    }

    [TestMethod]
    public void PreviousSignedCatalogEstablishesMonotonicRevisionHistory()
    {
        using var fixture = new ManagedPublisherFixture();
        var first = fixture.Publish(1, "first");
        var next = fixture.Publish(3, "next", first.BundlePath);

        Assert.AreEqual(1L, next.Manifest.PreviousRevision);
        Assert.AreEqual(3L, next.Manifest.Revision);
        _ = fixture.Verifier().VerifyPublication(next.ChannelMetadataPath, next.ChannelSignaturePath, next.BundlePath, fixture.CreatedUtc);
        Assert.ThrowsExactly<CatalogValidationException>(() => fixture.Publish(1, "rollback", first.BundlePath));
    }

    [TestMethod]
    public void TamperedCatalogChannelAndHashesFailClosed()
    {
        using var fixture = new ManagedPublisherFixture();
        var result = fixture.Publish(1);

        var alteredPayload = Encoding.UTF8.GetString(ReadEntry(result.BundlePath, ManagedCatalogBundleNames.Payload)) + " ";
        var tamperedBundle = fixture.RewriteBundle(result.BundlePath, alteredPayload, updateManifestHash: false, "tampered-payload");
        Assert.ThrowsExactly<CatalogValidationException>(() => fixture.Verifier().VerifyFile(tamperedBundle));

        var tamperedChannel = File.ReadAllBytes(result.ChannelMetadataPath);
        tamperedChannel[^2] ^= 1;
        Assert.ThrowsExactly<CatalogValidationException>(() => fixture.Verifier().VerifyChannel(
            tamperedChannel, File.ReadAllBytes(result.ChannelSignaturePath), fixture.CreatedUtc));

        var wrongHashChannel = fixture.RewriteChannel(result.ChannelMetadataPath, channel => channel["BundleSha256"] = new string('A', 64), "wrong-hash");
        Assert.ThrowsExactly<CatalogValidationException>(() => fixture.Verifier().VerifyPublication(
            wrongHashChannel.MetadataPath, wrongHashChannel.SignaturePath, result.BundlePath, fixture.CreatedUtc));
    }

    [TestMethod]
    public void WrongAndReferenceAuthoritiesCannotAuthorizeManagedCatalogs()
    {
        using var fixture = new ManagedPublisherFixture();
        var result = fixture.Publish(1);
        var referenceSigned = fixture.ResignBundle(
            result.BundlePath,
            manifest => manifest["SigningKeyId"] = fixture.ReferenceKeyId,
            fixture.ReferenceKey,
            "reference-signed");
        Assert.ThrowsExactly<CatalogValidationException>(() => fixture.Verifier().VerifyFile(referenceSigned));

        var productionReferencePem = ProductionReferenceCatalogTrustAnchors.All[
            ProductionReferenceCatalogConfiguration.PrimarySigningKeyId];
        Assert.ThrowsExactly<ArgumentException>(() => new ManagedCatalogVerifier(new(
            "1.1.1",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["managed-alias"] = productionReferencePem })));
        Assert.ThrowsExactly<ArgumentException>(() => new ManagedCatalogVerifier(new(
            "1.1.1",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProductionReferenceCatalogConfiguration.PrimarySigningKeyId] = fixture.ManagedKey.ExportSubjectPublicKeyInfoPem()
            })));

        using var unrelated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var wrongVerifier = new ManagedCatalogVerifier(new("1.1.1", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [fixture.ManagedKeyId] = unrelated.ExportSubjectPublicKeyInfoPem()
        }));
        Assert.ThrowsExactly<CatalogValidationException>(() => wrongVerifier.VerifyFile(result.BundlePath));

        var referenceVerifier = new ReferenceCatalogChannelVerifier(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [fixture.ManagedKeyId] = fixture.ManagedKey.ExportSubjectPublicKeyInfoPem()
        });
        Assert.ThrowsExactly<CatalogValidationException>(() => referenceVerifier.Verify(
            File.ReadAllBytes(result.ChannelMetadataPath), File.ReadAllBytes(result.ChannelSignaturePath), fixture.CreatedUtc));
    }

    [TestMethod]
    public void SignedWrongIdentitySchemaAndMinimumVersionFailClosed()
    {
        using var fixture = new ManagedPublisherFixture();
        var result = fixture.Publish(1);

        var wrongIdentity = fixture.ResignBundle(result.BundlePath, manifest => manifest["CatalogId"] = ReferenceCatalogBundleNames.CatalogId, fixture.ManagedKey, "wrong-id");
        Assert.ThrowsExactly<CatalogValidationException>(() => fixture.Verifier().VerifyFile(wrongIdentity));

        var wrongSchema = fixture.ResignBundle(result.BundlePath, manifest => manifest["SchemaVersion"] = 2, fixture.ManagedKey, "wrong-schema");
        Assert.ThrowsExactly<CatalogValidationException>(() => fixture.Verifier().VerifyFile(wrongSchema));

        var newerApp = fixture.ResignBundle(result.BundlePath, manifest => manifest["MinimumAppVersion"] = "99.0.0", fixture.ManagedKey, "newer-app");
        Assert.ThrowsExactly<ManagedCatalogRequiresNewerApplicationException>(() => fixture.Verifier().VerifyFile(newerApp));

        var wrongChannelIdentity = fixture.RewriteChannel(result.ChannelMetadataPath, channel => channel["CatalogId"] = ReferenceCatalogBundleNames.CatalogId, "wrong-channel-id");
        Assert.ThrowsExactly<CatalogValidationException>(() => fixture.Verifier().VerifyChannel(
            File.ReadAllBytes(wrongChannelIdentity.MetadataPath), File.ReadAllBytes(wrongChannelIdentity.SignaturePath), fixture.CreatedUtc));

        var wrongChannelSchema = fixture.RewriteChannel(result.ChannelMetadataPath, channel => channel["SchemaVersion"] = 2, "wrong-channel-schema");
        Assert.ThrowsExactly<CatalogValidationException>(() => fixture.Verifier().VerifyChannel(
            File.ReadAllBytes(wrongChannelSchema.MetadataPath), File.ReadAllBytes(wrongChannelSchema.SignaturePath), fixture.CreatedUtc));

        var ambiguousArtifact = fixture.RewriteChannel(
            result.ChannelMetadataPath,
            channel => channel["BundleUri"] = "https://catalog.example.test/avwt/managed/catalogs/1/subdir/AVWT-Managed-Catalog-2026.9.20.1.avwtmanaged",
            "ambiguous-artifact");
        Assert.ThrowsExactly<CatalogValidationException>(() => fixture.Verifier().VerifyChannel(
            File.ReadAllBytes(ambiguousArtifact.MetadataPath), File.ReadAllBytes(ambiguousArtifact.SignaturePath), fixture.CreatedUtc));
    }

    [TestMethod]
    public void SignedMalformedAndUnknownManagedPoliciesFailClosed()
    {
        using var fixture = new ManagedPublisherFixture();
        var result = fixture.Publish(1);
        var valid = JsonNode.Parse(ReadEntry(result.BundlePath, ManagedCatalogBundleNames.Payload))!.AsObject();

        AssertRejected(payload => payload["Packages"]![0]!["InstallerMode"] = new JsonArray("Silent"), "installer-array");
        AssertRejected(payload => payload["Packages"]![0]!["Risk"] = "Arbitrary", "risk");
        AssertRejected(payload => payload["Packages"]![0]!["Deployment"] = "Arbitrary", "deployment");
        AssertRejected(payload => payload["Packages"]![0]!["Maintenance"] = "Arbitrary", "maintenance");
        AssertRejected(payload => payload["Packages"]![0]!["InstallerMode"] = "Arbitrary", "installer-mode");
        AssertRejected(payload => payload["Packages"]![0]!["Command"] = "cmd.exe", "command");
        AssertRejected(payload => payload["ForbiddenPattern"] = "(?i)NeverMatch", "forbidden-weakened");
        AssertRejected(payload => payload["Packages"]!.AsArray().Add(payload["Packages"]![0]!.DeepClone()), "duplicate-id");

        void AssertRejected(Action<JsonObject> mutate, string suffix)
        {
            var payload = valid.DeepClone().AsObject();
            mutate(payload);
            var signed = fixture.RewriteBundle(result.BundlePath, payload.ToJsonString(), updateManifestHash: true, suffix);
            Assert.ThrowsExactly<CatalogValidationException>(() => fixture.Verifier().VerifyFile(signed), suffix);
        }
    }

    [TestMethod]
    public void PublisherRejectsMalformedCanonicalInputsBeforeWritingOutput()
    {
        using var fixture = new ManagedPublisherFixture();
        fixture.MutateSource(payload => payload["Packages"]![0]!["InstallerMode"] = new JsonArray("Silent"));
        Assert.ThrowsExactly<CatalogValidationException>(() => fixture.Publish(1));
        Assert.IsFalse(Directory.Exists(fixture.Output(1)));

        using var weakeningFixture = new ManagedPublisherFixture();
        weakeningFixture.MutateSource(payload => payload["ForbiddenPattern"] = "(?i)NeverMatch");
        Assert.ThrowsExactly<CatalogValidationException>(() => weakeningFixture.Publish(1));
        Assert.IsFalse(Directory.Exists(weakeningFixture.Output(1)));
    }

    [TestMethod]
    public void UnexpectedDuplicateAndTraversalEntriesFailClosed()
    {
        using var fixture = new ManagedPublisherFixture();
        var result = fixture.Publish(1);
        var entries = ReadEntries(result.BundlePath);

        var missing = fixture.WriteArchive("missing", entries.Where(item => item.Key != ManagedCatalogBundleNames.Signature));
        Assert.ThrowsExactly<CatalogValidationException>(() => fixture.Verifier().VerifyFile(missing));

        var unexpected = fixture.WriteArchive("unexpected", entries.Append(new("extra.json", Encoding.UTF8.GetBytes("{}"))));
        Assert.ThrowsExactly<CatalogValidationException>(() => fixture.Verifier().VerifyFile(unexpected));

        var traversalEntries = entries.Select(item => item.Key == ManagedCatalogBundleNames.Signature
            ? new KeyValuePair<string, byte[]>("../managed-catalog-manifest.sig", item.Value)
            : item);
        var traversal = fixture.WriteArchive("traversal", traversalEntries);
        Assert.ThrowsExactly<CatalogValidationException>(() => fixture.Verifier().VerifyFile(traversal));

        var duplicateEntries = entries.Append(new(ManagedCatalogBundleNames.Payload, entries[ManagedCatalogBundleNames.Payload]));
        var duplicate = fixture.WriteArchive("duplicate", duplicateEntries);
        Assert.ThrowsExactly<CatalogValidationException>(() => fixture.Verifier().VerifyFile(duplicate));
    }

    [TestMethod]
    public void ProductionVerifierRejectsDevelopmentSignedAndUnsignedManagedBundles()
    {
        using var fixture = new ManagedPublisherFixture();
        var result = fixture.Publish(1);
        var productionVerifier = ProductionManagedCatalogConfiguration.Create("1.1.1").Verifier;

        Assert.ThrowsExactly<CatalogValidationException>(() => productionVerifier.VerifyFile(result.BundlePath),
            "A development signing-key ID must not be accepted by production.");

        var developmentSignatureClaimingProductionId = fixture.ResignBundle(
            result.BundlePath,
            manifest => manifest["SigningKeyId"] = ProductionManagedCatalogConfiguration.PrimarySigningKeyId,
            fixture.ManagedKey,
            "development-signature-production-id");
        Assert.ThrowsExactly<CatalogValidationException>(
            () => productionVerifier.VerifyFile(developmentSignatureClaimingProductionId),
            "A development signature must not be accepted under the production key ID.");

        var unsignedEntries = ReadEntries(developmentSignatureClaimingProductionId);
        unsignedEntries[ManagedCatalogBundleNames.Signature] = [];
        var unsigned = fixture.WriteArchive("unsigned-production-id", unsignedEntries);
        Assert.ThrowsExactly<CatalogValidationException>(() => productionVerifier.VerifyFile(unsigned));
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

    private static Dictionary<string, byte[]> ReadEntries(string bundlePath)
    {
        using var archive = ZipFile.OpenRead(bundlePath);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            using var input = entry.Open();
            using var output = new MemoryStream();
            input.CopyTo(output);
            result.Add(entry.FullName, output.ToArray());
        }
        return result;
    }

    private sealed class ManagedPublisherFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"avwt-managed-publisher-{Guid.NewGuid():N}");
        private readonly string privateKeyPath;

        public ManagedPublisherFixture()
        {
            Directory.CreateDirectory(root);
            RepositoryRoot = Path.Combine(root, "repository");
            Directory.CreateDirectory(Path.Combine(RepositoryRoot, "manifests"));
            var source = FindRepositoryRoot();
            File.Copy(Path.Combine(source, "VERSION"), Path.Combine(RepositoryRoot, "VERSION"));
            File.Copy(
                Path.Combine(source, "manifests", ManagedCatalogBundleNames.Payload),
                Path.Combine(RepositoryRoot, "manifests", ManagedCatalogBundleNames.Payload));
            ManagedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            ReferenceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            privateKeyPath = Path.Combine(root, "managed-test-private.pem");
            File.WriteAllText(privateKeyPath, ManagedKey.ExportPkcs8PrivateKeyPem(), new UTF8Encoding(false));
        }

        public string RepositoryRoot { get; }
        public ECDsa ManagedKey { get; }
        public ECDsa ReferenceKey { get; }
        public string ManagedKeyId { get; } = "managed-test-2026";
        public string ReferenceKeyId { get; } = "reference-test-2026";
        public DateTimeOffset CreatedUtc { get; } = DateTimeOffset.UtcNow.AddMinutes(-1);
        public int SourcePackageCount
        {
            get
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RepositoryRoot, "manifests", ManagedCatalogBundleNames.Payload)));
                return document.RootElement.GetProperty("Packages").GetArrayLength();
            }
        }

        public ManagedCatalogPublishResult Publish(long revision, string? suffix = null, string? previousCatalogPath = null) =>
            new ManagedCatalogPublisher().Publish(new(
                RepositoryRoot,
                Output(revision, suffix),
                $"2026.9.20.{revision}",
                revision,
                "1.1.1",
                CreatedUtc,
                ManagedKeyId,
                privateKeyPath,
                new Uri("https://catalog.example.test/avwt/managed/"),
                previousCatalogPath));

        public string Output(long revision, string? suffix = null) =>
            Path.Combine(root, $"feed-{revision}{(suffix is null ? string.Empty : $"-{suffix}")}");

        public ManagedCatalogVerifier Verifier() => new(new("1.1.1", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ManagedKeyId] = ManagedKey.ExportSubjectPublicKeyInfoPem()
        }));

        public void MutateSource(Action<JsonObject> mutate)
        {
            var path = Path.Combine(RepositoryRoot, "manifests", ManagedCatalogBundleNames.Payload);
            var payload = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            mutate(payload);
            File.WriteAllText(path, payload.ToJsonString(new() { WriteIndented = true }), new UTF8Encoding(false));
        }

        public string RewriteBundle(string bundlePath, string payloadJson, bool updateManifestHash, string suffix)
        {
            var entries = ReadEntries(bundlePath);
            entries[ManagedCatalogBundleNames.Payload] = Encoding.UTF8.GetBytes(payloadJson);
            if (updateManifestHash)
            {
                var manifest = JsonNode.Parse(entries[ManagedCatalogBundleNames.Manifest])!.AsObject();
                manifest["Files"]![ManagedCatalogBundleNames.Payload]!["Sha256"] =
                    Convert.ToHexString(SHA256.HashData(entries[ManagedCatalogBundleNames.Payload]));
                entries[ManagedCatalogBundleNames.Manifest] = Encoding.UTF8.GetBytes(manifest.ToJsonString(new() { WriteIndented = true }));
                entries[ManagedCatalogBundleNames.Signature] = ManagedKey.SignData(
                    entries[ManagedCatalogBundleNames.Manifest],
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            return WriteArchive(suffix, entries);
        }

        public string ResignBundle(string bundlePath, Action<JsonObject> mutateManifest, ECDsa signingKey, string suffix)
        {
            var entries = ReadEntries(bundlePath);
            var manifest = JsonNode.Parse(entries[ManagedCatalogBundleNames.Manifest])!.AsObject();
            mutateManifest(manifest);
            entries[ManagedCatalogBundleNames.Manifest] = Encoding.UTF8.GetBytes(manifest.ToJsonString(new() { WriteIndented = true }));
            entries[ManagedCatalogBundleNames.Signature] = signingKey.SignData(
                entries[ManagedCatalogBundleNames.Manifest],
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return WriteArchive(suffix, entries);
        }

        public (string MetadataPath, string SignaturePath) RewriteChannel(string channelPath, Action<JsonObject> mutate, string suffix)
        {
            var channel = JsonNode.Parse(File.ReadAllBytes(channelPath))!.AsObject();
            mutate(channel);
            var bytes = Encoding.UTF8.GetBytes(channel.ToJsonString(new() { WriteIndented = true }));
            var directory = Path.Combine(root, suffix);
            Directory.CreateDirectory(directory);
            var metadataPath = Path.Combine(directory, ManagedCatalogBundleNames.ChannelMetadata);
            var signaturePath = Path.Combine(directory, ManagedCatalogBundleNames.ChannelSignature);
            File.WriteAllBytes(metadataPath, bytes);
            File.WriteAllBytes(signaturePath, ManagedKey.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            return (metadataPath, signaturePath);
        }

        public string WriteArchive(string suffix, IEnumerable<KeyValuePair<string, byte[]>> entries)
        {
            var path = Path.Combine(root, $"{suffix}{ManagedCatalogBundleNames.BundleExtension}");
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (name, bytes) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var output = entry.Open();
                output.Write(bytes);
            }
            return path;
        }

        public void Dispose()
        {
            ManagedKey.Dispose();
            ReferenceKey.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "AVWorkstationToolkit.slnx"))) current = current.Parent;
            return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        }
    }
}
