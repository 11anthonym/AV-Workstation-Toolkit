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
        StringAssert.Contains(result.Detail, "activated on disk");
        StringAssert.Contains(result.Detail, "Restart AV Workstation Toolkit");

        var loaded = store.LoadActiveOrEmbedded();
        Assert.IsFalse(loaded.Source.IsEmbedded);
        Assert.AreEqual(1L, loaded.Source.Revision);
        Assert.HasCount(529, loaded.Hardware.Models);
        Assert.HasCount(338, loaded.Compatibility.DeviceSoftwareRelations);
        Assert.IsTrue(Directory.Exists(Path.Combine(fixture.DataRoot, "ReferenceCatalog", "catalogs", "1")));
        Assert.HasCount(0, Directory.GetDirectories(Path.Combine(fixture.DataRoot, "ReferenceCatalog", "staging")));
    }

    [TestMethod]
    public async Task CompleteSnapshotsAllowSkippedAndSequentialRevisionsButRejectSameOrLowerRevisions()
    {
        using var fixture = new BundleFixture();
        var skippedStore = fixture.CreateStore();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await skippedStore.ImportAsync(fixture.CreateBundle(43, 42))).State);

        var skipped = await skippedStore.ImportAsync(fixture.CreateBundle(45, 44));
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, skipped.State);
        Assert.AreEqual(45L, skipped.CurrentRevision);
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await skippedStore.ImportAsync(fixture.CreateBundle(45, 44))).State);
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await skippedStore.ImportAsync(fixture.CreateBundle(44, 43))).State);

        using var sequentialFixture = new BundleFixture();
        var sequentialStore = sequentialFixture.CreateStore();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await sequentialStore.ImportAsync(sequentialFixture.CreateBundle(43, 42))).State);
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await sequentialStore.ImportAsync(sequentialFixture.CreateBundle(44, 43))).State);
    }

    [TestMethod]
    public async Task RestorePersistsSuppressesRolledBackRevisionAndAllowsNewerRevision()
    {
        using var fixture = new BundleFixture();
        var store = fixture.CreateStore();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await store.ImportAsync(fixture.CreateBundle(43, 42))).State);
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await store.ImportAsync(fixture.CreateBundle(45, 44))).State);
        _ = store.LoadActiveOrEmbedded();
        Assert.AreEqual(43L, store.Status.RestorableRevision);

        var restored = await store.RestorePreviousAsync();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, restored.State);
        Assert.AreEqual(43L, restored.CurrentRevision);
        Assert.AreEqual(0L, restored.RestorableRevision);
        StringAssert.Contains(restored.Detail, "Restart AV Workstation Toolkit");

        var restarted = fixture.CreateStore();
        Assert.AreEqual(43L, restarted.LoadActiveOrEmbedded().Source.Revision);
        Assert.AreEqual(0L, restarted.Status.RestorableRevision);

        var suppressedBundle = fixture.CreateBundle(45, 44);
        using var suppressedChannel = fixture.CreateChannel(suppressedBundle, revision: 45, previousRevision: 44);
        var suppressedStore = fixture.CreateStore(channel: suppressedChannel);
        _ = suppressedStore.LoadActiveOrEmbedded();
        Assert.AreEqual(ReferenceCatalogUpdateState.Current, (await suppressedStore.CheckAsync()).State);

        var newerBundle = fixture.CreateBundle(46, 45);
        using var newerChannel = fixture.CreateChannel(newerBundle, revision: 46, previousRevision: 45);
        var newerStore = fixture.CreateStore(channel: newerChannel);
        _ = newerStore.LoadActiveOrEmbedded();
        Assert.AreEqual(ReferenceCatalogUpdateState.UpdateAvailable, (await newerStore.CheckAsync()).State);
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await newerStore.InstallAvailableAsync()).State);
        Assert.AreEqual(46L, newerStore.Status.CurrentRevision);
    }

    [TestMethod]
    public async Task CorruptPreviousCannotBeRestoredAndCorruptRestoredCatalogFallsBackToEmbedded()
    {
        using var fixture = new BundleFixture();
        var store = fixture.CreateStore();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await store.ImportAsync(fixture.CreateBundle(43, 42))).State);
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await store.ImportAsync(fixture.CreateBundle(45, 44))).State);
        File.AppendAllText(fixture.StoredHardwarePath(43), " ", Encoding.UTF8);

        _ = store.LoadActiveOrEmbedded();
        Assert.AreEqual(0L, store.Status.RestorableRevision);
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await store.RestorePreviousAsync()).State);

        using var fallbackFixture = new BundleFixture();
        var fallbackStore = fallbackFixture.CreateStore();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await fallbackStore.ImportAsync(fallbackFixture.CreateBundle(43, 42))).State);
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await fallbackStore.ImportAsync(fallbackFixture.CreateBundle(45, 44))).State);
        _ = fallbackStore.LoadActiveOrEmbedded();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await fallbackStore.RestorePreviousAsync()).State);
        File.AppendAllText(fallbackFixture.StoredHardwarePath(43), " ", Encoding.UTF8);

        var fallback = fallbackFixture.CreateStore().LoadActiveOrEmbedded();
        Assert.IsTrue(fallback.Source.IsEmbedded);
        Assert.AreEqual(0L, fallback.Source.Revision);
    }

    [TestMethod]
    public void ExplicitMissingDataRootIsCreatedWithoutChangingEmbeddedCatalogAuthority()
    {
        using var fixture = new BundleFixture();
        var dataRoot = Path.Combine(fixture.DataRoot, "clean-profile-root");
        Assert.IsFalse(Directory.Exists(dataRoot));

        var loaded = fixture.CreateStore(dataRootOverride: dataRoot).LoadActiveOrEmbedded();

        Assert.IsTrue(Directory.Exists(dataRoot));
        Assert.IsTrue(loaded.Source.IsEmbedded);
        Assert.HasCount(529, loaded.Hardware.Models);
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
        StringAssert.Contains(installed.Detail, "activated on disk");
        StringAssert.Contains(installed.Detail, "Restart AV Workstation Toolkit");
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

    [TestMethod]
    public async Task SignedEmbeddedAndRetainedCatalogsUseHighestCompatibleNonSuppressedSnapshot()
    {
        using var embeddedUpgrade = new BundleFixture();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed,
            (await embeddedUpgrade.CreateStore().ImportAsync(embeddedUpgrade.CreateBundle(5, 4))).State);
        embeddedUpgrade.EmbedBundle(embeddedUpgrade.CreateBundle(10, 9));

        var embeddedSelected = embeddedUpgrade.CreateStore().LoadActiveOrEmbedded();
        Assert.IsTrue(embeddedSelected.Source.IsEmbedded);
        Assert.AreEqual(10L, embeddedSelected.Source.Revision);
        var embeddedStore = embeddedUpgrade.CreateStore();
        _ = embeddedStore.LoadActiveOrEmbedded();
        Assert.AreEqual(5L, embeddedStore.Status.RestorableRevision);
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await embeddedStore.RestorePreviousAsync()).State);
        Assert.AreEqual(5L, embeddedUpgrade.CreateStore().LoadActiveOrEmbedded().Source.Revision);

        using var retainedUpgrade = new BundleFixture();
        retainedUpgrade.EmbedBundle(retainedUpgrade.CreateBundle(5, 4));
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed,
            (await retainedUpgrade.CreateStore().ImportAsync(retainedUpgrade.CreateBundle(10, 9))).State);

        var retainedSelected = retainedUpgrade.CreateStore().LoadActiveOrEmbedded();
        Assert.IsFalse(retainedSelected.Source.IsEmbedded);
        Assert.AreEqual(10L, retainedSelected.Source.Revision);
    }

    [TestMethod]
    public async Task DowngradeRetainsValidIncompatibleCatalogAndUpgradeUsesItAgain()
    {
        using var fixture = new BundleFixture();
        fixture.EmbedBundle(fixture.CreateBundle(20, 19, minimumAppVersion: "1.1.0"));
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed,
            (await fixture.CreateStore(applicationVersion: "1.3.0").ImportAsync(
                fixture.CreateBundle(30, 29, minimumAppVersion: "1.3.0"))).State);

        var downgraded = fixture.CreateStore(applicationVersion: "1.2.0").LoadActiveOrEmbedded();
        Assert.AreEqual(20L, downgraded.Source.Revision);
        Assert.IsTrue(downgraded.Source.IsEmbedded);
        StringAssert.Contains(downgraded.Source.Detail, "requires a newer");
        Assert.IsTrue(Directory.Exists(fixture.StoredCatalogDirectory(30)));
        Assert.HasCount(0, Directory.GetFileSystemEntries(fixture.QuarantineRoot));

        var upgradedAgain = fixture.CreateStore(applicationVersion: "1.3.0").LoadActiveOrEmbedded();
        Assert.AreEqual(30L, upgradedAgain.Source.Revision);
        Assert.IsFalse(upgradedAgain.Source.IsEmbedded);
    }

    [TestMethod]
    public async Task LocalStartupDoesNotContactNetworkAndAutomaticOfflineCheckIsQuietAndThrottled()
    {
        using var fixture = new BundleFixture();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero));
        var channel = new CountingOfflineChannel();
        var store = fixture.CreateStore(channel: channel, timeProvider: clock);

        var local = store.LoadActiveOrEmbedded();
        Assert.IsTrue(local.Source.IsEmbedded);
        Assert.AreEqual(0, channel.CallCount);

        var first = await store.CheckInBackgroundIfDueAsync();
        Assert.AreEqual(ReferenceCatalogUpdateState.Current, first.State);
        Assert.AreEqual(1, channel.CallCount);
        Assert.AreEqual(local.Source.Revision, store.Status.CurrentRevision);

        _ = await store.CheckInBackgroundIfDueAsync();
        Assert.AreEqual(1, channel.CallCount);
        clock.Advance(TimeSpan.FromHours(25));
        _ = await store.CheckInBackgroundIfDueAsync();
        Assert.AreEqual(2, channel.CallCount);

        clock.Advance(TimeSpan.FromDays(-2));
        _ = await fixture.CreateStore(channel: channel, timeProvider: clock).CheckInBackgroundIfDueAsync();
        Assert.AreEqual(2, channel.CallCount, "An implausibly future persisted check time must not trigger a request loop.");
    }

    [TestMethod]
    public void OrphanedCompleteActivationIsRecoveredWhileStagingAndQuarantineAreBounded()
    {
        using var fixture = new BundleFixture();
        fixture.ExtractStoredBundle(fixture.CreateBundle(9, 8), 9);
        var staging = Path.Combine(fixture.DataRoot, "ReferenceCatalog", "staging", "interrupted");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "partial.tmp"), "partial", Encoding.UTF8);
        Directory.CreateDirectory(fixture.QuarantineRoot);
        for (var index = 0; index < 12; index++)
        {
            var path = Path.Combine(fixture.QuarantineRoot, $"old-{index:D2}.bin");
            File.WriteAllText(path, "invalid", Encoding.UTF8);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-index));
        }

        var recovered = fixture.CreateStore().LoadActiveOrEmbedded();

        Assert.AreEqual(9L, recovered.Source.Revision);
        Assert.IsFalse(recovered.Source.IsEmbedded);
        Assert.HasCount(0, Directory.GetFileSystemEntries(Path.Combine(fixture.DataRoot, "ReferenceCatalog", "staging")));
        Assert.HasCount(8, Directory.GetFileSystemEntries(fixture.QuarantineRoot));
    }

    [TestMethod]
    public void ObsoleteCompatibleSnapshotsAreBoundedWithoutDeletingSelectedOrIncompatibleData()
    {
        using var fixture = new BundleFixture();
        for (var revision = 1; revision <= 5; revision++)
            fixture.ExtractStoredBundle(fixture.CreateBundle(revision, revision - 1), revision);

        Assert.AreEqual(5L, fixture.CreateStore().LoadActiveOrEmbedded().Source.Revision);
        CollectionAssert.AreEquivalent(new[] { "3", "4", "5" },
            Directory.GetDirectories(Path.Combine(fixture.DataRoot, "ReferenceCatalog", "catalogs")).Select(Path.GetFileName).ToArray());

        using var incompatible = new BundleFixture();
        incompatible.ExtractStoredBundle(incompatible.CreateBundle(10, 9, minimumAppVersion: "9.0.0"), 10);
        _ = incompatible.CreateStore(applicationVersion: "1.1.1").LoadActiveOrEmbedded();
        Assert.IsTrue(Directory.Exists(incompatible.StoredCatalogDirectory(10)));
    }

    [TestMethod]
    public async Task CorruptActiveFallsThroughPreviousThenSignedEmbeddedWithoutPartialMerge()
    {
        using var fixture = new BundleFixture();
        fixture.EmbedBundle(fixture.CreateBundle(10, 9));
        var store = fixture.CreateStore();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await store.ImportAsync(fixture.CreateBundle(20, 19))).State);
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await store.ImportAsync(fixture.CreateBundle(30, 29))).State);
        File.AppendAllText(fixture.StoredHardwarePath(30), " ", Encoding.UTF8);

        Assert.AreEqual(20L, fixture.CreateStore().LoadActiveOrEmbedded().Source.Revision);
        File.AppendAllText(fixture.StoredHardwarePath(20), " ", Encoding.UTF8);
        var embedded = fixture.CreateStore().LoadActiveOrEmbedded();
        Assert.AreEqual(10L, embedded.Source.Revision);
        Assert.IsTrue(embedded.Source.IsEmbedded);
    }

    [TestMethod]
    public async Task PerUserMutationLockRejectsConcurrentImportWithoutDamagingCurrentCatalog()
    {
        using var fixture = new BundleFixture();
        var store = fixture.CreateStore(mutationLockTimeout: TimeSpan.FromMilliseconds(100));
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await store.ImportAsync(fixture.CreateBundle(1, 0))).State);
        var next = fixture.CreateBundle(2, 1);
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => fixture.HoldMutationLock(ready, release));
        Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(5)));

        var blocked = await store.ImportAsync(next);
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, blocked.State);
        StringAssert.Contains(blocked.Detail, "Another AV Workstation Toolkit instance");
        Assert.AreEqual(1L, fixture.CreateStore().LoadActiveOrEmbedded().Source.Revision);

        release.Set();
        await holder;
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await fixture.CreateStore().ImportAsync(next)).State);
    }

    [TestMethod]
    public async Task TamperedOfflineImportAndBadNetworkEvidenceLeaveInstalledCatalogUsable()
    {
        using var fixture = new BundleFixture();
        var store = fixture.CreateStore();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await store.ImportAsync(fixture.CreateBundle(1, 0))).State);
        var tampered = fixture.CreateBundle(2, 1, corruptPayloadAfterSigning: true);
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await store.ImportAsync(tampered)).State);
        Assert.AreEqual(1L, fixture.CreateStore().LoadActiveOrEmbedded().Source.Revision);

        var invalidChannel = new InvalidChannelClient("Captive portal or expired/future signed metadata.");
        var onlineStore = fixture.CreateStore(channel: invalidChannel);
        _ = onlineStore.LoadActiveOrEmbedded();
        Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await onlineStore.CheckAsync()).State);
        Assert.AreEqual(1L, fixture.CreateStore().LoadActiveOrEmbedded().Source.Revision);
    }

    [TestMethod]
    public async Task ExpiredAndImplausiblyFutureChannelMetadataNeverInvalidatesInstalledData()
    {
        using var fixture = new BundleFixture();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed,
            (await fixture.CreateStore().ImportAsync(fixture.CreateBundle(1, 0))).State);
        var now = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
        var candidate = fixture.CreateBundle(2, 1);
        using (var expired = fixture.CreateChannel(candidate, revision: 2, previousRevision: 1,
                   observedNow: now, createdUtc: now.AddDays(-2), expiresUtc: now.AddMinutes(-1)))
        {
            var store = fixture.CreateStore(channel: expired);
            _ = store.LoadActiveOrEmbedded();
            Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await store.CheckAsync()).State);
        }
        using (var future = fixture.CreateChannel(candidate, revision: 2, previousRevision: 1,
                   observedNow: now, createdUtc: now.AddMinutes(6), expiresUtc: now.AddDays(1)))
        {
            var store = fixture.CreateStore(channel: future);
            _ = store.LoadActiveOrEmbedded();
            Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, (await store.CheckAsync()).State);
        }
        Assert.AreEqual(1L, fixture.CreateStore().LoadActiveOrEmbedded().Source.Revision);
    }

    [TestMethod]
    public async Task StateWriteFailureLeavesPriorStateAndCompleteSnapshotRecoverable()
    {
        using var fixture = new BundleFixture();
        var store = fixture.CreateStore();
        Assert.AreEqual(ReferenceCatalogUpdateState.Completed, (await store.ImportAsync(fixture.CreateBundle(1, 0))).State);
        var statePath = Path.Combine(fixture.DataRoot, "ReferenceCatalog", "state.json");
        using (var lockedState = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failed = await store.ImportAsync(fixture.CreateBundle(2, 1));
            Assert.AreEqual(ReferenceCatalogUpdateState.Rejected, failed.State);
        }

        var restarted = fixture.CreateStore();
        var recovered = restarted.LoadActiveOrEmbedded();
        Assert.AreEqual(2L, recovered.Source.Revision);
        Assert.AreEqual(1L, restarted.Status.RestorableRevision);
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
        public string QuarantineRoot => Path.Combine(DataRoot, "ReferenceCatalog", "quarantine");

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
            DateTimeOffset? expiresUtc = null)
        {
            var bundle = File.ReadAllBytes(bundlePath);
            var now = observedNow ?? DateTimeOffset.UtcNow;
            var metadataUri = new Uri("https://catalog.avwt.example/catalog-channel.json");
            var signatureUri = new Uri("https://catalog.avwt.example/catalog-channel.sig");
            var bundleUri = new Uri($"https://{bundleHost}/catalog-{revision}.avwtcatalog");
            var metadata = JsonSerializer.SerializeToUtf8Bytes(new
            {
                CatalogId = ReferenceCatalogBundleNames.CatalogId,
                SchemaVersion = 1,
                CatalogVersion = $"2026.9.12.{revision}",
                Revision = revision,
                PreviousRevision = previousRevision,
                MinimumAppVersion = "1.1.1",
                CreatedUtc = createdUtc ?? now.AddMinutes(-1),
                ExpiresUtc = expiresUtc ?? now.AddDays(7),
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
            return new(policy, handler, ownsHandler: true, timeProvider: new ManualTimeProvider(now));
        }

        public string StoredHardwarePath(long revision) =>
            Path.Combine(DataRoot, "ReferenceCatalog", "catalogs", revision.ToString(System.Globalization.CultureInfo.InvariantCulture), ReferenceCatalogBundleNames.Hardware);

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

        private static string RepositoryRoot() => RepositoryRootPath();

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
