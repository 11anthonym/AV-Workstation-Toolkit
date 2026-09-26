using System.Windows;
using AVWorkstationToolkit.App.ViewModels;

namespace AVWorkstationToolkit.App;

public partial class ProfileEditorWindow : Window
{
    public ProfileEditorWindow(ProfileEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        EventHandler close = (_, _) => Close();
        viewModel.CloseRequested += close;
        Closed += (_, _) => viewModel.CloseRequested -= close;
    }
}
