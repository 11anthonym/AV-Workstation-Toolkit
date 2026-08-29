using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using AVWorkstationToolkit.App.ViewModels;

namespace AVWorkstationToolkit.App;

public partial class MainWindow : Window
{
    private readonly bool autoRefresh;
    private readonly bool allowDialogs;

    public MainWindow(MainWindowViewModel viewModel, bool autoRefresh = true, bool allowDialogs = true)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        this.autoRefresh = autoRefresh;
        this.allowDialogs = allowDialogs;
        viewModel.DetailRequested += ShowDetail;
        viewModel.DiagnosticsRequested += ShowDiagnostics;
        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            viewModel.DetailRequested -= ShowDetail;
            viewModel.DiagnosticsRequested -= ShowDiagnostics;
            viewModel.Dispose();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (autoRefresh && DataContext is MainWindowViewModel viewModel)
            await viewModel.RefreshAsync().ConfigureAwait(true);
    }

    private void PackageGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || string.IsNullOrWhiteSpace(e.Column.SortMemberPath)) return;
        e.Handled = true;
        viewModel.SetSort(e.Column.SortMemberPath);
        foreach (var column in PackageGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = viewModel.SortDirection;
    }

    private void PackageGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = PackageGrid.ActualWidth >= 920;
        ScrollViewer.SetHorizontalScrollBarVisibility(PackageGrid, wide ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        PackageGrid.Columns[1].Width = wide ? new DataGridLength(1.5, DataGridLengthUnitType.Star) : new DataGridLength(190);
        PackageGrid.Columns[2].Width = wide ? new DataGridLength(0.9, DataGridLengthUnitType.Star) : new DataGridLength(95);
        PackageGrid.Columns[7].Width = wide ? new DataGridLength(2, DataGridLengthUnitType.Star) : new DataGridLength(220);
    }

    internal void VerifySmokeContract()
    {
        var required = new[]
        {
            "TopMenu", "RebootBanner", "SearchBox", "StandardFilter", "CatalogPresetFilter", "ManufacturerFilter",
            "DisciplineFilter", "RoleFilter", "AllAppsButton", "SelectMissingButton", "SelectUpdatesButton", "PackageGrid",
            "ActivityLog", "SelectionSummary", "InstallButton", "UpdateButton", "RefreshButton"
        };
        foreach (var name in required)
        {
            if (FindName(name) is null) throw new InvalidOperationException($"Compiled WPF smoke could not find required control '{name}'.");
        }
        if (DataContext is not MainWindowViewModel viewModel || viewModel.VisiblePackages.Count != 3)
            throw new InvalidOperationException("Compiled WPF smoke did not bind the deterministic plan.");
        Measure(new Size(1280, 860));
        Arrange(new Rect(0, 0, 1280, 860));
        UpdateLayout();
        if (PackageGrid.ActualWidth <= 0 || PackageGrid.ActualHeight <= 0 || !PackageGrid.IsVisible)
            throw new InvalidOperationException("Compiled WPF smoke did not produce a visible package grid.");
        if (viewModel.VisiblePackages.Any(item => item.Package.Authority == AVWorkstationToolkit.Domain.Catalog.CatalogAuthority.AwarenessOnly && item.SelectionEnabled))
            throw new InvalidOperationException("Compiled WPF smoke exposed awareness selection authority.");

        var selectable = viewModel.VisiblePackages.FirstOrDefault(item => item.SelectionEnabled)
            ?? throw new InvalidOperationException("Compiled WPF smoke did not expose an eligible selection fixture.");
        PackageGrid.ScrollIntoView(selectable);
        UpdateLayout();
        var row = PackageGrid.ItemContainerGenerator.ContainerFromItem(selectable) as DataGridRow
            ?? throw new InvalidOperationException("Compiled WPF smoke could not realize an eligible package row.");
        var checkBox = FindVisualChild<CheckBox>(row)
            ?? throw new InvalidOperationException("Compiled WPF smoke could not find the package selection control.");
        var peer = new CheckBoxAutomationPeer(checkBox);
        if (peer.GetPattern(PatternInterface.Toggle) is not IToggleProvider toggle)
            throw new InvalidOperationException("Compiled WPF package selection does not expose the expected toggle behavior.");
        toggle.Toggle();
        if (!selectable.Selected)
            throw new InvalidOperationException("Compiled WPF package selection did not update the ViewModel.");
        toggle.Toggle();
        if (selectable.Selected)
            throw new InvalidOperationException("Compiled WPF package deselection did not update the ViewModel.");

        var awareness = viewModel.VisiblePackages.First(item => item.Package.Authority == AVWorkstationToolkit.Domain.Catalog.CatalogAuthority.AwarenessOnly);
        viewModel.SelectedRow = awareness;
        viewModel.DetailsCommand.Execute(null);
        var detail = viewModel.SelectedDetail ?? throw new InvalidOperationException("Compiled WPF smoke did not prepare selected application details.");
        var detailWindow = new CatalogDetailWindow(detail) { Owner = this };
        detailWindow.Show();
        detailWindow.UpdateLayout();
        detailWindow.VerifySmokeContract(awareness.Id);
        detail.ProductIntentCommand.Execute(null);
        if (!detail.IntentStatus.StartsWith("READ-ONLY", StringComparison.Ordinal))
            throw new InvalidOperationException("Compiled official URI intent did not remain read-only.");
        detailWindow.Close();

        viewModel.DiagnosticsCommand.Execute(null);
        var diagnostics = viewModel.Diagnostics ?? throw new InvalidOperationException("Compiled WPF smoke did not prepare diagnostics.");
        var diagnosticsWindow = new DiagnosticsWindow(diagnostics) { Owner = this };
        diagnosticsWindow.Show();
        diagnosticsWindow.UpdateLayout();
        diagnosticsWindow.VerifySmokeContract();
        diagnosticsWindow.Close();
        if (!viewModel.WarningVisible || !RebootBanner.IsVisible)
            throw new InvalidOperationException("Compiled WPF smoke did not present the deterministic reboot/provider warning.");

        toggle.Toggle();
        viewModel.InstallCommand.Execute(null);
        if (viewModel.MutationRefusalCount != 1)
            throw new InvalidOperationException("Compiled WPF smoke did not preserve the explicit mutation refusal.");
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private void ShowDetail(CatalogDetailViewModel viewModel)
    {
        if (!allowDialogs) return;
        new CatalogDetailWindow(viewModel) { Owner = this }.ShowDialog();
    }

    private void ShowDiagnostics(DiagnosticsViewModel viewModel)
    {
        if (!allowDialogs) return;
        new DiagnosticsWindow(viewModel) { Owner = this }.ShowDialog();
    }
}
