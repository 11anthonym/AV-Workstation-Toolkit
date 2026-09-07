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
        try
        {
            var readOnlyCheck = e.Args.Contains("--read-only-check", StringComparer.Ordinal);
            if (!smoke && IsElevated())
            {
                MessageBox.Show(
                    "For safety, AV Workstation Toolkit must be launched as a standard user. Close this copy and start it normally; individual installers can request elevation through Windows.",
                    "Standard-user launch required", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                productVersion = services.Version;
                executionMode = services.ExecutionMode;
            }
            var viewModel = new MainWindowViewModel(coordinator, diagnostics, details, actions, diagnosticsExport, handoffs, packageDelivery,
                applicationMenu, liveRehearsalMode: false, compatibilityService: compatibility);
            var window = new MainWindow(viewModel, productVersion, executionMode, autoRefresh: !smoke && !readOnlyCheck, allowDialogs: !smoke && !readOnlyCheck);
            MainWindow = window;
            window.Show();
            if (smoke)
            {
                await viewModel.RefreshAsync().ConfigureAwait(true);
                if (packagedContext is null) window.VerifySmokeContract();
                else await window.VerifyProductionSmokeContractAsync().ConfigureAwait(true);
                window.Close();
                Shutdown(0);
            }
            else if (readOnlyCheck)
            {
                await viewModel.RefreshAsync().ConfigureAwait(true);
                if (viewModel.Packages.Count < 300)
                    throw new InvalidOperationException("Compiled read-only integration did not produce the complete catalog plan.");
                window.Close();
                Shutdown(0);
            }
        }
        catch (Exception exception)
        {
            if (!smoke)
                MessageBox.Show(exception.Message, "AV Workstation Toolkit startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                Console.Error.WriteLine(exception.Message);
            Shutdown(1);
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

}
