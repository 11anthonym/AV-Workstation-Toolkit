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
        Issues = snapshot.Issues.Select(CreateIssuePresentation).ToArray();
        this.exportService = exportService;
        ExportCommand = new RelayCommand(_ => Export(), _ => exportService is not null);
    }

    public DiagnosticsSnapshot Snapshot { get; }
    public string ActionContext { get; }
    public string Text => ActionContext.Length == 0 ? Snapshot.Text : $"{Snapshot.Text}{Environment.NewLine}{Environment.NewLine}{ActionContext}";
    public string State => Snapshot.OverallState.ToString();
    public IReadOnlyList<DiagnosticIssuePresentation> Issues { get; }
    public bool HasIssues => Issues.Count > 0;
    public int WarningCount => Snapshot.Issues.Count(issue => issue.Severity == DiagnosticSeverity.Warning);
    public int ErrorCount => Snapshot.Issues.Count(issue => issue.Severity == DiagnosticSeverity.Error);
    public string IssueSummary => ErrorCount > 0 && WarningCount > 0
        ? $"{ErrorCount} failed check{Plural(ErrorCount)} and {WarningCount} warning{Plural(WarningCount)}"
        : ErrorCount > 0 ? $"{ErrorCount} failed check{Plural(ErrorCount)}"
        : WarningCount > 0 ? $"{WarningCount} warning{Plural(WarningCount)}" : "No warnings or failed checks";
    public RelayCommand ExportCommand { get; }
    public string Status { get => status; set => SetProperty(ref status, DiagnosticsRedactor.Sanitize(value)); }

    private void Export()
    {
        if (exportService is null) return;
        Status = $"Exported sanitized diagnostics to {DiagnosticsRedactor.SanitizePath(exportService.Export(Text))}";
    }

    private static DiagnosticIssuePresentation CreateIssuePresentation(DiagnosticIssue issue)
    {
        var source = DiagnosticsRedactor.Sanitize(issue.Source);
        var detail = string.IsNullOrWhiteSpace(issue.Detail)
            ? "No additional technical detail was provided."
            : DiagnosticsRedactor.Sanitize(issue.Detail);
        var (title, explanation, action) = (issue.Code, source) switch
        {
            (DiagnosticIssueCode.InventoryUnavailable, "WinGet updates") or
            (DiagnosticIssueCode.SourceWarning, "WinGet updates") => (
                "Update availability check failed",
                "Installed application inventory remains available, but AV Workstation Toolkit cannot verify which managed apps are current or have updates.",
                "Select Check again. If this persists, update or repair App Installer/WinGet, then export diagnostics for support."),
            (DiagnosticIssueCode.InventoryUnavailable, "WinGet inventory") or
            (DiagnosticIssueCode.SourceWarning, "WinGet inventory") => (
                "Installed application inventory check failed",
                "Managed application presence could not be verified. Managed install and update actions remain unavailable for uncertain items.",
                "Select Check again. If this persists, verify App Installer/WinGet and export diagnostics for support."),
            (DiagnosticIssueCode.SourceWarning, "External inventory") or
            (DiagnosticIssueCode.InventoryUnavailable, "External inventory") or
            (DiagnosticIssueCode.PartialRegistrySourceFailure, _) => (
                "Some external application inventory is incomplete",
                "One or more Windows uninstall-inventory sources could not be read. Affected external records remain explicitly incomplete.",
                "Select Check again. If the warning persists, export diagnostics and review the named inventory source."),
            (DiagnosticIssueCode.PendingReboot, _) => (
                "Restart recommended",
                "Windows reports pending work that requires a restart. Existing risk and reboot policies remain enforced.",
                "Save your work and restart Windows when practical, then select Check again."),
            (DiagnosticIssueCode.QuarantinedMetadata, _) => (
                "Some catalog metadata is restricted",
                "One or more catalog records contain metadata that did not meet the current verification policy. Restricted metadata cannot grant operational authority.",
                "No immediate action is required. Include diagnostics when reporting a catalog-data issue."),
            (DiagnosticIssueCode.StaleVerification, _) => (
                "Catalog metadata review is due",
                "Some descriptive catalog records are older than the current review target. Package, download, credential, and worker authority are unchanged.",
                "No immediate workstation action is required. A signed catalog update may refresh this metadata when one is available."),
            (DiagnosticIssueCode.ProviderUnavailable, "WinGet") => (
                "WinGet runtime is unavailable",
                "The trusted WinGet executable could not be resolved, so managed inventory and actions cannot proceed.",
                "Repair or update Windows App Installer, then select Check again."),
            (DiagnosticIssueCode.InventoryUnavailable, "Reboot detection") or
            (DiagnosticIssueCode.SourceWarning, "Reboot detection") => (
                "Restart status could not be checked",
                "AV Workstation Toolkit could not verify whether Windows has a pending restart.",
                "Select Check again. Existing risk and reboot restrictions remain in force."),
            _ => (
                $"{FriendlySource(source)} needs attention",
                "A read-only diagnostic check did not complete normally. Existing verified state remains available and uncertain state stays explicit.",
                "Select Check again. If this persists, export diagnostics for support.")
        };
        return new(issue.Severity == DiagnosticSeverity.Error ? "CHECK FAILED" : "WARNING", title, explanation, action,
            $"{source}: {detail}", issue.Severity == DiagnosticSeverity.Error ? "#FF8D96" : "#F8C555");
    }

    private static string FriendlySource(string source) => string.IsNullOrWhiteSpace(source) ? "A system check" : source;
    private static string Plural(int value) => value == 1 ? string.Empty : "s";
}

public sealed record DiagnosticIssuePresentation(
    string SeverityLabel,
    string Title,
    string Explanation,
    string RecommendedAction,
    string TechnicalDetail,
    string Accent);
