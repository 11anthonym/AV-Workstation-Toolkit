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
        var activated = await fixture.ActivateAsync(fixture.CreateBundle(1, 0), revision: 1);
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, activated.State);
        Assert.AreEqual(1L, activated.CurrentRevision);

        var restarted = fixture.CreateStore();
        var set = restarted.LoadActiveOrEmbedded();
        Assert.AreEqual(1L, set.Source.Revision);
        Assert.IsFalse(set.Source.IsEmbedded);
        Assert.IsGreaterThan(0, set.Hardware.Models.Count);
        Assert.IsGreaterThan(0, set.Compatibility.DeviceSoftwareRelations.Count);
        Assert.IsTrue(File.Exists(fixture.StoredHardwarePath(1)));
    }

    [TestMethod]
    public async Task SkippedAndSequentialRevisionsActivateButSameOrLowerRevisionsAreRejected()
    {
        using var fixture = new BundleFixture();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed,
            (await fixture.ActivateAsync(fixture.CreateBundle(43, 42), revision: 43, previousRevision: 42)).State);
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed,
            (await fixture.ActivateAsync(fixture.CreateBundle(45, 44), revision: 45, previousRevision: 44)).State);

        // The channel itself withholds a non-forward revision, so the store never sees a candidate.
        Assert.AreEqual(ReferenceCatalogUpdateState.Current,
            (await fixture.ActivateAsync(fixture.CreateBundle(45, 44), revision: 45, previousRevision: 44)).State);
        Assert.AreEqual(ReferenceCatalogUpdateState.Current,
            (await fixture.ActivateAsync(fixture.CreateBundle(44, 43), revision: 44, previousRevision: 43)).State);
        Assert.AreEqual(45L, fixture.CreateStore().LoadActiveOrEmbedded().Source.Revision);
    }

    [TestMethod]
    public async Task OnlyTheSelectedRevisionIsRetainedOnDisk()
    {
        using var fixture = new BundleFixture();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed,
            (await fixture.ActivateAsync(fixture.CreateBundle(20, 19), revision: 20, previousRevision: 19)).State);
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed,
            (await fixture.ActivateAsync(fixture.CreateBundle(30, 29), revision: 30, previousRevision: 29)).State);

        Assert.AreEqual(30L, fixture.CreateStore().LoadActiveOrEmbedded().Source.Revision);
        Assert.IsTrue(Directory.Exists(fixture.StoredCatalogDirectory(30)));
        Assert.IsFalse(Directory.Exists(fixture.StoredCatalogDirectory(20)));
        Assert.HasCount(1, Directory.GetDirectories(Path.Combine(fixture.DataRoot, "ReferenceCatalog", "catalogs")));
    }

    [TestMethod]
    public void ExplicitMissingDataRootIsCreatedWithoutChangingEmbeddedCatalogAuthority()
    {
        using var fixture = new BundleFixture();
        var missing = Path.Combine(fixture.DataRoot, "profile", "missing-root");
        var store = fixture.CreateStore(dataRootOverride: missing);
        var set = store.LoadActiveOrEmbedded();
        Assert.IsTrue(Directory.Exists(Path.Combine(missing, "ReferenceCatalog")));
        Assert.IsTrue(set.Source.IsEmbedded);
        Assert.IsGreaterThan(0, set.Hardware.Models.Count);
    }

    [TestMethod]
    public async Task InvalidSignatureHashExtraEntryAndFutureApplicationFailClosed()
    {
        using var fixture = new BundleFixture();
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var wrongSignature = fixture.CreateBundle(1, 0, signingKey: otherKey);
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await fixture.ActivateAsync(wrongSignature, revision: 1)).State);

        var badHash = fixture.CreateBundle(1, 0, corruptPayloadAfterSigning: true);
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await fixture.ActivateAsync(badHash, revision: 1)).State);

        var extra = fixture.CreateBundle(1, 0, extraEntry: true);
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await fixture.ActivateAsync(extra, revision: 1)).State);

        var future = fixture.CreateBundle(1, 0, minimumAppVersion: "9.9.9");
        Assert.AreEqual(ReferenceCatalogUpdateState.RequiresNewerApp, (await fixture.ActivateAsync(future, revision: 1)).State);

        Assert.IsTrue(fixture.CreateStore().LoadActiveOrEmbedded().Source.IsEmbedded);
    }

    [TestMethod]
    public async Task ExecutionShapedFieldsAndUntrustedConfigurationAreRejected()
    {
        using var fixture = new BundleFixture();
        var unknownField = fixture.CreateBundle(1, 0, addForbiddenHardwareField: true);
        var rejected = await fixture.ActivateAsync(unknownField, revision: 1);
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, rejected.State);

        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected,
            (await fixture.ActivateAsync(fixture.CreateBundle(3, 2), revision: 3, previousRevision: 2, trusted: false)).State);
        Assert.IsTrue(fixture.CreateStore().LoadActiveOrEmbedded().Source.IsEmbedded);
    }

    [TestMethod]
    public async Task StartupTamperDetectionDeletesActiveRevisionAndUsesEmbeddedFallback()
    {
        using var fixture = new BundleFixture();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await fixture.ActivateAsync(fixture.CreateBundle(1, 0), revision: 1)).State);
        fixture.EmbedBundle(fixture.CreateBundle(1, 0));

        var hardware = fixture.StoredHardwarePath(1);
        var bytes = File.ReadAllBytes(hardware);
        bytes[bytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(hardware, bytes);

        var set = fixture.CreateStore().LoadActiveOrEmbedded();
        Assert.IsTrue(set.Source.IsEmbedded);
        Assert.IsGreaterThan(0, set.Hardware.Models.Count);
        Assert.IsFalse(Directory.Exists(fixture.StoredCatalogDirectory(1)));
    }

    [TestMethod]
    public async Task OversizedBundleUnexpectedStoredFileAndMalformedStateFailClosedToEmbedded()
    {
        using var fixture = new BundleFixture();
        var oversized = fixture.CreateBundle(1, 0);
        using (var archive = ZipFile.Open(oversized, ZipArchiveMode.Update))
        {
            var entry = archive.CreateEntry("filler.bin", CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(new byte[1024]);
        }
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await fixture.ActivateAsync(oversized, revision: 1)).State);

        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await fixture.ActivateAsync(fixture.CreateBundle(1, 0), revision: 1)).State);
        File.WriteAllText(Path.Combine(fixture.StoredCatalogDirectory(1), "unexpected.json"), "{}");
        Assert.IsTrue(fixture.CreateStore().LoadActiveOrEmbedded().Source.IsEmbedded);

        File.WriteAllText(Path.Combine(fixture.DataRoot, "ReferenceCatalog", "state.json"), "{ not json");
        var recovered = fixture.CreateStore().LoadActiveOrEmbedded();
        Assert.IsTrue(recovered.Source.IsEmbedded);
        Assert.IsGreaterThan(0, recovered.Hardware.Models.Count);
    }

    [TestMethod]
    public void ManifestParserRejectsDuplicatePropertiesAndExecutionShapedMetadata()
    {
        var parser = new ReferenceCatalogManifestParser();
        Assert.ThrowsExactly<CatalogValidationException>(() => parser.ParseManifest(BundleFixture.ManifestJsonWithDuplicateRevision));
        Assert.ThrowsExactly<CatalogValidationException>(() => parser.ParseManifest(BundleFixture.ManifestJsonWithExecutionField));
    }

    [TestMethod]
    public async Task SignedExactChannelChecksDownloadsAndActivatesWithoutRedirectOrCallerSelectedUrl()
    {
        using var fixture = new BundleFixture();
        var bundle = fixture.CreateBundle(1, 0);
        using var channel = fixture.CreateChannel(bundle, revision: 1);
        var store = fixture.CreateStore(channel: channel);
        _ = store.LoadActiveOrEmbedded();

        var available = await store.CheckAsync();
        Assert.AreEqual(ReferenceCatalogUpdateState.UpdateAvailable, available.State);
        Assert.AreEqual(1L, available.AvailableRevision);
        Assert.IsNotNull(available.Changes);

        var installed = await store.InstallAvailableAsync();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, installed.State);
        Assert.AreEqual(1L, fixture.CreateStore().LoadActiveOrEmbedded().Source.Revision);
    }

    [TestMethod]
    public async Task OfflineChannelIsNonFatalAndSignedUnapprovedBundleOriginIsRejected()
    {
        using var fixture = new BundleFixture();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await fixture.ActivateAsync(fixture.CreateBundle(1, 0), revision: 1)).State);

        var offlineStore = fixture.CreateStore(channel: new ThrowingChannelClient());
        _ = offlineStore.LoadActiveOrEmbedded();
        Assert.AreEqual(ReferenceCatalogUpdateState.Offline, (await offlineStore.CheckAsync()).State);
        Assert.AreEqual(1L, fixture.CreateStore().LoadActiveOrEmbedded().Source.Revision);

        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected,
            (await fixture.ActivateAsync(fixture.CreateBundle(2, 1), revision: 2, previousRevision: 1, bundleHost: "mirror.example")).State);
        Assert.AreEqual(1L, fixture.CreateStore().LoadActiveOrEmbedded().Source.Revision);
    }

    [TestMethod]
    public async Task HighestRevisionWinsBetweenSignedEmbeddedAndRetainedCatalogs()
    {
        using var fixture = new BundleFixture();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed,
            (await fixture.ActivateAsync(fixture.CreateBundle(5, 4), revision: 5, previousRevision: 4)).State);
        Assert.AreEqual(5L, fixture.CreateStore().LoadActiveOrEmbedded().Source.Revision);

        // A newer signed embedded baseline from an application upgrade supersedes the retained snapshot.
        fixture.EmbedBundle(fixture.CreateBundle(9, 8));
        var upgraded = fixture.CreateStore().LoadActiveOrEmbedded();
        Assert.AreEqual(9L, upgraded.Source.Revision);
        Assert.IsTrue(upgraded.Source.IsEmbedded);
        Assert.IsFalse(Directory.Exists(fixture.StoredCatalogDirectory(5)));

        // Monotonic protection now measures from the embedded baseline.
        Assert.AreEqual(ReferenceCatalogUpdateState.Current,
            (await fixture.ActivateAsync(fixture.CreateBundle(7, 6), revision: 7, previousRevision: 6)).State);
    }

    [TestMethod]
    public async Task CatalogRequiringANewerApplicationIsNeverRetained()
    {
        using var fixture = new BundleFixture();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await fixture.ActivateAsync(fixture.CreateBundle(1, 0), revision: 1)).State);

        var futureBundle = fixture.CreateBundle(2, 1, minimumAppVersion: "1.3.0");
        Assert.AreEqual(ReferenceCatalogUpdateState.RequiresNewerApp,
            (await fixture.ActivateAsync(futureBundle, revision: 2, previousRevision: 1, bundleMinimumAppVersion: "1.3.0")).State);
        Assert.IsFalse(Directory.Exists(fixture.StoredCatalogDirectory(2)));
        Assert.AreEqual(1L, fixture.CreateStore().LoadActiveOrEmbedded().Source.Revision);

        // After the application upgrade the same revision is accepted from the channel again.
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed,
            (await fixture.ActivateAsync(futureBundle, revision: 2, previousRevision: 1, applicationVersion: "1.3.0",
                bundleMinimumAppVersion: "1.3.0")).State);
        Assert.AreEqual(2L, fixture.CreateStore(applicationVersion: "1.3.0").LoadActiveOrEmbedded().Source.Revision);

        // The channel pointer and the bundle must agree on the minimum application version.
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected,
            (await fixture.ActivateAsync(fixture.CreateBundle(3, 2, minimumAppVersion: "1.3.0"), revision: 3,
                previousRevision: 2, applicationVersion: "1.3.0", bundleMinimumAppVersion: "1.1.1")).State);
    }

    [TestMethod]
    public async Task LocalStartupDoesNotContactNetworkAndAutomaticOfflineCheckIsQuietAndThrottled()
    {
        using var fixture = new BundleFixture();
        var counting = new CountingOfflineChannel();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.Zero));
        var store = fixture.CreateStore(channel: counting, timeProvider: time);

        var set = store.LoadActiveOrEmbedded();
        Assert.IsTrue(set.Source.IsEmbedded);
        Assert.AreEqual(0, counting.CallCount);

        var priorState = store.Status.State;
        Assert.AreEqual(priorState, (await store.CheckInBackgroundIfDueAsync()).State);
        Assert.AreEqual(1, counting.CallCount);

        Assert.AreEqual(priorState, (await store.CheckInBackgroundIfDueAsync()).State);
        Assert.AreEqual(1, counting.CallCount);

        time.Advance(ReferenceCatalogStore.AutomaticCheckInterval + TimeSpan.FromMinutes(1));
        Assert.AreEqual(priorState, (await store.CheckInBackgroundIfDueAsync()).State);
        Assert.AreEqual(2, counting.CallCount);
    }

    [TestMethod]
    public void OrphanedActivationIsRecoveredAndStagingIsCleaned()
    {
        using var fixture = new BundleFixture();
        var bundle = fixture.CreateBundle(4, 3);
        fixture.ExtractStoredBundle(bundle, 4);

        var stagingRoot = Path.Combine(fixture.DataRoot, "ReferenceCatalog", "staging");
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(Path.Combine(stagingRoot, "abandoned"));
        File.WriteAllText(Path.Combine(fixture.DataRoot, "ReferenceCatalog", "state.abandoned.tmp"), "{}");

        var set = fixture.CreateStore().LoadActiveOrEmbedded();
        Assert.AreEqual(4L, set.Source.Revision);
        Assert.IsFalse(set.Source.IsEmbedded);
        Assert.IsEmpty(Directory.GetFileSystemEntries(stagingRoot));
        Assert.IsEmpty(Directory.GetFiles(Path.Combine(fixture.DataRoot, "ReferenceCatalog"), "state.*.tmp"));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.DataRoot, "ReferenceCatalog", "quarantine")));
    }

    [TestMethod]
    public async Task PerUserMutationLockRejectsConcurrentActivationWithoutDamagingCurrentCatalog()
    {
        using var fixture = new BundleFixture();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await fixture.ActivateAsync(fixture.CreateBundle(1, 0), revision: 1)).State);

        var bundle = fixture.CreateBundle(2, 1);
        using var channel = fixture.CreateChannel(bundle, revision: 2, previousRevision: 1);
        var store = fixture.CreateStore(channel: channel, mutationLockTimeout: TimeSpan.FromMilliseconds(250));
        _ = store.LoadActiveOrEmbedded();
        Assert.AreEqual(ReferenceCatalogUpdateState.UpdateAvailable, (await store.CheckAsync()).State);

        using var ready = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = Task.Run(() => fixture.HoldMutationLock(ready, release));
        ready.Wait(TimeSpan.FromSeconds(5));
        try
        {
            Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await store.InstallAvailableAsync()).State);
        }
        finally
        {
            release.Set();
            await holder;
        }
        Assert.AreEqual(1L, fixture.CreateStore().LoadActiveOrEmbedded().Source.Revision);
    }

    [TestMethod]
    public async Task TamperedDownloadAndBadNetworkEvidenceLeaveInstalledCatalogUsable()
    {
        using var fixture = new BundleFixture();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await fixture.ActivateAsync(fixture.CreateBundle(1, 0), revision: 1)).State);

        var tampered = fixture.CreateBundle(2, 1, corruptPayloadAfterSigning: true);
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await fixture.ActivateAsync(tampered, revision: 2, previousRevision: 1)).State);
        Assert.AreEqual(1L, fixture.CreateStore().LoadActiveOrEmbedded().Source.Revision);

        var invalidChannel = new InvalidChannelClient("Captive portal or unverifiable signed metadata.");
        var onlineStore = fixture.CreateStore(channel: invalidChannel);
        _ = onlineStore.LoadActiveOrEmbedded();
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await onlineStore.CheckAsync()).State);
        Assert.AreEqual(1L, fixture.CreateStore().LoadActiveOrEmbedded().Source.Revision);
    }

    [TestMethod]
    public async Task StaleSignedPointerStaysUsableWhileImplausiblyFutureMetadataIsRejected()
    {
        using var fixture = new BundleFixture();
        var now = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
        var candidate = fixture.CreateBundle(1, 0);

        // Age alone never invalidates an authentic signed pointer: descriptive catalog data carries no
        // execution authority, so a long-unchanged channel must keep working without being re-signed.
        using (var stale = fixture.CreateChannel(candidate, revision: 1, observedNow: now, createdUtc: now.AddDays(-400)))
        {
            var store = fixture.CreateStore(channel: stale);
            _ = store.LoadActiveOrEmbedded();
            Assert.AreEqual(ReferenceCatalogUpdateState.UpdateAvailable, (await store.CheckAsync()).State);
            Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await store.InstallAvailableAsync()).State);
        }
        Assert.AreEqual(1L, fixture.CreateStore().LoadActiveOrEmbedded().Source.Revision);

        using (var future = fixture.CreateChannel(fixture.CreateBundle(2, 1), revision: 2, previousRevision: 1,
                   observedNow: now, createdUtc: now.AddMinutes(6)))
        {
            var store = fixture.CreateStore(channel: future);
            _ = store.LoadActiveOrEmbedded();
            Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await store.CheckAsync()).State);
        }
        Assert.AreEqual(1L, fixture.CreateStore().LoadActiveOrEmbedded().Source.Revision);
    }

    [TestMethod]
    public async Task AlreadySignedPointerCarryingRetiredExpiryFieldRemainsAcceptable()
    {
        using var fixture = new BundleFixture();
        var now = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
        using var channel = fixture.CreateChannel(fixture.CreateBundle(1, 0), revision: 1, observedNow: now,
            createdUtc: now.AddDays(-90), legacyExpiresUtc: now.AddDays(-60));
        var store = fixture.CreateStore(channel: channel);
        _ = store.LoadActiveOrEmbedded();
        Assert.AreEqual(ReferenceCatalogUpdateState.UpdateAvailable, (await store.CheckAsync()).State);
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await store.InstallAvailableAsync()).State);
    }

    [TestMethod]
    public async Task StateWriteFailureAfterCommitStillReportsActivation()
    {
        using var fixture = new BundleFixture();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await fixture.ActivateAsync(fixture.CreateBundle(1, 0), revision: 1)).State);

        var statePath = Path.Combine(fixture.DataRoot, "ReferenceCatalog", "state.json");
        using var channel = fixture.CreateChannel(fixture.CreateBundle(2, 1), revision: 2, previousRevision: 1);
        var store = fixture.CreateStore(channel: channel);
        _ = store.LoadActiveOrEmbedded();
        Assert.AreEqual(ReferenceCatalogUpdateState.UpdateAvailable, (await store.CheckAsync()).State);

        ReferenceCatalogUpdateStatus installed;
        using (var lockedState = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            installed = await store.InstallAvailableAsync();
        }

        // The revision committed, so the reported outcome must say so. Reporting rejection here would
        // contradict the catalog the next launch actually selects.
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, installed.State);
        Assert.AreEqual(2L, installed.CurrentRevision);
        var restarted = fixture.CreateStore();
        Assert.AreEqual(2L, restarted.LoadActiveOrEmbedded().Source.Revision);
    }

    [TestMethod]
    public async Task ObsoleteRevisionCleanupFailureAfterCommitStillReportsActivation()
    {
        using var fixture = new BundleFixture();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await fixture.ActivateAsync(fixture.CreateBundle(1, 0), revision: 1)).State);

        using var channel = fixture.CreateChannel(fixture.CreateBundle(2, 1), revision: 2, previousRevision: 1);
        var store = fixture.CreateStore(channel: channel);
        _ = store.LoadActiveOrEmbedded();
        Assert.AreEqual(ReferenceCatalogUpdateState.UpdateAvailable, (await store.CheckAsync()).State);

        ReferenceCatalogUpdateStatus installed;
        // Hold a file inside the superseded revision so its post-commit deletion fails.
        using (var pinned = new FileStream(fixture.StoredHardwarePath(1), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            installed = await store.InstallAvailableAsync();
            Assert.IsTrue(Directory.Exists(fixture.StoredCatalogDirectory(1)));
        }

        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, installed.State);
        Assert.AreEqual(2L, installed.CurrentRevision);

        var restarted = fixture.CreateStore();
        Assert.AreEqual(2L, restarted.LoadActiveOrEmbedded().Source.Revision);
        // Startup owns the deferred cleanup once the file is no longer pinned.
        Assert.IsFalse(Directory.Exists(fixture.StoredCatalogDirectory(1)));
    }

    [TestMethod]
    public async Task PreCommitFailureReportsRejectionAndActivatesNothing()
    {
        using var fixture = new BundleFixture();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await fixture.ActivateAsync(fixture.CreateBundle(1, 0), revision: 1)).State);

        using var channel = fixture.CreateChannel(fixture.CreateBundle(2, 1), revision: 2, previousRevision: 1);
        var store = fixture.CreateStore(channel: channel);
        _ = store.LoadActiveOrEmbedded();
        Assert.AreEqual(ReferenceCatalogUpdateState.UpdateAvailable, (await store.CheckAsync()).State);

        // Occupy the destination so the commit move itself fails. Directory.Move refuses to overwrite,
        // which is what keeps an already-committed revision immutable.
        Directory.CreateDirectory(fixture.StoredCatalogDirectory(2));
        var installed = await store.InstallAvailableAsync();

        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, installed.State);
        Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(fixture.DataRoot, "ReferenceCatalog", "staging")));

        // The occupying directory is not a verified catalog, so startup discards it and keeps revision 1.
        var restarted = fixture.CreateStore();
        Assert.AreEqual(1L, restarted.LoadActiveOrEmbedded().Source.Revision);
        Assert.IsFalse(Directory.Exists(fixture.StoredCatalogDirectory(2)));
    }

    [TestMethod]
    public void LauncherAndReleaseBuildDefineSignedEmbeddedBaselineWithoutARepositoryKey()
    {
        var project = File.ReadAllText(Path.Combine(BundleFixture.RepositoryRootPath(), "src", "AVWorkstationToolkit.Launcher", "AVWorkstationToolkit.Launcher.csproj"));
        var release = File.ReadAllText(Path.Combine(BundleFixture.RepositoryRootPath(), "build", "Build-Release.ps1"));
        var app = File.ReadAllText(Path.Combine(BundleFixture.RepositoryRootPath(), "src", "AVWorkstationToolkit.App", "App.xaml.cs"));

        StringAssert.Contains(project, "AVWT-Reference-Catalog.avwtcatalog");
        StringAssert.Contains(project, "ReferenceCatalogBaselinePath");
        StringAssert.Contains(release, "Production release builds require a signed embedded .avwtcatalog baseline.");
        Assert.IsLessThan(app.IndexOf("CheckCatalogFreshnessAfterStartupAsync(referenceCatalogUpdates)", StringComparison.Ordinal), app.IndexOf("window.Show();", StringComparison.Ordinal));
        StringAssert.Contains(app, "Task.Delay(TimeSpan.FromSeconds(5))");
        Assert.IsFalse(project.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(release.Contains("BEGIN EC PRIVATE KEY", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class BundleFixture : IDisposable
    {
        public const string ManifestJsonWithDuplicateRevision =
            """
            {"CatalogId":"avwt-reference","CatalogVersion":"2026.9.12.1","Revision":1,"Revision":2,"SchemaVersion":1,"CompatibilityEpoch":1,"CreatedUtc":"2026-09-12T00:00:00Z","PreviousRevision":0,"MinimumAppVersion":"1.1.1","SigningKeyId":"test","Counts":{"Manufacturers":1,"Families":1,"ExactModels":1,"ReferenceSoftwareProducts":1,"Relations":1},"Files":{"hardware-identities.json":{"Sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"},"software-compatibility.json":{"Sha256":"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"},"catalog-changes.json":{"Sha256":"CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC"}}}
            """;

        public const string ManifestJsonWithExecutionField =
            """
            {"CatalogId":"avwt-reference","CatalogVersion":"2026.9.12.1","Revision":1,"SchemaVersion":1,"CompatibilityEpoch":1,"CreatedUtc":"2026-09-12T00:00:00Z","PreviousRevision":0,"MinimumAppVersion":"1.1.1","SigningKeyId":"test","Command":"cmd.exe","Counts":{"Manufacturers":1,"Families":1,"ExactModels":1,"ReferenceSoftwareProducts":1,"Relations":1},"Files":{"hardware-identities.json":{"Sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"},"software-compatibility.json":{"Sha256":"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"},"catalog-changes.json":{"Sha256":"CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC"}}}
            """;

        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly string root = Path.Combine(Path.GetTempPath(), $"awt-catalog-update-{Guid.NewGuid():N}");

        public BundleFixture()
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(DataRoot);
            Directory.CreateDirectory(Path.Combine(EmbeddedRoot, "manifests"));
            File.Copy(ManifestPath(ReferenceCatalogBundleNames.Hardware), Path.Combine(EmbeddedRoot, "manifests", ReferenceCatalogBundleNames.Hardware));
            File.Copy(ManifestPath(ReferenceCatalogBundleNames.Compatibility), Path.Combine(EmbeddedRoot, "manifests", ReferenceCatalogBundleNames.Compatibility));
        }

        public string DataRoot => Path.Combine(root, "data");
        public string EmbeddedRoot => Path.Combine(root, "application");

        public ReferenceCatalogStore CreateStore(
            bool trusted = true,
            IReferenceCatalogChannelClient? channel = null,
            string? dataRootOverride = null,
            string applicationVersion = "1.1.1",
            TimeProvider? timeProvider = null,
            TimeSpan? mutationLockTimeout = null)
        {
            IReadOnlyDictionary<string, string> keys = trusted
                ? new Dictionary<string, string>(StringComparer.Ordinal) { ["test-2026-a"] = key.ExportSubjectPublicKeyInfoPem() }
                : new Dictionary<string, string>(StringComparer.Ordinal);
            return new(EmbeddedRoot, dataRootOverride ?? DataRoot,
                new ReferenceCatalogBundleVerifier(new(applicationVersion, keys)), channel, timeProvider, mutationLockTimeout);
        }

        /// <summary>Drives the production path: signed channel check, then activation.</summary>
        public async Task<ReferenceCatalogUpdateStatus> ActivateAsync(
            string bundlePath,
            long revision,
            long previousRevision = 0,
            string applicationVersion = "1.1.1",
            bool trusted = true,
            string bundleHost = "catalog.avwt.example",
            TimeSpan? mutationLockTimeout = null,
            string bundleMinimumAppVersion = "1.1.1")
        {
            using var channel = CreateChannel(bundlePath, bundleHost, revision, previousRevision,
                minimumAppVersion: bundleMinimumAppVersion);
            var store = CreateStore(trusted: trusted, channel: channel, applicationVersion: applicationVersion,
                mutationLockTimeout: mutationLockTimeout);
            _ = store.LoadActiveOrEmbedded();
            var available = await store.CheckAsync();
            return available.State == ReferenceCatalogUpdateState.UpdateAvailable
                ? await store.InstallAvailableAsync()
                : available;
        }

        public void EmbedBundle(string path)
        {
            var directory = Path.Combine(EmbeddedRoot, "reference-catalog");
            Directory.CreateDirectory(directory);
            File.Copy(path, Path.Combine(directory, "AVWT-Reference-Catalog.avwtcatalog"), overwrite: true);
        }

        public ReferenceCatalogChannelClient CreateChannel(
            string bundlePath,
            string bundleHost = "catalog.avwt.example",
            long revision = 1,
            long previousRevision = 0,
            DateTimeOffset? observedNow = null,
            DateTimeOffset? createdUtc = null,
            DateTimeOffset? legacyExpiresUtc = null,
            string minimumAppVersion = "1.1.1")
        {
            var bundle = File.ReadAllBytes(bundlePath);
            var now = observedNow ?? DateTimeOffset.UtcNow;
            var metadataUri = new Uri("https://catalog.avwt.example/catalog-channel.json");
            var signatureUri = new Uri("https://catalog.avwt.example/catalog-channel.sig");
            var bundleUri = new Uri($"https://{bundleHost}/catalog-{revision}.avwtcatalog");
            var document = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["CatalogId"] = ReferenceCatalogBundleNames.CatalogId,
                ["SchemaVersion"] = 1,
                ["CatalogVersion"] = $"2026.9.12.{revision}",
                ["Revision"] = revision,
                ["PreviousRevision"] = previousRevision,
                ["MinimumAppVersion"] = minimumAppVersion,
                ["CreatedUtc"] = createdUtc ?? now.AddMinutes(-1),
                ["SigningKeyId"] = "test-2026-a",
                ["BundleUri"] = bundleUri.AbsoluteUri,
                ["BundleSha256"] = Convert.ToHexString(SHA256.HashData(bundle))
            };
            if (legacyExpiresUtc is not null) document["ExpiresUtc"] = legacyExpiresUtc.Value;
            var metadata = JsonSerializer.SerializeToUtf8Bytes(document);
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
            return new(policy, handler, ownsHandler: true, timeProvider: new ManualTimeProvider(now));
        }

        public string StoredHardwarePath(long revision) =>
            Path.Combine(StoredCatalogDirectory(revision), ReferenceCatalogBundleNames.Hardware);

        public string StoredCatalogDirectory(long revision) =>
            Path.Combine(DataRoot, "ReferenceCatalog", "catalogs", revision.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public void ExtractStoredBundle(string bundlePath, long revision)
        {
            var destination = StoredCatalogDirectory(revision);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            ZipFile.ExtractToDirectory(bundlePath, destination);
        }

        public void HoldMutationLock(ManualResetEventSlim ready, ManualResetEventSlim release)
        {
            var catalogRoot = Path.Combine(DataRoot, "ReferenceCatalog");
            var normalized = Path.GetFullPath(catalogRoot).ToUpperInvariant();
            var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32];
            using var mutex = new Mutex(false, $"Local\\AVWT.ReferenceCatalog.{suffix}");
            var acquired = false;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
                ready.Set();
                if (acquired) release.Wait(TimeSpan.FromSeconds(10));
            }
            finally
            {
                if (acquired) mutex.ReleaseMutex();
            }
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

        public static string RepositoryRootPath()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "AVWorkstationToolkit.slnx"))) current = current.Parent;
            return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        }

        private static string ManifestPath(string name) => Path.Combine(RepositoryRootPath(), "manifests", name);

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

    private sealed class CountingOfflineChannel : IReferenceCatalogChannelClient
    {
        private int callCount;
        public int CallCount => Volatile.Read(ref callCount);

        public Task<ReferenceCatalogChannelPackage?> GetLatestAsync(long currentRevision, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref callCount);
            return Task.FromException<ReferenceCatalogChannelPackage?>(new HttpRequestException("Fixture channel is offline."));
        }
    }

    private sealed class InvalidChannelClient(string message) : IReferenceCatalogChannelClient
    {
        public Task<ReferenceCatalogChannelPackage?> GetLatestAsync(long currentRevision, CancellationToken cancellationToken = default) =>
            Task.FromException<ReferenceCatalogChannelPackage?>(new CatalogValidationException(message));
    }

    private sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset current = current;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan value) => current = current.Add(value);
    }
}
