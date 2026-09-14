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

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DiagnosticsViewModel viewModel) return;
        try
        {
            Clipboard.SetText(viewModel.Text);
            viewModel.Status = "Sanitized diagnostics copied to the clipboard.";
        }
        catch (Exception exception)
        {
            viewModel.Status = $"Copy failed: {AVWorkstationToolkit.Application.Diagnostics.DiagnosticsRedactor.Sanitize(exception.Message)}";
        }
    }

    internal void VerifySmokeContract()
    {
        if (Icon is null || DataContext is not DiagnosticsViewModel viewModel || viewModel.Snapshot.Catalog.Total != 3 ||
            !DiagnosticsText.Text.Contains("[Catalog]", StringComparison.Ordinal) ||
            !DiagnosticsText.Text.Contains("[Warnings and errors]", StringComparison.Ordinal) ||
            viewModel.HasIssues && (!IssuesPanel.IsVisible || DiagnosticsIssues.Items.Count != viewModel.Issues.Count ||
                string.IsNullOrWhiteSpace(IssuesSummary.Text)) ||
            !viewModel.HasIssues && IssuesPanel.IsVisible)
            throw new InvalidOperationException("Compiled diagnostics surface did not bind the deterministic sanitized snapshot.");
    }
}
