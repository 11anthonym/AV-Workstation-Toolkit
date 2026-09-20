using System.Security.Principal;
using System.Windows;
using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Application.Details;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.App.ViewModels;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;
using AVWorkstationToolkit.Application.Compatibility;

namespace AVWorkstationToolkit.App;

public sealed record PackagedAppStartupContext(
    string ApplicationRoot,
    string DataRoot,
    string Version,
    string WorkerSha256,
    bool SmokeTest = false);

public partial class App : System.Windows.Application
{
    private readonly PackagedAppStartupContext? packagedContext;

    public App() { }

    public App(PackagedAppStartupContext packagedContext) =>
        this.packagedContext = packagedContext ?? throw new ArgumentNullException(nameof(packagedContext));

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke = packagedContext?.SmokeTest == true || e.Args.Contains("--smoke-test", StringComparer.Ordinal);
        // Both automated modes are read here so the failure path below can see them. A dialog shown to
        // an unattended harness blocks until its timeout and discards the diagnostic entirely.
        var readOnlyCheck = e.Args.Contains("--read-only-check", StringComparer.Ordinal);
        try
        {
            if (!smoke && IsElevated())
            {
                const string elevationRefusal = "For safety, AV Workstation Toolkit must be launched as a standard user. Close this copy and start it normally; individual installers can request elevation through Windows.";
                // The refusal itself is a prohibition, not a diagnostic, so it keeps its own exit code 2
                // on both paths. Under --read-only-check it must reach standard error: a dialog would
                // hold the unattended harness open until its timeout and report nothing about why.
                if (readOnlyCheck) Console.Error.WriteLine(DiagnosticsRedactor.Sanitize(elevationRefusal));
                else MessageBox.Show(elevationRefusal, "Standard-user launch required", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(2);
                return;
            }

            IWorkstationPlanningCoordinator coordinator;
            IReadOnlyDiagnosticsService diagnostics;
            var details = new CatalogDetailService();
            CompiledActionCoordinator? actions = null;
            IDiagnosticsExportService? diagnosticsExport = null;
            IValidatedUserHandoffService? handoffs = null;
            IPackageDeliveryWorkflow? packageDelivery = null;
            IApplicationMenuWorkflow? applicationMenu = null;
            CompatibilityCatalogQueryService? compatibility = null;
            IReferenceCatalogUpdateService? referenceCatalogUpdates = null;
            var productVersion = packagedContext?.Version ?? "Unknown";
            var executionMode = packagedContext is null ? "Source compiled runtime" : "Packaged compiled runtime";
            if (packagedContext is not null)
            {
                var services = CompiledAppComposition.CreateProduction(
                    packagedContext.ApplicationRoot,
                    packagedContext.DataRoot,
                    packagedContext.Version,
                    packagedContext.WorkerSha256);
                coordinator = services.Planning;
                diagnostics = services.Diagnostics;
                details = services.Details;
                actions = services.Actions;
                diagnosticsExport = services.DiagnosticsExport;
                handoffs = services.Handoffs;
                packageDelivery = services.PackageDelivery;
                applicationMenu = services.ApplicationMenu;
                compatibility = services.Compatibility;
                referenceCatalogUpdates = services.ReferenceCatalogUpdates;
                productVersion = services.Version;
                executionMode = services.ExecutionMode;
            }
            else if (smoke)
            {
                coordinator = SmokePlanningCoordinator.Create();
                diagnostics = SmokePlanningCoordinator.CreateDiagnostics();
                var repositoryRoot = RepositoryRootLocator.Find();
                compatibility = new CompatibilityCatalogQueryService(
                    new RepositoryCompatibilityCatalogLoader().Load(repositoryRoot),
                    new UnresolvedInstalledVersionEvidenceProvider(),
                    new RepositoryHardwareIdentityCatalogLoader().Load(repositoryRoot));
            }
            else
            {
                var repositoryRoot = RepositoryRootLocator.Find();
                var services = CompiledAppComposition.Create(repositoryRoot);
                coordinator = services.Planning;
                diagnostics = services.Diagnostics;
                details = services.Details;
                actions = services.Actions;
                diagnosticsExport = services.DiagnosticsExport;
                handoffs = services.Handoffs;
                packageDelivery = services.PackageDelivery;
                applicationMenu = services.ApplicationMenu;
                compatibility = services.Compatibility;
                referenceCatalogUpdates = services.ReferenceCatalogUpdates;
                productVersion = services.Version;
                executionMode = services.ExecutionMode;
            }
            var viewModel = new MainWindowViewModel(coordinator, diagnostics, details, actions, diagnosticsExport, handoffs, packageDelivery,
                applicationMenu, liveRehearsalMode: false, compatibilityService: compatibility, referenceCatalogUpdates: referenceCatalogUpdates);
            var window = new MainWindow(viewModel, productVersion, executionMode, autoRefresh: !smoke && !readOnlyCheck, allowDialogs: !smoke && !readOnlyCheck);
            MainWindow = window;
            window.Show();
            if (!smoke && !readOnlyCheck && referenceCatalogUpdates is not null)
                _ = CheckCatalogFreshnessAfterStartupAsync(referenceCatalogUpdates);
            if (smoke)
            {
                await viewModel.RefreshAsync().ConfigureAwait(true);
                if (packagedContext is null) await window.VerifySmokeContractAsync().ConfigureAwait(true);
                else await window.VerifyProductionSmokeContractAsync().ConfigureAwait(true);
                window.Close();
                Shutdown(0);
            }
            else if (readOnlyCheck)
            {
                await viewModel.RefreshAsync().ConfigureAwait(true);
                // Catalog breadth proves the catalog loaded; it says nothing about provider health,
                // because a plan keeps every catalog row even when no provider could be read.
                if (viewModel.Packages.Count < 300)
                    throw new InvalidOperationException("Compiled read-only integration did not produce the complete catalog plan.");
                var providers = viewModel.LatestPlan?.Providers
                    ?? throw new InvalidOperationException("Compiled read-only integration produced no plan; the refresh did not complete.");
                var outcomes = ReadOnlyIntegrationContract.Evaluate(providers);
                foreach (var outcome in outcomes) Console.Error.WriteLine(outcome.Report());
                var failed = outcomes.Where(outcome => outcome.Classification == ReadOnlyIntegrationContract.ProviderClassification.Failed).ToArray();
                if (failed.Length > 0)
                    throw new InvalidOperationException(
                        "Compiled read-only integration did not meet the provider contract: " +
                        string.Join("; ", failed.Select(outcome => $"{outcome.Name} reported {outcome.Quality} ({outcome.Failure}) - {outcome.Detail}")));
                window.Close();
                Shutdown(0);
            }
        }
        catch (Exception exception)
        {
            var diagnostic = DiagnosticsRedactor.Sanitize(exception.Message);
            if (smoke || readOnlyCheck)
                Console.Error.WriteLine(diagnostic);
            else
                MessageBox.Show(
                    $"AV Workstation Toolkit couldn't start.\n\n{diagnostic}\n\nTry opening it again. If the problem continues, include this message when reporting it.",
                    "Couldn't start AV Workstation Toolkit", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static async Task CheckCatalogFreshnessAfterStartupAsync(IReferenceCatalogUpdateService service)
    {
        try
        {
            // Local Device Lookup is already constructed and visible before any channel I/O begins.
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            _ = await service.CheckInBackgroundIfDueAsync().ConfigureAwait(false);
        }
        catch
        {
            // Automatic freshness is best-effort. Manual Check now remains available for diagnostics.
        }
    }

}
