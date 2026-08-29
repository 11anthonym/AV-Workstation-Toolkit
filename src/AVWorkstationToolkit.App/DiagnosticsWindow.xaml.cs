using System.Windows;
using AVWorkstationToolkit.App.ViewModels;

namespace AVWorkstationToolkit.App;

public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow(DiagnosticsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    internal void VerifySmokeContract()
    {
        if (DataContext is not DiagnosticsViewModel viewModel || viewModel.Snapshot.Catalog.Total != 3 ||
            !DiagnosticsText.Text.Contains("[Catalog]", StringComparison.Ordinal) ||
            !DiagnosticsText.Text.Contains("[Warnings and errors]", StringComparison.Ordinal))
            throw new InvalidOperationException("Compiled diagnostics surface did not bind the deterministic sanitized snapshot.");
    }
}
