using System.Windows;

namespace AVWorkstationToolkit.App;

public partial class SafetySecurityWindow : Window
{
    public SafetySecurityWindow() => InitializeComponent();

    internal void VerifySmokeContract()
    {
        var body = string.Concat(SafetyBody.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text));
        if (Icon is null || Title != "Safety & Security - AV Workstation Toolkit" || SafetyHeading.Text != "Safety & Security" ||
            !body.Contains("Only supported apps you select", StringComparison.Ordinal) ||
            !body.Contains("doesn't run vendor installers", StringComparison.Ordinal))
            throw new InvalidOperationException("The compiled Safety & Security surface is incomplete or stale.");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
