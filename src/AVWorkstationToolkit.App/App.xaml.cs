using System.Security.Principal;
using System.Windows;
using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Application.Details;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.App.ViewModels;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;

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
            var migrationTestRoot = ParseMigrationTestRoot(e.Args);
            var liveRehearsalRoot = ParseLiveRehearsalRoot(e.Args);
            if (migrationTestRoot is not null && liveRehearsalRoot is not null)
                throw new ArgumentException("Fake migration mode and live rehearsal mode cannot be enabled together.");
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
            var liveRehearsal = false;
            if (packagedContext is not null)
            {
                if (migrationTestRoot is not null || liveRehearsalRoot is not null)
                    throw new ArgumentException("Packaged production mode cannot be combined with migration test modes.");
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
            }
            else if (smoke)
            {
                coordinator = SmokePlanningCoordinator.Create();
                diagnostics = SmokePlanningCoordinator.CreateDiagnostics();
            }
            else
            {
                var repositoryRoot = RepositoryRootLocator.Find();
                var services = liveRehearsalRoot is null
                    ? CompiledAppComposition.Create(repositoryRoot, migrationTestRoot)
                    : CompiledAppComposition.CreateLiveRehearsal(repositoryRoot, liveRehearsalRoot);
                coordinator = services.Planning;
                diagnostics = services.Diagnostics;
                details = services.Details;
                actions = services.Actions;
                diagnosticsExport = services.DiagnosticsExport;
                handoffs = services.Handoffs;
                liveRehearsal = services.IsLiveRehearsal;
            }
            var viewModel = new MainWindowViewModel(coordinator, diagnostics, details, actions, diagnosticsExport, handoffs, liveRehearsal);
            var window = new MainWindow(viewModel, autoRefresh: !smoke && !readOnlyCheck, allowDialogs: !smoke && !readOnlyCheck);
            MainWindow = window;
            window.Show();
            if (smoke)
            {
                await viewModel.RefreshAsync().ConfigureAwait(true);
                if (packagedContext is null) window.VerifySmokeContract();
                else window.VerifyProductionSmokeContract();
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

    private static string? ParseMigrationTestRoot(IReadOnlyList<string> args)
    {
        var indexes = args.Select((value, index) => (value, index))
            .Where(item => item.value == "--migration-action-test-root")
            .Select(item => item.index).ToArray();
        if (indexes.Length == 0) return null;
        if (indexes.Length != 1 || indexes[0] + 1 >= args.Count)
            throw new ArgumentException("Migration action mode requires exactly one --migration-action-test-root <isolated-temp-root> argument.");
        return args[indexes[0] + 1];
    }

    private static string? ParseLiveRehearsalRoot(IReadOnlyList<string> args)
    {
        var indexes = args.Select((value, index) => (value, index))
            .Where(item => item.value == "--live-rehearsal-root")
            .Select(item => item.index).ToArray();
        if (indexes.Length == 0) return null;
        if (indexes.Length != 1 || indexes[0] + 1 >= args.Count)
            throw new ArgumentException("Live rehearsal mode requires exactly one --live-rehearsal-root <isolated-temp-root> argument.");
        return args[indexes[0] + 1];
    }
}
