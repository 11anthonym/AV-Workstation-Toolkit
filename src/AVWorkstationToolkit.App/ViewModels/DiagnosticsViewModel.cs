using AVWorkstationToolkit.Application.Diagnostics;

namespace AVWorkstationToolkit.App.ViewModels;

public sealed class DiagnosticsViewModel(DiagnosticsSnapshot snapshot)
{
    public DiagnosticsSnapshot Snapshot { get; } = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    public string Text => Snapshot.Text;
    public string State => Snapshot.OverallState.ToString();
    public int WarningCount => Snapshot.Issues.Count(issue => issue.Severity == DiagnosticSeverity.Warning);
    public int ErrorCount => Snapshot.Issues.Count(issue => issue.Severity == DiagnosticSeverity.Error);
}
