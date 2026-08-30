using System.Security.Principal;
using System.Windows;
using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Application.Details;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.App.ViewModels;
using AVWorkstationToolkit.Application.Actions;

namespace AVWorkstationToolkit.App;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var smoke = e.Args.Contains("--smoke-test", StringComparer.Ordinal);
            var readOnlyCheck = e.Args.Contains("--read-only-check", StringComparer.Ordinal);
            var migrationTestRoot = ParseMigrationTestRoot(e.Args);
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
            if (smoke)
            {
                coordinator = SmokePlanningCoordinator.Create();
                diagnostics = SmokePlanningCoordinator.CreateDiagnostics();
            }
            else
            {
                var services = CompiledAppComposition.Create(RepositoryRootLocator.Find(), migrationTestRoot);
                coordinator = services.Planning;
                diagnostics = services.Diagnostics;
                details = services.Details;
                actions = services.Actions;
                diagnosticsExport = services.DiagnosticsExport;
                handoffs = services.Handoffs;
            }
            var viewModel = new MainWindowViewModel(coordinator, diagnostics, details, actions, diagnosticsExport, handoffs);
            var window = new MainWindow(viewModel, autoRefresh: !smoke && !readOnlyCheck, allowDialogs: !smoke && !readOnlyCheck);
            MainWindow = window;
            window.Show();
            if (smoke)
            {
                await viewModel.RefreshAsync().ConfigureAwait(true);
                window.VerifySmokeContract();
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
            if (!e.Args.Contains("--smoke-test", StringComparer.Ordinal))
                MessageBox.Show(exception.Message, "Compiled migration startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
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
}
