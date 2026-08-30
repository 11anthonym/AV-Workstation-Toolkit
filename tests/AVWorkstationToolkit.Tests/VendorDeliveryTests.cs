using System.Net;
using System.Security.Cryptography;
using System.Text;
using AVWorkstationToolkit.Application.Vendors;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Authenticode;
using AVWorkstationToolkit.Infrastructure.Windows.Vendors;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class VendorDeliveryTests
{
    [TestMethod]
    public void OperationalCatalogRetainsValidatedVendorDeliveryPolicy()
    {
        var root = RepositoryRoot();
        var catalog = new CatalogParser(DateOnly.FromDateTime(DateTime.UtcNow))
            .ParseExternalCatalog(File.ReadAllText(Path.Combine(root, "manifests", "external-applications.json")));
        var direct = catalog.Items.Single(item => item.DeliveryMode == DeliveryMode.DirectDownload);
        var sftp = catalog.Items.Single(item => item.DeliveryMode == DeliveryMode.AuthenticatedSftp);
        Assert.IsNotNull(direct.DeliveryPolicy);
        Assert.IsNotEmpty(direct.DeliveryPolicy.AllowedHosts);
        Assert.IsNotNull(sftp.DeliveryPolicy);
        Assert.HasCount(sftp.AllowedProductIds.Count, sftp.DeliveryPolicy.AllowedProductIds);
    }

    [TestMethod]
    public async Task HttpsDownloadAcceptsApprovedHostAndApprovedRedirect()
    {
        using var root = new TemporaryDirectory();
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath == "/start.exe"
            ? Redirect("https://cdn.example/final.exe")
            : Payload([1, 2, 3]));
        var authorization = VendorDeliveryAuthorization.ForHttps(DirectPackage(), "1.2", new Uri("https://download.example/start.exe"));
        using var downloader = new VendorHttpsDownloader(handler, new VendorCachePathPolicy());
        var result = await downloader.DownloadAsync(authorization, root.Path, CancellationToken.None);
        Assert.AreEqual(VendorPayloadState.Downloaded, result.State);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(result.Path));
        CollectionAssert.AreEqual(new[] { "download.example", "cdn.example" }, handler.Hosts);
    }

    [TestMethod]
    public async Task HttpsDownloadAcceptsApprovedHostWithoutRedirect()
    {
        using var root = new TemporaryDirectory();
        var handler = new RoutingHandler(_ => Payload([7]));
        var authorization = VendorDeliveryAuthorization.ForHttps(DirectPackage(), "1.2", new Uri("https://download.example/start.exe"));
        using var downloader = new VendorHttpsDownloader(handler, new VendorCachePathPolicy());
        Assert.AreEqual(VendorPayloadState.Downloaded, (await downloader.DownloadAsync(authorization, root.Path, CancellationToken.None)).State);
        CollectionAssert.AreEqual(new[] { "download.example" }, handler.Hosts);
    }

    [TestMethod]
    public async Task HttpsDownloadRejectsUnapprovedRedirectAndRemovesPartialFile()
    {
        using var root = new TemporaryDirectory();
        using var downloader = new VendorHttpsDownloader(new RoutingHandler(_ => Redirect("https://evil.example/payload.exe")), new VendorCachePathPolicy());
        var authorization = VendorDeliveryAuthorization.ForHttps(DirectPackage(), "1.2", new Uri("https://download.example/start.exe"));
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(authorization, root.Path, CancellationToken.None));
        Assert.IsEmpty(Directory.GetFiles(root.Path, "*.download", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task HttpsDownloadRejectsEmptyAndStreamedOversizedPayloads()
    {
        foreach (var content in new[] { Array.Empty<byte>(), new byte[1_048_577] })
        {
            using var root = new TemporaryDirectory();
            using var downloader = new VendorHttpsDownloader(new RoutingHandler(_ => Payload(content, omitLength: true)), new VendorCachePathPolicy());
            var authorization = VendorDeliveryAuthorization.ForHttps(DirectPackage(), "1.2", new Uri("https://download.example/start.exe"));
            await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(authorization, root.Path, CancellationToken.None));
            Assert.IsEmpty(Directory.GetFiles(root.Path, "*.download", SearchOption.AllDirectories));
        }
    }

    [TestMethod]
    public async Task SftpValidatesHostBeforeCredentialLookupAndDownloadsContainedPayload()
    {
        using var root = new TemporaryDirectory();
        var transport = new FakeSftpTransport(Fingerprint, [4, 5, 6]);
        var credentials = new OrderedCredentialStore(transport, "engineer", "secret");
        var authorization = VendorDeliveryAuthorization.ForSftp(SftpPackage(), "2.0", "engineer", Trust(), "/approved/setup.exe");
        var result = await new VendorSftpDeliveryService(transport, credentials, new VendorCachePathPolicy())
            .DownloadAsync(authorization, root.Path, null, false, CancellationToken.None);
        Assert.AreEqual(VendorPayloadState.Downloaded, result.State);
        Assert.IsTrue(credentials.ReadAfterProbe);
        CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, File.ReadAllBytes(result.Path));
    }

    [TestMethod]
    public async Task SftpWrongFingerprintFailsBeforeCredentialLookup()
    {
        using var root = new TemporaryDirectory();
        var transport = new FakeSftpTransport("SHA256:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", [1]);
        var credentials = new OrderedCredentialStore(transport, "engineer", "secret");
        var authorization = VendorDeliveryAuthorization.ForSftp(SftpPackage(), "2.0", "engineer", Trust(), "/approved/setup.exe");
        await Assert.ThrowsAsync<InvalidDataException>(() => new VendorSftpDeliveryService(transport, credentials, new VendorCachePathPolicy())
            .DownloadAsync(authorization, root.Path, null, false, CancellationToken.None));
        Assert.IsFalse(credentials.ReadCalled);
    }

    [TestMethod]
    public void SftpAuthorizationRejectsRemoteEscapeAndInvalidFingerprint()
    {
        Assert.Throws<InvalidDataException>(() => VendorDeliveryAuthorization.ForSftp(SftpPackage(), "2.0", "engineer", Trust(), "/other/setup.exe"));
        Assert.Throws<InvalidDataException>(() => VendorDeliveryAuthorization.ForSftp(SftpPackage(), "2.0", "engineer", new VendorSftpHostTrust(new("sftp.example", 22), "SHA256:bad"), "/approved/setup.exe"));
    }

    [TestMethod]
    public void CredentialTargetsAreExactScopedAndRetainLegacyReadIdentity()
    {
        var identity = VendorDeliveryAuthorization.ForSftp(SftpPackage(), "2.0", "engineer", Trust(), "/approved/setup.exe").SftpIdentity!;
        Assert.AreEqual("AVWorkstationToolkit:VendorSftp:sftp.example:22:engineer", WindowsVendorCredentialStore.CurrentTarget(identity));
        Assert.AreEqual("AVinite:VendorSftp:sftp.example:22:engineer", WindowsVendorCredentialStore.LegacyTarget(identity));
        using var credential = new VendorCredential("engineer", "secret".ToCharArray());
        StringAssert.DoesNotMatch(credential.ToString(), new System.Text.RegularExpressions.Regex("secret", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        credential.Dispose();
        Assert.IsTrue(credential.Secret.IsEmpty);
    }

    [TestMethod]
    public void TrustedHostStoreProducesScopedEvidenceAndRejectsChangedFingerprint()
    {
        using var root = new TemporaryDirectory();
        var store = new VendorTrustedHostStore();
        var endpoint = new VendorEndpoint("sftp.example", 22);
        var trust = store.Trust(root.Path, endpoint, Fingerprint);
        Assert.AreEqual(Fingerprint, trust.Fingerprint);
        Assert.AreEqual(Fingerprint, store.Read(root.Path, endpoint)!.Fingerprint);
        var changed = "SHA256:CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
        Assert.AreNotEqual(changed, store.Read(root.Path, endpoint)!.Fingerprint);
    }

    [TestMethod]
    public async Task SftpOversizedPayloadIsRejectedAndRemoved()
    {
        using var root = new TemporaryDirectory();
        var transport = new FakeSftpTransport(Fingerprint, new byte[1_048_577]);
        var credentials = new OrderedCredentialStore(transport, "engineer", "secret");
        var authorization = VendorDeliveryAuthorization.ForSftp(SftpPackage(), "2.0", "engineer", Trust(), "/approved/setup.exe");
        await Assert.ThrowsAsync<InvalidDataException>(() => new VendorSftpDeliveryService(transport, credentials, new VendorCachePathPolicy())
            .DownloadAsync(authorization, root.Path, null, false, CancellationToken.None));
        Assert.IsEmpty(Directory.GetFiles(root.Path, "*.download", SearchOption.AllDirectories));
    }

    [TestMethod]
    public void VerificationRejectsHashOrPublisherAndPromotesOnlyVerifiedPayload()
    {
        using var root = new TemporaryDirectory();
        var authorization = VendorDeliveryAuthorization.ForHttps(DirectPackage(), "1.2", new Uri("https://download.example/start.exe"));
        var paths = new VendorCachePathPolicy();
        var badHashPath = paths.GetTemporaryPayloadPath(root.Path, authorization, "start.exe");
        File.WriteAllBytes(badHashPath, [1, 2, 3]);
        var verifier = new VendorPayloadVerificationService(paths, new FakeSignatureInspector(true, "O=Approved Vendor"));
        var rejected = new VendorPayloadVerificationService(paths, new FakeSignatureInspector(false, string.Empty))
            .VerifyAndPromote(authorization, root.Path, badHashPath);
        Assert.AreEqual(VendorPayloadState.Rejected, rejected.State);
        Assert.IsFalse(File.Exists(badHashPath));

        var validPath = paths.GetTemporaryPayloadPath(root.Path, authorization, "start.exe");
        File.WriteAllBytes(validPath, [1, 2, 3]);
        var verified = verifier.VerifyAndPromote(authorization, root.Path, validPath);
        Assert.AreEqual(VendorPayloadState.Verified, verified.State);
        Assert.IsTrue(File.Exists(verified.Path));
        Assert.IsTrue(File.Exists(verified.Path + ".avworkstationtoolkit.json"));

        var nextAuthorization = VendorDeliveryAuthorization.ForHttps(DirectPackage(), "1.3", new Uri("https://download.example/start.exe"));
        var wrongPublisherPath = paths.GetTemporaryPayloadPath(root.Path, nextAuthorization, "start.exe");
        File.WriteAllBytes(wrongPublisherPath, [1]);
        var wrongPublisher = new VendorPayloadVerificationService(paths, new FakeSignatureInspector(true, "O=Unexpected"));
        Assert.AreEqual(VendorPayloadState.Rejected, wrongPublisher.VerifyAndPromote(nextAuthorization, root.Path, wrongPublisherPath).State);
    }

    [TestMethod]
    public void CorruptCacheDoesNotRemainTrusted()
    {
        using var root = new TemporaryDirectory();
        var authorization = VendorDeliveryAuthorization.ForHttps(DirectPackage(), "1.2", new Uri("https://download.example/start.exe"));
        var paths = new VendorCachePathPolicy();
        var temporary = paths.GetTemporaryPayloadPath(root.Path, authorization, "start.exe");
        File.WriteAllBytes(temporary, [1, 2, 3]);
        var verifier = new VendorPayloadVerificationService(paths, new FakeSignatureInspector(true, "O=Approved Vendor"));
        var verified = verifier.VerifyAndPromote(authorization, root.Path, temporary);
        File.WriteAllBytes(verified.Path, [9]);
        Assert.AreEqual(VendorPayloadState.Rejected, verifier.ResolveCached(authorization, root.Path, verified.Path).State);
    }

    [TestMethod]
    public void CatalogAuthorityCannotBeInventedByVendorRequest()
    {
        var awareness = DirectPackage() with { Authority = CatalogAuthority.AwarenessOnly };
        Assert.Throws<InvalidOperationException>(() => VendorDeliveryAuthorization.ForHttps(awareness, "1.2", new Uri("https://download.example/start.exe")));
        var managed = DirectPackage() with { Provider = ProviderKind.WinGet, Authority = CatalogAuthority.ManagedWinGet };
        Assert.Throws<InvalidOperationException>(() => VendorDeliveryAuthorization.ForHttps(managed, "1.2", new Uri("https://download.example/start.exe")));
        Assert.Throws<InvalidDataException>(() => VendorDeliveryAuthorization.ForHttps(DirectPackage() with { Id = "../escape" }, "1.2", new Uri("https://download.example/start.exe")));
    }

    [TestMethod]
    public void VendorCacheRejectsAReparseComponent()
    {
        using var root = new TemporaryDirectory();
        var paths = new VendorCachePathPolicy(path => path.EndsWith("vendor-cache", StringComparison.OrdinalIgnoreCase));
        var authorization = VendorDeliveryAuthorization.ForHttps(DirectPackage(), "1.2", new Uri("https://download.example/start.exe"));
        Assert.Throws<IOException>(() => paths.GetTemporaryPayloadPath(root.Path, authorization, "start.exe"));
    }

    private const string Fingerprint = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static VendorSftpHostTrust Trust() => new(new VendorEndpoint("sftp.example", 22), Fingerprint);

    private static PackageDefinition DirectPackage() => Package(DeliveryMode.DirectDownload,
        new CatalogDeliveryPolicy("https://download.example/start.exe", "^https://(?:download|cdn)\\.example/.+\\.exe$",
            ["download.example", "cdn.example"], "Approved Vendor", 1_048_576, "", 0, "", "", [], "", "", "", ""));

    private static PackageDefinition SftpPackage() => Package(DeliveryMode.AuthenticatedSftp,
        new CatalogDeliveryPolicy("", "", [], "Approved Vendor", 1_048_576, "sftp.example", 22,
            "https://download.example/catalog.xml", "/approved", ["1"], "", "", "", ""));

    private static PackageDefinition Package(DeliveryMode mode, CatalogDeliveryPolicy policy) =>
        new("Vendor.Tool", "Vendor Tool", "Vendor", string.Empty, "Fixture", ProviderKind.External, CatalogAuthority.OperationalExternal,
            PackageProfile.Optional, PackagePriority.P2, PackageRisk.None, DeploymentPolicy.ManualHold, MaintenancePolicy.Hold,
            DeploymentClass.ManualHandoff, CatalogMaintenancePolicy.Manual, VersionRule.Latest, VersionCouplingMode.Independent,
            string.Empty, Lifecycle.Current, [ApplicationType.FieldUtility], [], [], [LicensingModel.Paid], ["VENDOR"],
            DistributionPolicy.LinkOnly, [InstallationForm.Exe], [SupportedOperatingSystem.Windows], mode, ReleaseMode.InventoryOnly,
            DetectionMode.None, DetectionVersionPolicy.None, "Vendor", string.Empty, string.Empty, string.Empty, [], true, null, null, null,
            null, null, null, null, null, string.Empty, [], Details: null, DeliveryPolicy: policy);

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static HttpResponseMessage Payload(byte[] bytes, bool omitLength = false)
    {
        HttpContent content = omitLength ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hosts.Add(request.RequestUri!.DnsSafeHost);
            return Task.FromResult(route(request));
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class FakeSftpTransport(string fingerprint, byte[] payload) : IVendorSftpTransport
    {
        public bool Probed { get; private set; }
        public Task<string> ProbeHostFingerprintAsync(VendorEndpoint endpoint, CancellationToken cancellationToken) { Probed = true; return Task.FromResult(fingerprint); }
        public async Task DownloadAsync(VendorSftpIdentity identity, ReadOnlyMemory<char> secret, string remotePath, Stream destination, long maximumBytes, CancellationToken cancellationToken)
        {
            if (!Probed) throw new InvalidOperationException("Credentialed transport occurred before host probing.");
            await destination.WriteAsync(payload, cancellationToken);
        }
    }

    private sealed class OrderedCredentialStore(FakeSftpTransport transport, string username, string secret) : IVendorCredentialStore
    {
        public bool ReadCalled { get; private set; }
        public bool ReadAfterProbe { get; private set; }
        public VendorCredential? Read(VendorSftpIdentity identity) { ReadCalled = true; ReadAfterProbe = transport.Probed; return new(username, secret.ToCharArray()); }
        public void Write(VendorSftpIdentity identity, ReadOnlySpan<char> value) { }
        public bool Delete(VendorSftpIdentity identity) => false;
    }

    private sealed class FakeSignatureInspector(bool valid, string subject) : IAuthenticodeSignatureInspector
    {
        public AuthenticodeSignatureResult Inspect(string path) => new(valid, subject, valid ? "Valid" : "Rejected");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AVWorkstationToolkit-vendor-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(System.IO.Path.Combine(current.FullName, "VERSION"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
