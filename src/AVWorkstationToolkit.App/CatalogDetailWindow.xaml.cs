using System.Windows;
using AVWorkstationToolkit.App.ViewModels;

namespace AVWorkstationToolkit.App;

public partial class CatalogDetailWindow : Window
{
    public CatalogDetailWindow(CatalogDetailViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        Title = $"Application details - {viewModel.Name}";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    internal void VerifySmokeContract(string packageId)
    {
        if (DataContext is not CatalogDetailViewModel viewModel || viewModel.Detail.PackageId != packageId)
            throw new InvalidOperationException("Compiled detail surface does not match the selected package.");
        if (DetailGroups.Items.Count < 7 || !ProductIntentButton.IsEnabled)
            throw new InvalidOperationException("Compiled detail surface is missing semantic groups or its validated official product intent.");
        if (DownloadIntentButton.IsEnabled && viewModel.Detail.DownloadIntent is null)
            throw new InvalidOperationException("Compiled detail surface enabled an unavailable official download intent.");
    }
}
