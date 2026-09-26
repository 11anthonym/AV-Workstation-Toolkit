using System.Windows;
using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.App.ViewModels;

namespace AVWorkstationToolkit.App;

public partial class MigrationWindow : Window
{
    public MigrationWindow(MigrationViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        viewModel.ProfileEditorRequested += ShowProfileEditor;
        SourceInitialized += (_, _) => WindowWorkAreaPlacement.TryFitToCurrentMonitor(this);
        Closed += (_, _) =>
        {
            viewModel.ProfileEditorRequested -= ShowProfileEditor;
            viewModel.Dispose();
        };
    }

    internal MigrationViewModel ViewModel => (MigrationViewModel)DataContext;

    private void ShowProfileEditor(ProfileEditorViewModel editor) => new ProfileEditorWindow(editor) { Owner = this }.ShowDialog();

    /// <summary>Checks that the window's named controls exist and bind to the view model, for automated verification.</summary>
    internal void VerifySmokeContract()
    {
        var required = new[]
        {
            "ThisPcTitle", "ThisPcSummary", "ScanButton", "ExportInventoryButton", "ImportInventoryButton", "ApplyProfileButton",
            "NewProfileButton", "EditProfileButton", "SaveAsProfileButton", "MigrationStatus", "SourceTitle", "MigrationProgress",
            "RemainingHeadline", "ProgressText", "SummaryText", "RescanButton", "FinishButton", "ChecklistSearch", "ChecklistFilters", "ChecklistGrid",
            "EmptyState", "ReviewSearch", "ReviewSummary", "ReviewGrid", "DetailPanel", "InstallSelectedButton", "ConfirmButton", "ClearConfirmationButton", "ExcludeButton", "IncludeButton", "RemoveButton",
            "TasksPanel", "TaskList", "MigrationRiskAcknowledgement", "InstallAllButton"
        };
        foreach (var name in required)
            if (FindName(name) is null) throw new InvalidOperationException($"The migration window is missing control '{name}'.");
        if (Icon is null || DataContext is not MigrationViewModel viewModel)
            throw new InvalidOperationException("The migration window did not load its icon or view model.");
        if (ChecklistGrid.ItemsSource != viewModel.VisibleItems || TaskList.ItemsSource != viewModel.Tasks || ReviewGrid.ItemsSource != viewModel.ReviewItems)
            throw new InvalidOperationException("The migration window did not bind its checklist.");
    }
}
