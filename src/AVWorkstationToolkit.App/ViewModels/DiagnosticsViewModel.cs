using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.App.Commands;

namespace AVWorkstationToolkit.App.ViewModels;

public sealed class DiagnosticsViewModel : ObservableObject
{
    private readonly IDiagnosticsExportService? exportService;
    private string status = string.Empty;

    public DiagnosticsViewModel(DiagnosticsSnapshot snapshot, string actionContext = "", IDiagnosticsExportService? exportService = null)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        ActionContext = DiagnosticsRedactor.Sanitize(actionContext);
        this.exportService = exportService;
        ExportCommand = new RelayCommand(_ => Export(), _ => exportService is not null);
    }

    public DiagnosticsSnapshot Snapshot { get; }
    public string ActionContext { get; }
    public string Text => ActionContext.Length == 0 ? Snapshot.Text : $"{Snapshot.Text}{Environment.NewLine}{Environment.NewLine}{ActionContext}";
    public string State => Snapshot.OverallState.ToString();
    public int WarningCount => Snapshot.Issues.Count(issue => issue.Severity == DiagnosticSeverity.Warning);
    public int ErrorCount => Snapshot.Issues.Count(issue => issue.Severity == DiagnosticSeverity.Error);
    public RelayCommand ExportCommand { get; }
    public string Status { get => status; set => SetProperty(ref status, DiagnosticsRedactor.Sanitize(value)); }

    private void Export()
    {
        if (exportService is null) return;
        Status = $"Exported sanitized diagnostics to {DiagnosticsRedactor.SanitizePath(exportService.Export(Text))}";
    }
}
