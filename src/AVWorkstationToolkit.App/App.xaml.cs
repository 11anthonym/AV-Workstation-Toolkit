using System.Security.Principal;
using System.Windows;
using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.App.ViewModels;

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
            if (!smoke && IsElevated())
            {
                MessageBox.Show(
                    "For safety, AV Workstation Toolkit must be launched as a standard user. Close this copy and start it normally; individual installers can request elevation through Windows.",
                    "Standard-user launch required", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(2);
                return;
            }

            var coordinator = smoke
                ? SmokePlanningCoordinator.Create()
                : CompiledAppComposition.Create(RepositoryRootLocator.Find());
            var viewModel = new MainWindowViewModel(coordinator);
            var window = new MainWindow(viewModel, autoRefresh: !smoke && !readOnlyCheck);
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
}
