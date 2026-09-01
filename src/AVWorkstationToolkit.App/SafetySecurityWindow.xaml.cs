using System.Windows;

namespace AVWorkstationToolkit.App;

public partial class SafetySecurityWindow : Window
{
    public SafetySecurityWindow() => InitializeComponent();

    internal void VerifySmokeContract()
    {
        var body = string.Concat(SafetyBody.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text));
        if (Icon is null || Title != "Safety & Security - AV Workstation Toolkit" || SafetyHeading.Text != "Safety & Security" ||
            !body.Contains("independent compiled worker", StringComparison.Ordinal) ||
            !body.Contains("does not execute", StringComparison.Ordinal))
            throw new InvalidOperationException("The compiled Safety & Security surface is incomplete or stale.");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
