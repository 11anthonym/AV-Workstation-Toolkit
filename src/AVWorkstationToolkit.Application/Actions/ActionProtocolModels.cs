namespace AVWorkstationToolkit.Application.Actions;

public enum ActionArtifactKind
{
    Request,
    Progress,
    Result,
    Cancellation,
    WinGetLog
}

public enum ActionProgressLevel
{
    Info,
    Success,
    Warning,
    Error
}

public enum ActionResultStatus
{
    Succeeded,
    Failed,
    Rejected,
    Cancelled,
    Blocked
}

public enum PackageOutcomeStatus
{
    Planned,
    Blocked,
    Failed,
    Succeeded,
    Unverified
}

public enum ActionProtocolFailure
{
    MalformedJson,
    Oversized,
    MissingField,
    UnknownField,
    DuplicateField,
    WrongType,
    UnsupportedSchema,
    InvalidValue,
    RequestMismatch,
    PackageMismatch,
    InvalidTransition
}

public sealed class ActionProtocolValidationException : Exception
{
    public ActionProtocolValidationException(ActionProtocolFailure failure, string message, Exception? innerException = null)
        : base(message, innerException) => Failure = failure;

    public ActionProtocolFailure Failure { get; }
}

public sealed record ActionArtifactPaths(
    string RequestId,
    string RequestPath,
    string ProgressPath,
    string ResultPath,
    string CancellationPath,
    string WinGetLogPath);

public sealed record ActionProgressRecord
{
    internal ActionProgressRecord(DateTimeOffset timestamp, ActionProgressLevel level, string stage, string packageId, string message)
    {
        Timestamp = timestamp;
        Level = level;
        Stage = stage;
        PackageId = packageId;
        Message = message;
    }

    public DateTimeOffset Timestamp { get; }
    public ActionProgressLevel Level { get; }
    public string Stage { get; }
    public string PackageId { get; }
    public string Message { get; }
}

public sealed record ActionProgressIssue(long RecordNumber, ActionProtocolFailure Failure, string Detail);

public sealed class ActionProgressParseState
{
    private readonly byte[] pendingBytes;

    public static ActionProgressParseState Empty { get; } = new(0, 0, []);

    public ActionProgressParseState(long bytesConsumed, long completeRecordCount, ReadOnlySpan<byte> pendingBytes)
    {
        if (bytesConsumed < 0) throw new ArgumentOutOfRangeException(nameof(bytesConsumed));
        if (completeRecordCount < 0) throw new ArgumentOutOfRangeException(nameof(completeRecordCount));
        BytesConsumed = bytesConsumed;
        CompleteRecordCount = completeRecordCount;
        this.pendingBytes = pendingBytes.ToArray();
    }

    public long BytesConsumed { get; }
    public long CompleteRecordCount { get; }
    public ReadOnlyMemory<byte> PendingBytes => pendingBytes;
}

public sealed record ActionProgressParseBatch(
    IReadOnlyList<ActionProgressRecord> Records,
    IReadOnlyList<ActionProgressIssue> Issues,
    ActionProgressParseState State);

public sealed record ActionPackageOutcome
{
    internal ActionPackageOutcome(
        string id,
        string name,
        ManagedRequestAction action,
        PackageOutcomeStatus status,
        int exitCode,
        bool verified,
        DateTimeOffset? startedAt,
        DateTimeOffset finishedAt,
        IReadOnlyList<string> arguments)
    {
        Id = id;
        Name = name;
        Action = action;
        Status = status;
        ExitCode = exitCode;
        Verified = verified;
        StartedAt = startedAt;
        FinishedAt = finishedAt;
        Arguments = arguments;
    }

    public string Id { get; }
    public string Name { get; }
    public ManagedRequestAction Action { get; }
    public PackageOutcomeStatus Status { get; }
    public int ExitCode { get; }
    public bool Verified { get; }
    public DateTimeOffset? StartedAt { get; }
    public DateTimeOffset FinishedAt { get; }
    public IReadOnlyList<string> Arguments { get; }
}

public sealed record ActionFinalResult
{
    internal ActionFinalResult(
        int schemaVersion,
        string requestId,
        DateTimeOffset generatedAt,
        string computer,
        ActionResultStatus status,
        string message,
        int exitCode,
        long managedCatalogRevision,
        string requestPath,
        string progressPath,
        string winGetLogPath,
        IReadOnlyList<ActionPackageOutcome> packages)
    {
        SchemaVersion = schemaVersion;
        RequestId = requestId;
        GeneratedAt = generatedAt;
        Computer = computer;
        Status = status;
        Message = message;
        ExitCode = exitCode;
        ManagedCatalogRevision = managedCatalogRevision;
        RequestPath = requestPath;
        ProgressPath = progressPath;
        WinGetLogPath = winGetLogPath;
        Packages = packages;
    }

    public int SchemaVersion { get; }
    public string RequestId { get; }
    public DateTimeOffset GeneratedAt { get; }
    public string Computer { get; }
    public ActionResultStatus Status { get; }
    public string Message { get; }
    public int ExitCode { get; }
    public long ManagedCatalogRevision { get; }
    public string RequestPath { get; }
    public string ProgressPath { get; }
    public string WinGetLogPath { get; }
    public IReadOnlyList<ActionPackageOutcome> Packages { get; }
}

public enum ActionCancellationState
{
    None,
    Requested,
    ObservedBetweenPackages,
    CompletedBeforeObservation,
    ConfirmedCancelled
}

public enum ActionLifecycleState
{
    Prepared,
    Persisted,
    AwaitingWorker,
    Running,
    CancellationRequested,
    CancellationObserved,
    Completed,
    Failed,
    Rejected,
    Blocked,
    Cancelled
}

public sealed class ActionRequestLifecycle
{
    private readonly List<ActionProgressRecord> progress = [];

    public ActionRequestLifecycle(ActionRequest request)
    {
        ActionRequestRules.Validate(request);
        Request = request;
    }

    public ActionRequest Request { get; }
    public ActionLifecycleState State { get; private set; } = ActionLifecycleState.Prepared;
    public ActionCancellationState Cancellation { get; private set; } = ActionCancellationState.None;
    public IReadOnlyList<ActionProgressRecord> Progress => progress.AsReadOnly();
    public ActionFinalResult? FinalResult { get; private set; }

    public void MarkPersisted()
    {
        RequireState(ActionLifecycleState.Prepared);
        State = ActionLifecycleState.Persisted;
    }

    public void MarkAwaitingWorker()
    {
        RequireState(ActionLifecycleState.Persisted);
        State = ActionLifecycleState.AwaitingWorker;
    }

    public void AttachProgress(string requestId, ActionProgressRecord record)
    {
        RequireRequest(requestId);
        ArgumentNullException.ThrowIfNull(record);
        if (IsFinal(State)) throw InvalidTransition("Progress cannot attach after a final result.");
        if (State is not (ActionLifecycleState.AwaitingWorker or ActionLifecycleState.Running or ActionLifecycleState.CancellationRequested or ActionLifecycleState.CancellationObserved))
            throw InvalidTransition("Progress can attach only after the request is awaiting a worker.");
        if (State == ActionLifecycleState.AwaitingWorker) State = ActionLifecycleState.Running;
        progress.Add(record);
    }

    public void RequestCancellation(string requestId)
    {
        RequireRequest(requestId);
        if (IsFinal(State)) throw InvalidTransition("A completed request cannot be cancelled.");
        if (State is not (ActionLifecycleState.Persisted or ActionLifecycleState.AwaitingWorker or ActionLifecycleState.Running or ActionLifecycleState.CancellationRequested))
            throw InvalidTransition("Cancellation can be requested only for a persisted active request.");
        Cancellation = ActionCancellationState.Requested;
        State = ActionLifecycleState.CancellationRequested;
    }

    public void MarkCancellationObserved(string requestId)
    {
        RequireRequest(requestId);
        if (State != ActionLifecycleState.CancellationRequested)
            throw InvalidTransition("Cancellation observation requires a pending cancellation request.");
        Cancellation = ActionCancellationState.ObservedBetweenPackages;
        State = ActionLifecycleState.CancellationObserved;
    }

    public bool AttachFinalResult(ActionFinalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        RequireRequest(result.RequestId);
        if (FinalResult is not null)
        {
            if (ActionProtocolSemantics.FinalResultsEqual(FinalResult, result)) return false;
            throw InvalidTransition("A different final result cannot replace the authoritative result.");
        }
        if (State is not (ActionLifecycleState.AwaitingWorker or ActionLifecycleState.Running or ActionLifecycleState.CancellationRequested or ActionLifecycleState.CancellationObserved))
            throw InvalidTransition("A final result can attach only after the request is awaiting a worker.");

        FinalResult = result;
        State = result.Status switch
        {
            ActionResultStatus.Succeeded => ActionLifecycleState.Completed,
            ActionResultStatus.Failed => ActionLifecycleState.Failed,
            ActionResultStatus.Rejected => ActionLifecycleState.Rejected,
            ActionResultStatus.Blocked => ActionLifecycleState.Blocked,
            ActionResultStatus.Cancelled => ActionLifecycleState.Cancelled,
            _ => throw new InvalidOperationException("Unknown final-result status.")
        };
        if (result.Status == ActionResultStatus.Cancelled)
            Cancellation = ActionCancellationState.ConfirmedCancelled;
        else if (Cancellation is ActionCancellationState.Requested or ActionCancellationState.ObservedBetweenPackages)
            Cancellation = ActionCancellationState.CompletedBeforeObservation;
        return true;
    }

    private void RequireRequest(string requestId)
    {
        if (!string.Equals(Request.RequestId, requestId, StringComparison.Ordinal))
            throw new ActionProtocolValidationException(ActionProtocolFailure.RequestMismatch, "The artifact belongs to another request.");
    }

    private void RequireState(ActionLifecycleState required)
    {
        if (State != required) throw InvalidTransition($"Lifecycle state {State} cannot transition through {required}.");
    }

    private static bool IsFinal(ActionLifecycleState state) => state is
        ActionLifecycleState.Completed or ActionLifecycleState.Failed or ActionLifecycleState.Rejected or
        ActionLifecycleState.Blocked or ActionLifecycleState.Cancelled;

    private static ActionProtocolValidationException InvalidTransition(string message) =>
        new(ActionProtocolFailure.InvalidTransition, message);
}

internal static class ActionProtocolSemantics
{
    public static bool FinalResultsEqual(ActionFinalResult left, ActionFinalResult right)
    {
        if (left.SchemaVersion != right.SchemaVersion || left.RequestId != right.RequestId || left.GeneratedAt != right.GeneratedAt ||
            left.Computer != right.Computer || left.Status != right.Status || left.Message != right.Message || left.ExitCode != right.ExitCode ||
            left.ManagedCatalogRevision != right.ManagedCatalogRevision ||
            !string.Equals(left.RequestPath, right.RequestPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(left.ProgressPath, right.ProgressPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(left.WinGetLogPath, right.WinGetLogPath, StringComparison.OrdinalIgnoreCase) ||
            left.Packages.Count != right.Packages.Count) return false;
        return left.Packages.Zip(right.Packages).All(pair => PackageOutcomesEqual(pair.First, pair.Second));
    }

    private static bool PackageOutcomesEqual(ActionPackageOutcome left, ActionPackageOutcome right) =>
        left.Id == right.Id && left.Name == right.Name && left.Action == right.Action && left.Status == right.Status &&
        left.ExitCode == right.ExitCode && left.Verified == right.Verified && left.StartedAt == right.StartedAt &&
        left.FinishedAt == right.FinishedAt && left.Arguments.SequenceEqual(right.Arguments, StringComparer.Ordinal);
}
