using AVWorkstationToolkit.App.ViewModels;

namespace AVWorkstationToolkit.App;

public partial class ManagedCatalogUpdateWindow : System.Windows.Window
{
    public ManagedCatalogUpdateWindow(ManagedCatalogUpdateViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }
}
