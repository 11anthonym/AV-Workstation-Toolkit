using Microsoft.Win32;
using AVWorkstationToolkit.App.ViewModels;

namespace AVWorkstationToolkit.App;

public partial class CatalogUpdateWindow : System.Windows.Window
{
    private readonly CatalogUpdateViewModel viewModel;

    public CatalogUpdateWindow(CatalogUpdateViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        viewModel.ImportRequested += ImportRequested;
        Closed += (_, _) => viewModel.ImportRequested -= ImportRequested;
    }

    private async void ImportRequested()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import signed AV Workstation Toolkit reference catalog",
            Filter = "AVWT reference catalogs (*.avwtcatalog)|*.avwtcatalog",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) await viewModel.ImportAsync(dialog.FileName).ConfigureAwait(true);
    }

    internal void VerifySmokeContract()
    {
        if (CheckNowButton.Command != viewModel.CheckNowCommand || UpdateCatalogButton.Command != viewModel.InstallCommand ||
            ImportButton.Command != viewModel.ImportCommand || RestoreButton.Command != viewModel.RestoreCommand || !CheckNowButton.Focusable ||
            !UpdateCatalogButton.Focusable || !ImportButton.Focusable || !RestoreButton.Focusable)
            throw new InvalidOperationException("Reference catalog update controls are not bound or keyboard accessible.");
    }
}
