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
    }

    internal void VerifySmokeContract()
    {
        if (CheckNowButton.Command != viewModel.CheckNowCommand || UpdateCatalogButton.Command != viewModel.InstallCommand ||
            !CheckNowButton.Focusable || !UpdateCatalogButton.Focusable)
            throw new InvalidOperationException("Reference catalog update controls are not bound or keyboard accessible.");
    }
}
