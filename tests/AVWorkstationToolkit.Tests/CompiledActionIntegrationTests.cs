using System.Text;
using AVWorkstationToolkit.App.ViewModels;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Details;
using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Application.Vendors;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Infrastructure.Windows.Diagnostics;
using AVWorkstationToolkit.Infrastructure.Windows.Processes;
using AVWorkstationToolkit.Infrastructure.Windows.Vendors;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class CompiledActionIntegrationTests
{
    [TestMethod]
    public void OpenLogsPathUsesCanonicalContainedLogsDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"awt-open-logs-{Guid.NewGuid():N}");
        try
        {
            var logs = WindowsValidatedUserHandoffService.PrepareLogsDirectory(root);

            Assert.AreEqual(Path.Combine(Path.GetFullPath(root), "logs"), logs);
            Assert.IsTrue(Directory.Exists(logs));
            Assert.Throws<IOException>(() =>
                WindowsValidatedUserHandoffService.PrepareLogsDirectory(Path.GetPathRoot(root)!));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ValidSelectionRunsCorrelatedFlowAndRefreshesPlan()
    {
        var current = Plan(State("Vendor.One", PackageStatus.Missing, PackageAction.Install));
        var refreshed = Plan(State("Vendor.One", PackageStatus.Current, PackageAction.None, installed: true));
        var store = new FakeProtocolStore(ActionResultStatus.Succeeded);
        var planning = new QueuePlanningCoordinator(refreshed);
        var coordinator = Coordinator(store, planning);

        var result = await coordinator.StartAsync(ManagedRequestAction.Install, current.Packages, current, false, false);

        Assert.AreEqual(ActionResultStatus.Succeeded, result.Result.Status);
        Assert.AreEqual(CompiledActionState.Completed, coordinator.Snapshot.State);
        Assert.AreEqual(1, planning.Calls);
        Assert.AreEqual("Vendor.One", store.Request!.PackageIds.Single());
        Assert.IsTrue(store.Session!.Disposed);
        Assert.IsFalse(store.Session.Killed);
    }

    [TestMethod]
    public async Task ConflictingSecondActionIsRejectedWhileFirstIsRunning()
    {
        var current = Plan(State("Vendor.One", PackageStatus.Missing, PackageAction.Install));
        var store = new FakeProtocolStore(null);
        var coordinator = Coordinator(store, new QueuePlanningCoordinator(current));
        var running = coordinator.StartAsync(ManagedRequestAction.Install, current.Packages, current, false, false);
        await WaitForStateAsync(coordinator, CompiledActionState.Running);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync(
            ManagedRequestAction.Install, current.Packages, current, false, false));
        await coordinator.RequestCancellationAsync();
        Assert.AreEqual(ActionResultStatus.Cancelled, (await running).Result.Status);
    }

    [TestMethod]
    public async Task CancellationIntentRemainsPendingUntilCancelledResultArrives()
    {
        var current = Plan(State("Vendor.One", PackageStatus.Missing, PackageAction.Install));
        var store = new FakeProtocolStore(null, delayCancellationResult: true);
        var coordinator = Coordinator(store, new QueuePlanningCoordinator(current));
        var running = coordinator.StartAsync(ManagedRequestAction.Install, current.Packages, current, false, false);
        await WaitForStateAsync(coordinator, CompiledActionState.Running);

        Assert.IsTrue(await coordinator.RequestCancellationAsync());
        Assert.AreEqual(CompiledActionState.CancellationRequested, coordinator.Snapshot.State);
        store.CompleteCancellation();
        Assert.AreEqual(ActionResultStatus.Cancelled, (await running).Result.Status);
        Assert.AreEqual(CompiledActionState.Cancelled, coordinator.Snapshot.State);
    }

    [TestMethod]
    public async Task FailedResultRemainsFailureAndDoesNotBecomeSuccess()
    {
        var current = Plan(State("Vendor.One", PackageStatus.Missing, PackageAction.Install));
        var coordinator = Coordinator(new FakeProtocolStore(ActionResultStatus.Failed), new QueuePlanningCoordinator(current));
        var result = await coordinator.StartAsync(ManagedRequestAction.Install, current.Packages, current, false, false);
        Assert.AreEqual(ActionResultStatus.Failed, result.Result.Status);
        Assert.AreEqual(CompiledActionState.Failed, coordinator.Snapshot.State);
    }

    [TestMethod]
    public async Task ViewModelKeepsNormalModeReadOnlyAndRejectsExternalAuthority()
    {
        var external = State("Vendor.External", PackageStatus.Manual, PackageAction.Manual,
            provider: ProviderKind.External, authority: CatalogAuthority.OperationalExternal);
        using var viewModel = new MainWindowViewModel(new QueuePlanningCoordinator(Plan(external)));
        await viewModel.RefreshAsync();
        Assert.IsFalse(viewModel.MigrationActionMode);
        Assert.IsFalse(viewModel.Packages.Single().SelectionEnabled);
        Assert.IsFalse(viewModel.InstallCommand.CanExecute(null));
    }

    [TestMethod]
    public void OfficialUriUsesOnlyValidatedIntentAndDiagnosticsRedactSecrets()
    {
        var package = State("Vendor.External", PackageStatus.Manual, PackageAction.Manual,
            provider: ProviderKind.External, authority: CatalogAuthority.OperationalExternal,
            productUri: "https://vendor.example/tool");
        var catalog = new PackageCatalog([package.Package]);
        var detail = new CatalogDetailService().Create(package, catalog);
        var handoff = new CapturingHandoff();
        var viewModel = new CatalogDetailViewModel(detail, handoff);
        viewModel.ProductIntentCommand.Execute(null);
        Assert.AreEqual("vendor.example", handoff.Intent!.Uri.DnsSafeHost);

        var snapshot = DiagnosticsSnapshotFor("password=cleartext");
        var export = new CapturingExport();
        var diagnostics = new DiagnosticsViewModel(snapshot, "token: topsecret", export);
        diagnostics.ExportCommand.Execute(null);
        StringAssert.Contains(export.Text, "password=[REDACTED]");
        StringAssert.Contains(export.Text, "token: [REDACTED]");
        Assert.DoesNotContain("cleartext", export.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("topsecret", export.Text, StringComparison.Ordinal);
    }

    [TestMethod]
    public void DiagnosticsExportIsContainedAndSanitized()
    {
        var root = Path.Combine(Path.GetTempPath(), $"awt-diagnostics-{Guid.NewGuid():N}");
        try
        {
            var export = new DiagnosticsExportService(root, new FixedTimeProvider());
            var path = export.Export("password=cleartext\r\ntoken: topsecret");
            Assert.IsTrue(path.StartsWith(Path.Combine(root, "logs", "diagnostics") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("cleartext", text, StringComparison.Ordinal);
            Assert.DoesNotContain("topsecret", text, StringComparison.Ordinal);
            StringAssert.Contains(text, "[REDACTED]");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void UnverifiedVendorPayloadCannotBeRevealed()
    {
        var package = DirectDeliveryPackage();
        var authorization = VendorDeliveryAuthorization.ForHttps(package, "1.2", new Uri("https://vendor.example/tool.exe"));
        var handoff = new WindowsValidatedUserHandoffService(new VendorCachePathPolicy(), new RejectingVerifier());
        Assert.Throws<InvalidOperationException>(() => handoff.RevealVerifiedPayload(
            authorization, new(VendorPayloadState.Downloaded, "C:\\untrusted\\tool.exe", "Downloaded only"), "C:\\fixture"));
    }

    [TestMethod]
    public async Task UserVisibleActivityRedactsProviderSecrets()
    {
        using var viewModel = new MainWindowViewModel(new FailingPlanningCoordinator());
        await viewModel.RefreshAsync();
        StringAssert.Contains(viewModel.ActivityText, "password=[REDACTED]");
        Assert.DoesNotContain("cleartext", viewModel.ActivityText, StringComparison.Ordinal);
    }

    private static CompiledActionCoordinator Coordinator(FakeProtocolStore store, IWorkstationPlanningCoordinator planning) =>
        new(store, new FakeLauncher(store), planning,
            new ActionRequestFactory(new FixedTimeProvider(), () => "a1b2c3d4"),
            pollInterval: TimeSpan.FromMilliseconds(1), resultTimeout: TimeSpan.FromSeconds(5));

    private static async Task WaitForStateAsync(CompiledActionCoordinator coordinator, CompiledActionState expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && coordinator.Snapshot.State != expected) await Task.Delay(1);
        Assert.AreEqual(expected, coordinator.Snapshot.State);
    }

    private static WorkstationPlan Plan(params PackageState[] packages) => new(
        packages,
        new(packages.Length, packages.Count(item => item.Status == PackageStatus.Current), packages.Count(item => item.Status == PackageStatus.Missing),
            0, 0, packages.Count(item => item.CanSelect), 0, 0, 0, 0, 0, 0, 0, 0),
        RebootState.Clear,
        new(ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, []));

    private static PackageState State(
        string id,
        PackageStatus status,
        PackageAction action,
        bool installed = false,
        ProviderKind provider = ProviderKind.WinGet,
        CatalogAuthority authority = CatalogAuthority.ManagedWinGet,
        string productUri = "")
    {
        var definition = new PackageDefinition(
            id, id, "Vendor", string.Empty, "Fixture", provider, authority, PackageProfile.Standard, PackagePriority.P2,
            PackageRisk.None, authority == CatalogAuthority.ManagedWinGet ? DeploymentPolicy.Allowlisted : DeploymentPolicy.ManualHold,
            authority == CatalogAuthority.ManagedWinGet ? MaintenancePolicy.Allowlisted : MaintenancePolicy.Hold,
            authority == CatalogAuthority.ManagedWinGet ? DeploymentClass.Managed : DeploymentClass.ManualHandoff,
            authority == CatalogAuthority.ManagedWinGet ? CatalogMaintenancePolicy.Managed : CatalogMaintenancePolicy.Manual,
            VersionRule.Latest, VersionCouplingMode.Independent, string.Empty, Lifecycle.Current, [], [], [], [LicensingModel.Free],
            ["PUBLIC-DL"], DistributionPolicy.PackageManagerOnly, [InstallationForm.WinGet], [SupportedOperatingSystem.Windows],
            authority == CatalogAuthority.ManagedWinGet ? DeliveryMode.None : DeliveryMode.VendorPage, ReleaseMode.None,
            authority == CatalogAuthority.ManagedWinGet ? DetectionMode.WinGet : DetectionMode.None, DetectionVersionPolicy.None,
            "Stable", string.Empty, string.Empty, string.Empty, [], null, null, null, null, null, false, false, false, null,
            string.Empty, [], Details: new CatalogMetadataDetails(
                string.Empty, "HARD", ["x64"], "Unknown", string.Empty, productUri, ["Fixture"], "2026-08-30",
                MetadataVerificationState.Current, [], false, string.Empty, "vendor.example", string.Empty,
                "Unknown", "Unknown", "Unknown", string.Empty, string.Empty, string.Empty));
        return new(definition, installed, string.Empty, [], string.Empty, false, status, status.ToString(), status.ToString(), action, InventoryQuality.Complete);
    }

    private static DiagnosticsSnapshot DiagnosticsSnapshotFor(string text) => new(1,
        new("1.1.1", "Migration", "Root", "Logs"),
        new(new(DiagnosticEvidenceState.Available, "Windows"), new(DiagnosticEvidenceState.Available, "5.1"),
            new(DiagnosticEvidenceState.Available, ".NET"), new(DiagnosticEvidenceState.Available, "x64"),
            new(DiagnosticEvidenceState.Available, "Standard"), new(DiagnosticEvidenceState.Available, "WinGet"),
            new(DiagnosticEvidenceState.Available, "1")), DiagnosticEvidenceState.Available, DiagnosticEvidenceState.Available,
        DiagnosticEvidenceState.Available, DiagnosticEvidenceState.Available, [], false, [], new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        [], DiagnosticEvidenceState.Available, DiagnosticsRedactor.Sanitize(text));

    private static PackageDefinition DirectDeliveryPackage() => new(
        "Vendor.Tool", "Vendor Tool", "Vendor", string.Empty, "Fixture", ProviderKind.External, CatalogAuthority.OperationalExternal,
        PackageProfile.Optional, PackagePriority.P2, PackageRisk.None, DeploymentPolicy.ManualHold, MaintenancePolicy.Hold,
        DeploymentClass.ManualHandoff, CatalogMaintenancePolicy.Manual, VersionRule.Latest, VersionCouplingMode.Independent,
        string.Empty, Lifecycle.Current, [], [], [], [LicensingModel.Free], ["PUBLIC-DL"], DistributionPolicy.LinkOnly,
        [InstallationForm.Exe], [SupportedOperatingSystem.Windows], DeliveryMode.DirectDownload, ReleaseMode.InventoryOnly,
        DetectionMode.None, DetectionVersionPolicy.None, "Stable", string.Empty, string.Empty, string.Empty, [], null, null, null,
        null, null, null, null, null, null, string.Empty, [], DeliveryPolicy: new CatalogDeliveryPolicy(
            "https://vendor.example/tool.exe", "^https://vendor\\.example/.+\\.exe$", ["vendor.example"], "Approved Vendor",
            1_048_576, string.Empty, 0, string.Empty, string.Empty, [], string.Empty, string.Empty, string.Empty, string.Empty));

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class QueuePlanningCoordinator(params WorkstationPlan[] plans) : IWorkstationPlanningCoordinator
    {
        private int index;
        public int Calls { get; private set; }
        public Task<WorkstationPlan> RefreshAsync(IProgress<PlanningRefreshStage>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(plans[Math.Min(index++, plans.Length - 1)]);
        }
    }

    private sealed class FailingPlanningCoordinator : IWorkstationPlanningCoordinator
    {
        public Task<WorkstationPlan> RefreshAsync(IProgress<PlanningRefreshStage>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromException<WorkstationPlan>(new InvalidOperationException("password=cleartext"));
    }

    private sealed class FakeProtocolStore(ActionResultStatus? initialResult, bool delayCancellationResult = false) : IActionProtocolStore
    {
        private readonly ActionProgressCodec progressCodec = new();
        private readonly ActionResultCodec resultCodec = new();
        private byte[]? result;
        public ActionRequest? Request { get; private set; }
        public ActionArtifactPaths? Paths { get; private set; }
        public FakeSession? Session { get; set; }

        public Task<ActionArtifactPaths> PersistRequestAsync(AuthorizedActionRequest request, CancellationToken cancellationToken = default)
        {
            Request = request.Request;
            Paths = new(request.Request.RequestId, $"C:\\fixture\\{request.Request.RequestId}.json",
                $"C:\\fixture\\{request.Request.RequestId}.progress.jsonl", $"C:\\fixture\\{request.Request.RequestId}.result.json",
                $"C:\\fixture\\{request.Request.RequestId}.cancel", $"C:\\fixture\\{request.Request.RequestId}.winget.log");
            if (initialResult is not null) Complete(initialResult.Value);
            return Task.FromResult(Paths);
        }

        public Task<bool> CreateCancellationMarkerAsync(string requestId, CancellationToken cancellationToken = default)
        {
            if (!delayCancellationResult) Complete(ActionResultStatus.Cancelled);
            return Task.FromResult(true);
        }

        public Task<byte[]?> TryReadArtifactAsync(string requestId, ActionArtifactKind kind, CancellationToken cancellationToken = default)
        {
            byte[]? value = kind switch
            {
                ActionArtifactKind.Progress when Request is not null => Progress(Request),
                ActionArtifactKind.Result => result,
                _ => null
            };
            return Task.FromResult(value);
        }

        public void CompleteCancellation() => Complete(ActionResultStatus.Cancelled);

        private void Complete(ActionResultStatus status)
        {
            if (Request is null || Paths is null) return;
            var packages = status switch
            {
                ActionResultStatus.Succeeded => new[]
                {
                    new ActionPackageOutcome(Request.PackageIds[0], Request.PackageIds[0], Request.Action, PackageOutcomeStatus.Succeeded,
                        0, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Arguments(Request))
                },
                ActionResultStatus.Failed => new[]
                {
                    new ActionPackageOutcome(Request.PackageIds[0], Request.PackageIds[0], Request.Action, PackageOutcomeStatus.Failed,
                        23, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Arguments(Request))
                },
                _ => []
            };
            var exit = status switch { ActionResultStatus.Succeeded => 0, ActionResultStatus.Cancelled => 2, _ => 1 };
            var final = new ActionFinalResult(1, Request.RequestId, DateTimeOffset.UtcNow, "Fixture", status, status.ToString(), exit,
                Paths.RequestPath, Paths.ProgressPath, Paths.WinGetLogPath, packages);
            result = resultCodec.Serialize(final, Request, Paths);
        }

        private byte[] Progress(ActionRequest request)
        {
            var record = new ActionProgressRecord(DateTimeOffset.UtcNow, ActionProgressLevel.Info, "Starting", request.PackageIds[0], "Fake worker started.");
            return [.. progressCodec.Serialize(record, request), (byte)'\n'];
        }

        private static IReadOnlyList<string> Arguments(ActionRequest request) =>
            [request.Action == ManagedRequestAction.Install ? "install" : "upgrade", "--id", request.PackageIds[0], "--exact", "--source", "winget", "--accept-package-agreements", "--accept-source-agreements"];
    }

    private sealed class FakeLauncher(FakeProtocolStore store) : ICompiledWorkerLauncher
    {
        public ValueTask<ICompiledWorkerSession> LaunchAsync(ActionArtifactPaths paths, CancellationToken cancellationToken = default)
        {
            store.Session = new FakeSession();
            return ValueTask.FromResult<ICompiledWorkerSession>(store.Session);
        }
    }

    private sealed class FakeSession : ICompiledWorkerSession
    {
        public int ProcessId => 42;
        public bool HasExited => false;
        public int? ExitCode => null;
        public bool Disposed { get; private set; }
        public bool Killed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class CapturingHandoff : IValidatedUserHandoffService
    {
        public OpenOfficialUriIntent? Intent { get; private set; }
        public void OpenOfficialUri(OpenOfficialUriIntent intent) => Intent = intent;
        public void RevealVerifiedPayload(VendorDeliveryAuthorization authorization, VendorDownloadResult payload, string explicitDataRoot) =>
            throw new NotSupportedException();
        public void OpenLogs(string explicitDataRoot) => throw new NotSupportedException();
    }

    private sealed class CapturingExport : IDiagnosticsExportService
    {
        public string Text { get; private set; } = string.Empty;
        public string Export(string sanitizedDiagnostics) { Text = sanitizedDiagnostics; return "C:\\fixture\\diagnostics.txt"; }
    }

    private sealed class RejectingVerifier : IVendorPayloadVerifier
    {
        public VendorDownloadResult VerifyAndPromote(VendorDeliveryAuthorization authorization, string explicitDataRoot, string temporaryPath) =>
            new(VendorPayloadState.Rejected, string.Empty, "Rejected");
        public VendorDownloadResult ResolveCached(VendorDeliveryAuthorization authorization, string explicitDataRoot, string payloadPath) =>
            new(VendorPayloadState.Rejected, string.Empty, "Rejected");
    }
}
