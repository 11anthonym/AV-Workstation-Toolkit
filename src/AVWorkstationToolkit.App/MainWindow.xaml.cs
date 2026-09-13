using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.App.ViewModels;
using AVWorkstationToolkit.Application.Compatibility;

namespace AVWorkstationToolkit.App;

public partial class MainWindow : Window
{
    private readonly bool autoRefresh;
    private readonly bool allowDialogs;
    private readonly string productVersion;
    private readonly string executionMode;
    private double pausedActivityOffset;
    private bool restoringActivityOffset;

    public MainWindow(MainWindowViewModel viewModel, string productVersion = "Unknown", string executionMode = "Compiled runtime", bool autoRefresh = true, bool allowDialogs = true)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        this.autoRefresh = autoRefresh;
        this.allowDialogs = allowDialogs;
        this.productVersion = productVersion;
        this.executionMode = executionMode;
        viewModel.DetailRequested += ShowDetail;
        viewModel.CompatibilityDetailRequested += ShowCompatibilityDetail;
        viewModel.DiagnosticsRequested += ShowDiagnostics;
        viewModel.SafetySecurityRequested += ShowSafetySecurity;
        viewModel.CatalogUpdatesRequested += ShowCatalogUpdates;
        viewModel.AboutRequested += ShowAbout;
        SourceInitialized += (_, _) => WindowWorkAreaPlacement.TryFitToCurrentMonitor(this);
        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            viewModel.DetailRequested -= ShowDetail;
            viewModel.CompatibilityDetailRequested -= ShowCompatibilityDetail;
            viewModel.DiagnosticsRequested -= ShowDiagnostics;
            viewModel.SafetySecurityRequested -= ShowSafetySecurity;
            viewModel.CatalogUpdatesRequested -= ShowCatalogUpdates;
            viewModel.AboutRequested -= ShowAbout;
            viewModel.Dispose();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        WindowWorkAreaPlacement.TryFitToCurrentMonitor(this);
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

    internal async Task VerifySmokeContractAsync()
    {
        var required = new[]
        {
            "BrandMark", "TopMenu", "RebootBanner", "FindSoftwareAndDevicesHeading", "FindSoftwareAndDevicesHint", "SearchBox", "SearchStatus", "CompatibilityMatchesPanel", "CompatibilityResultsScroll", "CompatibilitySearchResults", "CompatibilitySearchOutcome", "StandardFilter", "CatalogPresetFilter", "PriorityFilter", "ManufacturerFilter",
            "DisciplineFilter", "RoleFilter", "AllAppsButton", "SelectMissingButton", "SelectUpdatesButton", "PackageGrid",
            "ActivityLog", "FollowActivityCheckBox", "SelectionSummary", "RiskAcknowledgementCheckBox", "GetPackageButton", "InstallButton", "UpdateButton", "RefreshButton",
            "ExportPlanMenuItem", "OpenLogsMenuItem", "RefreshPlanMenuItem", "SafetySecurityMenuItem", "CatalogUpdatesMenuItem", "AboutMenuItem"
        };
        foreach (var name in required)
        {
            if (FindName(name) is null) throw new InvalidOperationException($"Compiled WPF smoke could not find required control '{name}'.");
        }
        VerifyBrandingContract();
        if (DataContext is not MainWindowViewModel viewModel || viewModel.VisiblePackages.Count != 3)
            throw new InvalidOperationException("Compiled WPF smoke did not bind the deterministic plan.");
        Measure(new Size(1280, 860));
        Arrange(new Rect(0, 0, 1280, 860));
        UpdateLayout();
        if (!WindowWorkAreaPlacement.IsTitleBarWithinCurrentWorkArea(this))
            throw new InvalidOperationException("Compiled WPF smoke found the main window title bar outside the current monitor work area.");
        if (PackageGrid.ActualWidth <= 0 || PackageGrid.ActualHeight <= 0 || !PackageGrid.IsVisible)
            throw new InvalidOperationException("Compiled WPF smoke did not produce a visible package grid.");
        if (viewModel.VisiblePackages.Any(item => item.Package.Authority == AVWorkstationToolkit.Domain.Catalog.CatalogAuthority.AwarenessOnly && item.SelectionEnabled))
            throw new InvalidOperationException("Compiled WPF smoke exposed awareness selection authority.");
        VerifyClosedComboBoxLabels();
        VerifyF5Binding(viewModel, invoke: true);
        if (FindSoftwareAndDevicesHeading.Text != "FIND SOFTWARE & DEVICES" ||
            !FindSoftwareAndDevicesHint.Text.Contains("device model", StringComparison.OrdinalIgnoreCase) ||
            !System.Windows.Automation.AutomationProperties.GetHelpText(SearchBox).Contains("device model", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Compiled WPF smoke did not expose the software and device search guidance.");

        viewModel.SearchText = "CP4N";
        await viewModel.SearchCompletion.ConfigureAwait(true);
        UpdateLayout();
        if (!CompatibilityMatchesPanel.IsVisible || CompatibilitySearchResults.Items.Count == 0 ||
            viewModel.CompatibilityMatches.Any(item => item.CanSelect))
            throw new InvalidOperationException("Compiled WPF smoke did not render read-only device matches through Find Apps.");
        viewModel.SearchText = "RLNK-910R";
        await viewModel.SearchCompletion.ConfigureAwait(true);
        var unresolvedHardware = viewModel.CompatibilityMatches.SingleOrDefault(item => item.Kind == CompatibilitySearchResultKind.Device)
            ?? throw new InvalidOperationException("Compiled WPF smoke did not surface the known RackLink hardware identity.");
        if (!unresolvedHardware.Subtitle.Contains("not yet verified", StringComparison.OrdinalIgnoreCase) || unresolvedHardware.CanSelect)
            throw new InvalidOperationException("Compiled WPF smoke did not preserve explicit unresolved hardware coverage.");
        viewModel.SearchText = string.Empty;
        await viewModel.SearchCompletion.ConfigureAwait(true);
        UpdateLayout();

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

        new SafetySecurityWindow().VerifySmokeContract();
        new AboutWindow(productVersion, executionMode).VerifySmokeContract();

        toggle.Toggle();
        viewModel.InstallCommand.Execute(null);
        if (viewModel.MutationRefusalCount != 1)
            throw new InvalidOperationException("Compiled WPF smoke did not preserve the explicit mutation refusal.");
        VerifyActivityFollowContract();
    }

    internal async Task VerifyProductionSmokeContractAsync()
    {
        var required = new[]
        {
            "BrandMark", "TopMenu", "SidebarScroll", "FindSoftwareAndDevicesHeading", "FindSoftwareAndDevicesHint", "SearchBox", "SearchStatus", "CompatibilityMatchesPanel", "CompatibilityResultsScroll", "CompatibilitySearchResults", "CompatibilitySearchOutcome", "CatalogPresetFilter", "PriorityFilter", "ManufacturerFilter", "DisciplineFilter",
            "RoleFilter", "AllAppsButton", "SelectMissingButton", "SelectUpdatesButton", "PackageGrid", "ActivityLog", "FollowActivityCheckBox",
            "DetailsButton", "DiagnosticsButton", "RiskAcknowledgementCheckBox", "GetPackageButton", "InstallButton", "UpdateButton", "RefreshButton",
            "ExportPlanMenuItem", "OpenLogsMenuItem", "RefreshPlanMenuItem", "SafetySecurityMenuItem", "CatalogUpdatesMenuItem", "AboutMenuItem"
        };
        foreach (var name in required)
        {
            if (FindName(name) is null) throw new InvalidOperationException($"Compiled production smoke could not find required control '{name}'.");
        }
        if (!WindowWorkAreaPlacement.IsTitleBarWithinCurrentWorkArea(this))
            throw new InvalidOperationException("Compiled production smoke found the main window title bar outside the current monitor work area.");
        VerifyBrandingContract();
        if (DataContext is not MainWindowViewModel viewModel || viewModel.Packages.Count < 300 || !viewModel.MigrationActionMode)
            throw new InvalidOperationException("Compiled production smoke did not load the complete actionable production composition.");
        VerifyClosedComboBoxLabels();
        VerifyF5Binding(viewModel, invoke: false);
        if (FindSoftwareAndDevicesHeading.Text != "FIND SOFTWARE & DEVICES" ||
            !FindSoftwareAndDevicesHint.Text.Contains("device model", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Compiled production smoke did not expose the software and device search guidance.");
        if (!ExportPlanMenuItem.IsEnabled || !OpenLogsMenuItem.IsEnabled || !SafetySecurityMenuItem.IsEnabled ||
            !CatalogUpdatesMenuItem.IsEnabled || !AboutMenuItem.IsEnabled)
            throw new InvalidOperationException("Compiled production smoke found a required application menu command disabled.");
        new SafetySecurityWindow().VerifySmokeContract();
        new AboutWindow(productVersion, executionMode).VerifySmokeContract();

        viewModel.SearchText = "Crestron";
        await viewModel.SearchCompletion.ConfigureAwait(true);
        if (viewModel.VisiblePackages.Count == 0)
            throw new InvalidOperationException("Compiled production smoke filtering produced no matching catalog rows.");
        viewModel.SearchText = "CP4N";
        await viewModel.SearchCompletion.ConfigureAwait(true);
        var deviceMatch = viewModel.CompatibilityMatches.SingleOrDefault(item => item.Kind == CompatibilitySearchResultKind.Device)
            ?? throw new InvalidOperationException("Compiled production smoke did not surface CP4N from the Find Apps device search.");
        if (deviceMatch.CanSelect || !CompatibilityMatchesPanel.IsVisible)
            throw new InvalidOperationException("Compiled production smoke exposed device compatibility as an action selection or hid its result.");
        deviceMatch.OpenCommand.Execute(null);
        for (var attempt = 0; attempt < 20 && viewModel.SelectedCompatibilityDetail is null; attempt++)
            await Task.Delay(10).ConfigureAwait(true);
        var compatibilityDetail = viewModel.SelectedCompatibilityDetail
            ?? throw new InvalidOperationException("Compiled production smoke did not open CP4N compatibility details.");
        if (!compatibilityDetail.Groups.SelectMany(group => group.Fields).Any(field => field.Label.Contains("SIMPL", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Compiled production smoke did not display CP4N's reviewed software relationships.");
        if (!compatibilityDetail.Groups.Any(group => group.Name == "Hardware identity"))
            throw new InvalidOperationException("Compiled production smoke did not display CP4N's descriptive hardware identity.");
        var compatibilityWindow = new CatalogDetailWindow(compatibilityDetail) { Owner = this };
        compatibilityWindow.Show();
        compatibilityWindow.UpdateLayout();
        compatibilityWindow.VerifyCompatibilitySmokeContract("Crestron.4Series");
        compatibilityDetail.RelatedSoftware.First(item => item.ProductName == "Crestron SIMPL Windows").OpenCommand.Execute(null);
        for (var attempt = 0; attempt < 20 && compatibilityWindow.DataContext == compatibilityDetail; attempt++)
            await Task.Delay(10).ConfigureAwait(true);
        compatibilityWindow.VerifyCompatibilitySmokeContract("Crestron.SIMPLWindows");
        compatibilityWindow.Close();
        viewModel.SearchText = string.Empty;
        await viewModel.SearchCompletion.ConfigureAwait(true);
        viewModel.QuickViewCommand.Execute("Missing");
        if (!viewModel.IsMissingQuickView)
            throw new InvalidOperationException("Compiled production smoke could not activate the Missing quick view.");
        viewModel.QuickViewCommand.Execute("All");
        viewModel.SetSort("VendorSortKey", ListSortDirection.Descending);
        var vendorOrder = viewModel.VisiblePackages.Select(item => item.VendorSortKey).ToArray();
        if (viewModel.SortDirection != ListSortDirection.Descending ||
            !vendorOrder.SequenceEqual(vendorOrder.OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Compiled production smoke could not apply the reviewed sort state.");
        viewModel.SetSort("ApplicationSortKey", ListSortDirection.Ascending);

        var selectable = viewModel.VisiblePackages.FirstOrDefault(item => item.SelectionEnabled)
            ?? throw new InvalidOperationException("Compiled production smoke found no safely selectable managed package.");
        PackageGrid.ScrollIntoView(selectable);
        UpdateLayout();
        var row = PackageGrid.ItemContainerGenerator.ContainerFromItem(selectable) as DataGridRow
            ?? throw new InvalidOperationException("Compiled production smoke could not realize an eligible package row.");
        var checkBox = FindVisualChild<CheckBox>(row)
            ?? throw new InvalidOperationException("Compiled production smoke could not find the package selection control.");
        if (new CheckBoxAutomationPeer(checkBox).GetPattern(PatternInterface.Toggle) is not IToggleProvider toggle)
            throw new InvalidOperationException("Compiled production selection does not expose keyboard/automation toggle behavior.");
        toggle.Toggle();
        if (!selectable.Selected || (!viewModel.CanInstall && !viewModel.CanUpdate))
            throw new InvalidOperationException("Compiled production selection did not update the authoritative action state.");
        toggle.Toggle();
        if (selectable.Selected)
            throw new InvalidOperationException("Compiled production deselection did not update the authoritative action state.");

        viewModel.SelectedRow = viewModel.VisiblePackages[0];
        viewModel.DetailsCommand.Execute(null);
        var detail = viewModel.SelectedDetail ?? throw new InvalidOperationException("Compiled production smoke did not prepare package details.");
        var detailWindow = new CatalogDetailWindow(detail) { Owner = this };
        detailWindow.Show();
        detailWindow.UpdateLayout();
        detailWindow.VerifySmokeContract(viewModel.SelectedRow.Id);
        detailWindow.Close();

        viewModel.DiagnosticsCommand.Execute(null);
        var diagnostics = viewModel.Diagnostics ?? throw new InvalidOperationException("Compiled production smoke did not prepare diagnostics.");
        var diagnosticsWindow = new DiagnosticsWindow(diagnostics) { Owner = this };
        diagnosticsWindow.Show();
        diagnosticsWindow.UpdateLayout();
        diagnosticsWindow.VerifySmokeContract();
        diagnosticsWindow.Close();

        if (!SearchBox.Focusable || !PackageGrid.Focusable || !SearchBox.Focus())
            throw new InvalidOperationException("Compiled production smoke could not place keyboard focus on the search field.");
        if (!SearchBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)) || Keyboard.FocusedElement is null)
            throw new InvalidOperationException("Compiled production smoke could not traverse keyboard focus from the search field.");

        foreach (var viewport in new[] { new Size(1040, 760), new Size(1280, 860), new Size(1440, 900), new Size(1920, 1080) })
        {
            Measure(viewport);
            Arrange(new Rect(new Point(), viewport));
            UpdateLayout();
            if (!PackageGrid.IsVisible || PackageGrid.ActualWidth <= 0 || PackageGrid.ActualHeight <= 0 ||
                SidebarScroll.ActualWidth <= 0 || SidebarScroll.ActualHeight <= 0)
                throw new InvalidOperationException($"Compiled production smoke did not render its common {viewport.Width}x{viewport.Height} layout.");
        }
        VerifyActivityFollowContract();
    }

    private void ActivityLog_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (FollowActivityCheckBox?.IsChecked == true)
        {
            ActivityLog.ScrollToEnd();
            return;
        }

        restoringActivityOffset = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            ActivityLog.ScrollToVerticalOffset(pausedActivityOffset);
            restoringActivityOffset = false;
        });
    }

    private void FollowActivityCheckBox_Checked(object sender, RoutedEventArgs e) => ActivityLog?.ScrollToEnd();

    private void FollowActivityCheckBox_Unchecked(object sender, RoutedEventArgs e) => pausedActivityOffset = ActivityLog?.VerticalOffset ?? 0;

    private void ActivityLog_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (FollowActivityCheckBox?.IsChecked == false && !restoringActivityOffset)
            pausedActivityOffset = e.VerticalOffset;
    }

    private void VerifyActivityFollowContract()
    {
        if (FollowActivityCheckBox.IsChecked != true)
            throw new InvalidOperationException("Activity follow must be enabled by default.");

        ActivityLog.Text = string.Join(Environment.NewLine, Enumerable.Range(1, 80).Select(index => $"Activity line {index}"));
        UpdateLayout();
        ActivityLog.ScrollToHome();
        FollowActivityCheckBox.IsChecked = false;
        var pausedOffset = ActivityLog.VerticalOffset;
        ActivityLog.AppendText($"{Environment.NewLine}Paused activity");
        UpdateLayout();
        Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        if (Math.Abs(ActivityLog.VerticalOffset - pausedOffset) > 0.5)
            throw new InvalidOperationException("Activity view moved while follow-latest was paused.");

        FollowActivityCheckBox.IsChecked = true;
        UpdateLayout();
        var maximumOffset = Math.Max(0, ActivityLog.ExtentHeight - ActivityLog.ViewportHeight);
        if (maximumOffset > 0 && ActivityLog.VerticalOffset < maximumOffset - 1)
            throw new InvalidOperationException("Activity view did not follow the newest entry when enabled.");
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

    private void VerifyClosedComboBoxLabels()
    {
        foreach (var comboBox in new[] { CatalogPresetFilter, PriorityFilter, ManufacturerFilter, DisciplineFilter, RoleFilter })
        {
            comboBox.IsDropDownOpen = false;
            comboBox.ApplyTemplate();
            comboBox.UpdateLayout();
            var expected = comboBox.SelectedItem?.GetType().GetProperty("Label")?.GetValue(comboBox.SelectedItem)?.ToString();
            var rendered = FindVisualChild<TextBlock>(comboBox)?.Text;
            if (string.IsNullOrWhiteSpace(expected) || !string.Equals(rendered, expected, StringComparison.Ordinal))
                throw new InvalidOperationException($"Closed ComboBox '{comboBox.Name}' rendered '{rendered}' instead of its FilterOption label '{expected}'.");
        }
    }

    private void VerifyBrandingContract()
    {
        if (Icon is null || BrandMark.Source is null || BrandMark.ActualWidth <= 0 || BrandMark.ActualHeight <= 0)
            throw new InvalidOperationException("The compiled WPF window did not load its application/taskbar icon and visible brand mark.");
    }

    private void VerifyF5Binding(MainWindowViewModel viewModel, bool invoke)
    {
        var binding = InputBindings.OfType<KeyBinding>().SingleOrDefault(item => item.Gesture is KeyGesture { Key: Key.F5 });
        if (binding?.Command != viewModel.RefreshCommand)
            throw new InvalidOperationException("F5 is not bound to the compiled refresh command.");
        if (!invoke) return;
        var before = viewModel.RefreshInvocationCount;
        binding.Command.Execute(binding.CommandParameter);
        if (viewModel.RefreshInvocationCount != before + 1)
            throw new InvalidOperationException("F5 did not invoke the compiled refresh command.");
    }

    private void ShowDetail(CatalogDetailViewModel viewModel)
    {
        if (!allowDialogs) return;
        new CatalogDetailWindow(viewModel) { Owner = this }.ShowDialog();
    }

    private void ShowCompatibilityDetail(CompatibilityDetailViewModel viewModel)
    {
        if (!allowDialogs) return;
        new CatalogDetailWindow(viewModel) { Owner = this }.ShowDialog();
    }

    private void ShowDiagnostics(DiagnosticsViewModel viewModel)
    {
        if (!allowDialogs) return;
        new DiagnosticsWindow(viewModel) { Owner = this }.ShowDialog();
    }

    private void ShowSafetySecurity()
    {
        if (!allowDialogs) return;
        new SafetySecurityWindow { Owner = this }.ShowDialog();
    }

    private void ShowCatalogUpdates(IReferenceCatalogUpdateService service)
    {
        if (!allowDialogs) return;
        new CatalogUpdateWindow(new CatalogUpdateViewModel(service)) { Owner = this }.ShowDialog();
    }

    private void ShowAbout()
    {
        if (!allowDialogs) return;
        new AboutWindow(productVersion, executionMode) { Owner = this }.ShowDialog();
    }
}
