using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Application.Diagnostics;

public enum DiagnosticEvidenceState { Available, Unavailable, Failed, Partial, Warning, Unknown }
public enum DiagnosticSeverity { Information, Warning, Error }
public enum DiagnosticIssueCode
{
    InventoryUnavailable,
    PartialRegistrySourceFailure,
    VendorMetadataUnavailable,
    StaleVerification,
    QuarantinedMetadata,
    PendingReboot,
    SourceWarning,
    MalformedVersion,
    ProviderUnavailable
}

public sealed record DiagnosticValue(DiagnosticEvidenceState State, string Value, string Detail = "");
public sealed record DiagnosticIssue(DiagnosticIssueCode Code, DiagnosticSeverity Severity, string Source, string Detail);
public sealed record RuntimeDiagnosticFacts(
    DiagnosticValue WindowsVersion,
    DiagnosticValue WindowsPowerShellVersion,
    DiagnosticValue CompiledRuntimeVersion,
    DiagnosticValue ProcessArchitecture,
    DiagnosticValue Privilege,
    DiagnosticValue WinGetPath,
    DiagnosticValue WinGetVersion);

public sealed record ApplicationDiagnosticContext(
    string ProductVersion,
    string ExecutionMode,
    string DataRoot,
    string LogsPath);

public sealed record DiagnosticSourceSnapshot(
    string Name,
    string Label,
    DiagnosticEvidenceState State,
    int EntryCount,
    string Detail);

public sealed record CatalogDiagnosticCounts(
    int Total,
    int WinGetManaged,
    int OperationalExternal,
    int Awareness,
    int Current,
    int Missing,
    int Updates,
    int Manual,
    int InventoryWarnings,
    int Errors);

public sealed record DiagnosticsSnapshot(
    int SchemaVersion,
    ApplicationDiagnosticContext Application,
    RuntimeDiagnosticFacts Runtime,
    DiagnosticEvidenceState WinGetInventoryState,
    DiagnosticEvidenceState WinGetUpdateState,
    DiagnosticEvidenceState ExternalInventoryState,
    DiagnosticEvidenceState RebootDetectionState,
    IReadOnlyList<DiagnosticSourceSnapshot> RegistrySources,
    bool RebootPending,
    IReadOnlyList<RebootReason> RebootReasons,
    CatalogDiagnosticCounts Catalog,
    IReadOnlyList<DiagnosticIssue> Issues,
    DiagnosticEvidenceState OverallState,
    string Text);

public interface IRuntimeDiagnosticsProvider
{
    Task<RuntimeDiagnosticFacts> ReadAsync(CancellationToken cancellationToken = default);
}

public interface IReadOnlyDiagnosticsService
{
    Task<DiagnosticsSnapshot> ComposeAsync(WorkstationPlan plan, CancellationToken cancellationToken = default);
}
