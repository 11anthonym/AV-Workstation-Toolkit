using System.Windows;

namespace AVWorkstationToolkit.App;

public partial class AboutWindow : Window
{
    private readonly string version;
    public AboutWindow(string version, string _)
    {
        this.version = string.IsNullOrWhiteSpace(version) ? "Unknown" : version;
        InitializeComponent();
        IdentityText.Text = $"Version {this.version}";
    }

    internal void VerifySmokeContract()
    {
        if (Icon is null || BrandMark.Source is null || Title != "About AV Workstation Toolkit" || ProductTitle.Text != "AV Workstation Toolkit" ||
            !IdentityText.Text.Contains(version, StringComparison.Ordinal) || DescriptionText.Text.Length == 0)
            throw new InvalidOperationException("The compiled About surface is incomplete or has inconsistent identity metadata.");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
