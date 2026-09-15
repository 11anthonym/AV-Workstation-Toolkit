using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;
using AVWorkstationToolkit.App.ViewModels;
using AVWorkstationToolkit.Application.Details;
using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Application.Providers;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class DiagnosticsAndDetailsTests
{
    [TestMethod]
    public void RepositoryCatalogRetainsDetailAndProvenanceMetadata()
    {
        var catalog = LoadCatalog();
        var toolbox = catalog.GetRequired("Crestron.Toolbox");

        Assert.AreEqual("Crestron.MasterInstaller", toolbox.ParentProviderId);
        Assert.StartsWith("https://", toolbox.MetadataDetails.OfficialProductUri, StringComparison.Ordinal);
        Assert.IsNotEmpty(toolbox.MetadataDetails.ValidationMethods);
        Assert.AreEqual("Unknown", toolbox.MetadataDetails.SideBySideSupported);
        Assert.AreNotEqual(MetadataVerificationState.Quarantined, toolbox.MetadataDetails.MetadataVerificationState);
    }

    [TestMethod]
    public void DetailSurfacePreservesUnknownValuesAndValidatedOfficialIntent()
    {
        var catalog = LoadCatalog();
        var package = catalog.GetRequired("QSC.QSYSDesigner.LTS");
        var state = State(package, PackageStatus.Inventory, installed: true, installedVersion: "9.12.0");
        var detail = new CatalogDetailService().Create(state, catalog);

        CollectionAssert.AreEqual(
            new[] { "About this software", "On this PC", "Installation and compatibility", "Access and system requirements", "System impact", "Source and verification details", "Official links" },
            detail.Groups.Select(group => group.Name).ToArray());
        Assert.AreEqual(package.Id, detail.ProductIntent?.PackageId);
        Assert.AreEqual(Uri.UriSchemeHttps, detail.ProductIntent?.Uri.Scheme);
        Assert.IsTrue(detail.Groups.SelectMany(group => group.Fields).All(field => !string.IsNullOrWhiteSpace(field.Value)));
    }

    [TestMethod]
    public void OfficialUriIntentRejectsUntrustedCatalogMetadata()
    {
        var catalog = LoadCatalog();
        var original = catalog.GetRequired("QSC.QSYSDesigner.LTS");
        var unsafePackage = original with
        {
            Details = original.MetadataDetails with { OfficialProductUri = "https://user:secret@example.test/product" }
        };

        Assert.Throws<InvalidDataException>(() => OpenOfficialUriIntent.FromCatalog(unsafePackage, OfficialUriKind.Product));
        Assert.IsNull(OpenOfficialUriIntent.FromCatalog(original with
        {
            Details = original.MetadataDetails with { OfficialDownloadUri = string.Empty }
        }, OfficialUriKind.Download));
    }

    [TestMethod]
    public void ExternalReadModelValidatesParentRelationshipWithoutTransport()
    {
        var catalog = LoadCatalog();
        var toolbox = catalog.GetRequired("Crestron.Toolbox");
        var result = new ExternalProviderReadModelService().Create(
            State(toolbox, PackageStatus.NotDetected, installed: false), catalog);

        Assert.IsTrue(result.ParentRelationshipValid);
        Assert.AreEqual(DiagnosticEvidenceState.Unknown, result.DeliveryEvidence);
        StringAssert.Contains(result.DeliveryDetail, "not checked");
        Assert.AreEqual(DiagnosticEvidenceState.Unknown, result.CacheEvidence);
    }

    [TestMethod]
    public async Task DiagnosticsPreservePartialSourcesAndRedactSecretsAndPaths()
    {
        var catalog = LoadCatalog();
        var package = catalog.GetRequired("Crestron.Toolbox");
        var plan = Plan(
            [State(package, PackageStatus.InventoryIncomplete, installed: true, installedVersion: "3.0", quality: InventoryQuality.Partial)],
            new(ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Partial, ProviderQuality.Complete,
                ["External inventory warning"], ExternalSources:
                [
                    new(RegistryInventorySource.Hklm64, true, 4, "OK"),
                    new(RegistryInventorySource.Hkcu, false, 0, "token=top-secret")
                ], ExternalInventoryDetail: "password=hunter2"));
        var localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var service = new ReadOnlyDiagnosticsService(new FixedRuntimeProvider(),
            new("1.1.1", "Compiled migration", Path.Combine(localRoot, "AVWorkstationToolkit"), Path.Combine(localRoot, "AVWorkstationToolkit", "logs")));

        var snapshot = await service.ComposeAsync(plan);

        Assert.AreEqual(DiagnosticEvidenceState.Partial, snapshot.ExternalInventoryState);
        Assert.AreEqual(DiagnosticEvidenceState.Failed, snapshot.RegistrySources.Single(source => source.Name == "Hkcu").State);
        Assert.AreEqual(DiagnosticEvidenceState.Warning, snapshot.OverallState);
        StringAssert.Contains(snapshot.Text, "%LOCALAPPDATA%");
        Assert.DoesNotContain("top-secret", snapshot.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", snapshot.Text, StringComparison.Ordinal);
        Assert.IsTrue(snapshot.Issues.Any(issue => issue.Code == DiagnosticIssueCode.PartialRegistrySourceFailure));
    }

    [TestMethod]
    public async Task RuntimeFailureIsExplicitAndDoesNotErasePlanCounts()
    {
        var catalog = LoadCatalog();
        var package = catalog.GetRequired("Crestron.Toolbox");
        var service = new ReadOnlyDiagnosticsService(new ThrowingRuntimeProvider(), new("1.1.1", "Compiled migration", "Unknown", "Unknown"));

        var snapshot = await service.ComposeAsync(Plan([State(package, PackageStatus.Inventory, true, "3.0")], CompleteProviders()));

        Assert.AreEqual(DiagnosticEvidenceState.Failed, snapshot.Runtime.WindowsVersion.State);
        Assert.AreEqual(1, snapshot.Catalog.Total);
        Assert.AreEqual(1, snapshot.Catalog.OperationalExternal);
    }

    [TestMethod]
    public async Task DiagnosticsPresentationExplainsFailedCheckImpactAndNextAction()
    {
        var catalog = LoadCatalog();
        var package = catalog.Items.First(item => item.Provider == ProviderKind.WinGet);
        var providers = CompleteProviders() with
        {
            WinGetUpdateQuality = ProviderQuality.Malformed,
            WinGetUpdateFailure = ProviderFailureKind.MalformedOutput,
            WinGetUpdateDetail = "WinGet update output contains a malformed package row.",
            Warnings = ["WinGet update check failed."]
        };
        var service = new ReadOnlyDiagnosticsService(new FixedRuntimeProvider(), new("1.1.1", "Test", "Unknown", "Unknown"));
        var snapshot = await service.ComposeAsync(Plan(
            [State(package, PackageStatus.CheckUnavailable, true, "1.0", InventoryQuality.Unavailable)], providers));

        var viewModel = new DiagnosticsViewModel(snapshot);
        var issue = viewModel.Issues.Single(item => item.Title == "Couldn't check for updates");

        Assert.AreEqual("CHECK FAILED", issue.SeverityLabel);
        StringAssert.Contains(issue.Explanation, "update availability is unknown");
        StringAssert.Contains(issue.RecommendedAction, "Check again");
        StringAssert.Contains(issue.TechnicalDetail, "malformed package row");
        StringAssert.Contains(viewModel.IssueSummary, "failed check");
        Assert.DoesNotContain("InventoryUnavailable", issue.Title, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task DiagnosticTextUsesStableBulletsForInventorySources()
    {
        var catalog = LoadCatalog();
        var package = catalog.GetRequired("Crestron.Toolbox");
        var providers = CompleteProviders() with
        {
            ExternalSources = [new(RegistryInventorySource.Hklm64, true, 1, "Registry source read successfully.")]
        };
        var service = new ReadOnlyDiagnosticsService(new FixedRuntimeProvider(), new("1.1.1", "Test", "Unknown", "Unknown"));
        var snapshot = await service.ComposeAsync(Plan([State(package, PackageStatus.Inventory, true, "3.0")], providers));

        StringAssert.Contains(snapshot.Text, "- HKLM 64-bit uninstall inventory:");
        Assert.DoesNotContain("\n  HKLM", snapshot.Text, StringComparison.Ordinal);
    }

    [TestMethod]
    public void PackagePresentationDoesNotTurnUnknownInstallationEvidenceIntoNotInstalled()
    {
        var catalog = LoadCatalog();
        var package = catalog.GetRequired("Crestron.Toolbox");
        var detectedWithoutVersion = State(package, PackageStatus.Inventory, installed: true, installedVersion: string.Empty);
        var unavailable = State(package, PackageStatus.InventoryUnavailable, installed: false, quality: InventoryQuality.Unavailable);
        var confirmedMissing = State(package, PackageStatus.NotDetected, installed: false);

        Assert.AreEqual("Version unknown", new PackageRowViewModel(detectedWithoutVersion, 0, () => { }).VersionLabel);
        Assert.AreEqual("Couldn't check", new PackageRowViewModel(unavailable, 0, () => { }).VersionLabel);
        Assert.AreEqual("Couldn't check installation", new PackageRowViewModel(unavailable, 0, () => { }).StatusLabel);
        Assert.AreEqual("Not installed", new PackageRowViewModel(confirmedMissing, 0, () => { }).VersionLabel);

        var workstationFields = new CatalogDetailService().Create(unavailable, catalog).Groups
            .Single(group => group.Name == "On this PC").Fields;
        Assert.AreEqual("Couldn't check", workstationFields.Single(field => field.Label == "Installed").Value);
        Assert.AreEqual("Couldn't check", workstationFields.Single(field => field.Label == "Installed version").Value);
    }

    [TestMethod]
    public async Task UpdateWarningDoesNotClaimInstalledInventorySurvivedWhenThatCheckAlsoFailed()
    {
        var catalog = LoadCatalog();
        var package = catalog.Items.First(item => item.Provider == ProviderKind.WinGet);
        var providers = CompleteProviders() with
        {
            WinGetInventoryQuality = ProviderQuality.Unavailable,
            WinGetUpdateQuality = ProviderQuality.Malformed,
            WinGetInventoryDetail = "WinGet installed inventory unavailable.",
            WinGetUpdateDetail = "WinGet update output contains a malformed package row."
        };
        var service = new ReadOnlyDiagnosticsService(new FixedRuntimeProvider(), new("1.1.1", "Test", "Unknown", "Unknown"));
        var snapshot = await service.ComposeAsync(Plan(
            [State(package, PackageStatus.InventoryUnavailable, installed: false, quality: InventoryQuality.Unavailable)], providers));

        var issue = new DiagnosticsViewModel(snapshot).Issues.Single(item => item.Title == "Couldn't check for updates");

        StringAssert.Contains(issue.Explanation, "Installation information may also be incomplete");
        Assert.DoesNotContain("still shown", issue.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("repair", issue.RecommendedAction, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task StaleSoftwareMetadataMessageDoesNotPromiseDeviceCatalogUpdateWillFixIt()
    {
        var catalog = LoadCatalog();
        var package = catalog.Items.First(item => item.MetadataDetails.MetadataVerificationState == MetadataVerificationState.VerificationRequired);
        var service = new ReadOnlyDiagnosticsService(new FixedRuntimeProvider(), new("1.1.1", "Test", "Unknown", "Unknown"));
        var snapshot = await service.ComposeAsync(Plan([State(package, PackageStatus.Awareness, installed: false)], CompleteProviders()));

        var issue = new DiagnosticsViewModel(snapshot).Issues.Single(item => item.Title == "Some software catalog information needs review");

        StringAssert.Contains(issue.Explanation, "doesn't change what AVWT can install or download");
        Assert.DoesNotContain("catalog update", issue.RecommendedAction, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("download", issue.RecommendedAction, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task DetailSelectionIsRetainedByIdentityAndClearedWhenFilteredOut()
    {
        var catalog = LoadCatalog();
        var package = catalog.GetRequired("Crestron.Toolbox");
        var plan = Plan([State(package, PackageStatus.Inventory, true, "3.0")], CompleteProviders());
        using var viewModel = new MainWindowViewModel(new FixedCoordinator(plan),
            new ReadOnlyDiagnosticsService(new FixedRuntimeProvider(), new("1.1.1", "Test", "Unknown", "Unknown")));

        await viewModel.RefreshAsync();
        viewModel.SelectedRow = viewModel.VisiblePackages.Single();
        viewModel.DetailsCommand.Execute(null);
        await viewModel.RefreshAsync();
        Assert.AreEqual(package.Id, viewModel.SelectedRow?.Id);
        viewModel.SearchText = "does-not-match";
        await viewModel.SearchCompletion;
        Assert.IsNull(viewModel.SelectedRow);
        Assert.IsNull(viewModel.SelectedDetail);
    }

    [TestMethod]
    public void DetailIntentCommandNeverLaunchesOrMutates()
    {
        var catalog = LoadCatalog();
        var package = catalog.GetRequired("QSC.QSYSDesigner.LTS");
        var detail = new CatalogDetailService().Create(State(package, PackageStatus.Inventory, true, "9.12"), catalog);
        var viewModel = new CatalogDetailViewModel(detail);

        viewModel.ProductIntentCommand.Execute(null);

        Assert.StartsWith("Preview only", viewModel.IntentStatus, StringComparison.Ordinal);
        StringAssert.Contains(viewModel.IntentStatus, "official link");
        Assert.DoesNotContain(package.Id, viewModel.IntentStatus, StringComparison.Ordinal);
    }

    private static PackageCatalog LoadCatalog() => new RepositoryCatalogLoader().Load(RepositoryRootLocator.Find());

    private static PackageState State(PackageDefinition package, PackageStatus status, bool installed, string installedVersion = "", InventoryQuality quality = InventoryQuality.Complete) =>
        new(package, installed, installedVersion, installed && installedVersion.Length > 0 ? [installedVersion] : [], package.KnownVersion, false,
            status, status.ToString(), status.ToString(), PackageAction.None, quality);

    private static WorkstationPlan Plan(IReadOnlyList<PackageState> states, ProviderRefreshSummary providers)
    {
        int Count(PackageStatus status) => states.Count(item => item.Status == status);
        return new(states, new(states.Count,
                Count(PackageStatus.Current), Count(PackageStatus.Missing), Count(PackageStatus.UpdateAvailable),
                Count(PackageStatus.Manual), Count(PackageStatus.ManualUpdate), Count(PackageStatus.Held),
                Count(PackageStatus.Inventory), Count(PackageStatus.NotDetected), Count(PackageStatus.InventoryIncomplete),
                Count(PackageStatus.InventoryUnavailable), Count(PackageStatus.CheckUnavailable), Count(PackageStatus.Awareness), Count(PackageStatus.Error)),
            RebootState.Clear, providers);
    }

    private static ProviderRefreshSummary CompleteProviders() =>
        new(ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, []);

    private sealed class FixedRuntimeProvider : IRuntimeDiagnosticsProvider
    {
        public Task<RuntimeDiagnosticFacts> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = new DiagnosticValue(DiagnosticEvidenceState.Available, "fixture");
            return Task.FromResult(new RuntimeDiagnosticFacts(available,
                new(DiagnosticEvidenceState.Available, "5.1"), new(DiagnosticEvidenceState.Available, ".NET 10"),
                new(DiagnosticEvidenceState.Available, "X64"), new(DiagnosticEvidenceState.Available, "Standard user"),
                new(DiagnosticEvidenceState.Available, "C:\\Program Files\\WindowsApps\\winget.exe"),
                new(DiagnosticEvidenceState.Available, "1.10")));
        }
    }

    private sealed class ThrowingRuntimeProvider : IRuntimeDiagnosticsProvider
    {
        public Task<RuntimeDiagnosticFacts> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<RuntimeDiagnosticFacts>(new InvalidOperationException("runtime unavailable token=secret"));
    }

    private sealed class FixedCoordinator(WorkstationPlan plan) : IWorkstationPlanningCoordinator
    {
        public Task<WorkstationPlan> RefreshAsync(IProgress<PlanningRefreshStage>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(plan);
    }
}
