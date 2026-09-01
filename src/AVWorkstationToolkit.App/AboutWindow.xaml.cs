using System.Windows;

namespace AVWorkstationToolkit.App;

public partial class AboutWindow : Window
{
    private readonly string version;
    private readonly string executionMode;

    public AboutWindow(string version, string executionMode)
    {
        this.version = string.IsNullOrWhiteSpace(version) ? "Unknown" : version;
        this.executionMode = string.IsNullOrWhiteSpace(executionMode) ? "Compiled runtime" : executionMode;
        InitializeComponent();
        IdentityText.Text = $"Version {this.version}  |  {this.executionMode}";
    }

    internal void VerifySmokeContract()
    {
        if (Icon is null || BrandMark.Source is null || Title != "About AV Workstation Toolkit" || ProductTitle.Text != "AV Workstation Toolkit" ||
            !IdentityText.Text.Contains(version, StringComparison.Ordinal) ||
            !IdentityText.Text.Contains(executionMode, StringComparison.Ordinal) || DescriptionText.Text.Length == 0)
            throw new InvalidOperationException("The compiled About surface is incomplete or has inconsistent identity metadata.");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
