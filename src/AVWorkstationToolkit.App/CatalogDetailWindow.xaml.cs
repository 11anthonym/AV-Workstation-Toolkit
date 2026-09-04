using System.Windows;
using AVWorkstationToolkit.App.ViewModels;

namespace AVWorkstationToolkit.App;

public partial class CatalogDetailWindow : Window
{
    private CompatibilityDetailViewModel? compatibilityViewModel;

    public CatalogDetailWindow(IReadOnlyDetailViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        Bind(viewModel);
        Closed += (_, _) => UnsubscribeCompatibility();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    internal void VerifySmokeContract(string packageId)
    {
        if (Icon is null || DataContext is not CatalogDetailViewModel viewModel || viewModel.Detail.PackageId != packageId)
            throw new InvalidOperationException("Compiled detail surface does not match the selected package.");
        if (DetailGroups.Items.Count < 7 || OfficialLinkButtons.Items.Count == 0)
            throw new InvalidOperationException("Compiled detail surface is missing semantic groups or its validated official product intent.");
    }

    internal void VerifyCompatibilitySmokeContract(string contextId)
    {
        if (Icon is null || DataContext is not CompatibilityDetailViewModel viewModel || viewModel.ContextId != contextId)
            throw new InvalidOperationException("Compiled compatibility detail surface does not match the selected compatibility record.");
        if (DetailGroups.Items.Count == 0 || OfficialLinkButtons.Items.Count == 0)
            throw new InvalidOperationException("Compiled compatibility detail surface is missing its descriptive groups or evidence links.");
    }

    private void Bind(IReadOnlyDetailViewModel viewModel)
    {
        UnsubscribeCompatibility();
        DataContext = viewModel;
        Title = $"Application details - {viewModel.Name}";
        compatibilityViewModel = viewModel as CompatibilityDetailViewModel;
        if (compatibilityViewModel is not null) compatibilityViewModel.NavigationRequested += NavigateCompatibility;
    }

    private void NavigateCompatibility(CompatibilityDetailViewModel target) => Bind(target);

    private void UnsubscribeCompatibility()
    {
        if (compatibilityViewModel is not null) compatibilityViewModel.NavigationRequested -= NavigateCompatibility;
        compatibilityViewModel = null;
    }
}
