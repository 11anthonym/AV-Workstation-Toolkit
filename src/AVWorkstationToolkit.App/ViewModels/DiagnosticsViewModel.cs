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
        Issues = snapshot.Issues.Select(issue => CreateIssuePresentation(issue, snapshot)).ToArray();
        this.exportService = exportService;
        ExportCommand = new RelayCommand(_ => Export(), _ => exportService is not null);
    }

    public DiagnosticsSnapshot Snapshot { get; }
    public string ActionContext { get; }
    public string Text => ActionContext.Length == 0 ? Snapshot.Text : $"{Snapshot.Text}{Environment.NewLine}{Environment.NewLine}{ActionContext}";
    public string State => Snapshot.OverallState switch
    {
        DiagnosticEvidenceState.Available => "All checks completed",
        DiagnosticEvidenceState.Warning => "Some checks need attention",
        DiagnosticEvidenceState.Failed => "Some checks failed",
        DiagnosticEvidenceState.Partial => "Some checks are incomplete",
        DiagnosticEvidenceState.Unavailable => "Checks unavailable",
        _ => "Check status unknown"
    };
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
        Status = $"Diagnostics saved to {DiagnosticsRedactor.SanitizePath(exportService.Export(Text))}";
    }

    private static DiagnosticIssuePresentation CreateIssuePresentation(DiagnosticIssue issue, DiagnosticsSnapshot snapshot)
    {
        var source = DiagnosticsRedactor.Sanitize(issue.Source);
        var detail = string.IsNullOrWhiteSpace(issue.Detail)
            ? "No additional technical detail was provided."
            : DiagnosticsRedactor.Sanitize(issue.Detail);
        var (title, explanation, action) = (issue.Code, source) switch
        {
            (DiagnosticIssueCode.InventoryUnavailable, "WinGet updates") or
            (DiagnosticIssueCode.SourceWarning, "WinGet updates") => (
                "Couldn't check for updates",
                snapshot.WinGetInventoryState == DiagnosticEvidenceState.Available
                    ? "Installed versions are still shown, but update availability is unknown."
                    : "Update availability is unknown. Installation information may also be incomplete.",
                "Select Check again. If this continues, copy or export diagnostics when reporting the problem."),
            (DiagnosticIssueCode.InventoryUnavailable, "WinGet inventory") or
            (DiagnosticIssueCode.SourceWarning, "WinGet inventory") => (
                "Couldn't check installed apps",
                "AVWT couldn't confirm which managed apps are installed. Install and update actions stay unavailable for uncertain items.",
                "Select Check again. If this continues, copy or export diagnostics when reporting the problem."),
            (DiagnosticIssueCode.SourceWarning, "External inventory") or
            (DiagnosticIssueCode.InventoryUnavailable, "External inventory") or
            (DiagnosticIssueCode.PartialRegistrySourceFailure, _) => (
                "Some installed apps couldn't be checked",
                "AVWT couldn't read one or more Windows app lists. Affected app statuses remain incomplete.",
                "Select Check again. If the warning continues, export diagnostics and review the source named below."),
            (DiagnosticIssueCode.PendingReboot, _) => (
                "Restart recommended",
                "Windows is waiting for a restart to finish an update. System-level changes stay paused until you restart.",
                "Save your work and restart Windows when practical, then select Check again."),
            (DiagnosticIssueCode.QuarantinedMetadata, _) => (
                "Some software catalog information is restricted",
                "Some catalog information didn't pass its verification checks. Restricted information can't add installation or download permissions.",
                "No action is required on this PC. Include diagnostics when reporting a catalog-data issue."),
            (DiagnosticIssueCode.StaleVerification, _) => (
                "Some software catalog information needs review",
                "Some catalog records have reached their review date. This doesn't change what AVWT can install or download.",
                "No action is required on this PC. Include diagnostics when reporting outdated catalog information."),
            (DiagnosticIssueCode.ProviderUnavailable, "WinGet") => (
                "WinGet isn't available",
                "AVWT couldn't find a trusted copy of WinGet, so it can't check or manage supported apps.",
                "Repair or update Windows App Installer, then select Check again."),
            (DiagnosticIssueCode.InventoryUnavailable, "Reboot detection") or
            (DiagnosticIssueCode.SourceWarning, "Reboot detection") => (
                "Couldn't check restart status",
                "AVWT couldn't determine whether Windows is waiting for a restart.",
                "Select Check again. System-level changes remain restricted until the check succeeds."),
            _ => (
                $"{FriendlySource(source)} needs attention",
                "A system check didn't finish. Information that was already confirmed remains available; uncertain items stay clearly marked.",
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
