using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;

namespace AVWorkstationToolkit.Application.Actions;

public interface IActionProtocolStore
{
    Task<ActionArtifactPaths> PersistRequestAsync(AuthorizedActionRequest request, CancellationToken cancellationToken = default);
    Task<bool> CreateCancellationMarkerAsync(string requestId, CancellationToken cancellationToken = default);
    Task<byte[]?> TryReadArtifactAsync(string requestId, ActionArtifactKind kind, CancellationToken cancellationToken = default);
}

public interface ICompiledWorkerSession : IDisposable
{
    int ProcessId { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
}

public interface ICompiledWorkerLauncher
{
    ValueTask<ICompiledWorkerSession> LaunchAsync(ActionArtifactPaths paths, CancellationToken cancellationToken = default);
}

public enum CompiledActionState
{
    Idle,
    Preparing,
    Running,
    CancellationRequested,
    Completed,
    Failed,
    Cancelled
}

public sealed class CompiledActionSnapshot(
    CompiledActionState state,
    string requestId,
    string status,
    IReadOnlyList<ActionProgressRecord> progress,
    ActionFinalResult? result) : EventArgs
{
    public CompiledActionState State { get; } = state;
    public string RequestId { get; } = requestId;
    public string Status { get; } = status;
    public IReadOnlyList<ActionProgressRecord> Progress { get; } = progress;
    public ActionFinalResult? Result { get; } = result;
}

public sealed record CompiledActionRunResult(ActionFinalResult Result, WorkstationPlan RefreshedPlan);

/// <summary>
/// Compiled UI action coordinator. Authority comes from the current typed plan
/// and the independent worker. The coordinator only persists
/// an authorized request, starts the fixed migration worker, observes correlated
/// artifacts, records cancellation intent, and refreshes read-only state.
/// </summary>
public sealed class CompiledActionCoordinator
{
    private readonly IActionProtocolStore store;
    private readonly ICompiledWorkerLauncher launcher;
    private readonly IWorkstationPlanningCoordinator planning;
    private readonly ActionRequestFactory requestFactory;
    private readonly ActionRequestAuthorizationService authorization;
    private readonly ActionProgressCodec progressCodec;
    private readonly ActionResultCodec resultCodec;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan resultTimeout;
    private readonly object gate = new();
    private ActionRequestLifecycle? lifecycle;
    private ActionArtifactPaths? paths;
    private bool active;

    public CompiledActionCoordinator(
        IActionProtocolStore store,
        ICompiledWorkerLauncher launcher,
        IWorkstationPlanningCoordinator planning,
        ActionRequestFactory? requestFactory = null,
        ActionRequestAuthorizationService? authorization = null,
        ActionProgressCodec? progressCodec = null,
        ActionResultCodec? resultCodec = null,
        TimeProvider? timeProvider = null,
        TimeSpan? pollInterval = null,
        TimeSpan? resultTimeout = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        this.planning = planning ?? throw new ArgumentNullException(nameof(planning));
        this.requestFactory = requestFactory ?? new ActionRequestFactory();
        this.authorization = authorization ?? new ActionRequestAuthorizationService();
        this.progressCodec = progressCodec ?? new ActionProgressCodec();
        this.resultCodec = resultCodec ?? new ActionResultCodec();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        this.resultTimeout = resultTimeout ?? TimeSpan.FromMinutes(30);
        if (this.pollInterval <= TimeSpan.Zero || this.resultTimeout <= this.pollInterval)
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "The action observation bounds are invalid.");
    }

    public event EventHandler<CompiledActionSnapshot>? StateChanged;

    public CompiledActionSnapshot Snapshot { get; private set; } =
        new(CompiledActionState.Idle, string.Empty, "Managed action worker is idle.", [], null);

    public async Task<CompiledActionRunResult> StartAsync(
        ManagedRequestAction action,
        IReadOnlyCollection<PackageState> selectedPackages,
        WorkstationPlan currentPlan,
        bool riskAcknowledged,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedPackages);
        ArgumentNullException.ThrowIfNull(currentPlan);
        lock (gate)
        {
            if (active) throw new InvalidOperationException("Another managed action is already active.");
            active = true;
        }

        ICompiledWorkerSession? session = null;
        try
        {
            SetSnapshot(CompiledActionState.Preparing, string.Empty, "Validating selected managed packages.", [], null);
            var expected = action == ManagedRequestAction.Install ? PackageAction.Install : PackageAction.Update;
            var ids = selectedPackages.Where(item => item.Action == expected).Select(item => item.Package.Id).ToArray();
            if (ids.Length != selectedPackages.Count)
                throw new ActionRequestValidationException(ActionRequestFailure.ActionMismatch, "Every selected package must match the requested action.");
            var request = requestFactory.Create(action, ids, riskAcknowledged, dryRun);
            var authorized = authorization.Authorize(request, currentPlan);
            lifecycle = new ActionRequestLifecycle(request);
            paths = await store.PersistRequestAsync(authorized, cancellationToken).ConfigureAwait(false);
            lifecycle.MarkPersisted();
            lifecycle.MarkAwaitingWorker();
            session = await launcher.LaunchAsync(paths, cancellationToken).ConfigureAwait(false);
            SetSnapshot(CompiledActionState.Running, request.RequestId, "The isolated compiled worker is running.", [], null);

            var result = await ObserveAsync(request, session, cancellationToken).ConfigureAwait(false);
            var refreshedPlan = await planning.RefreshAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var finalState = result.Status switch
            {
                ActionResultStatus.Succeeded => CompiledActionState.Completed,
                ActionResultStatus.Cancelled => CompiledActionState.Cancelled,
                _ => CompiledActionState.Failed
            };
            SetSnapshot(finalState, request.RequestId, result.Message, lifecycle.Progress, result);
            return new(result, refreshedPlan);
        }
        catch (OperationCanceledException)
        {
            SetSnapshot(CompiledActionState.Failed, lifecycle?.Request.RequestId ?? string.Empty,
                "Action observation stopped; the independent worker was not terminated.", Snapshot.Progress, Snapshot.Result);
            throw;
        }
        catch (Exception exception)
        {
            SetSnapshot(CompiledActionState.Failed, lifecycle?.Request.RequestId ?? string.Empty,
                Diagnostics.DiagnosticsRedactor.Sanitize(exception.Message), Snapshot.Progress, Snapshot.Result);
            throw;
        }
        finally
        {
            session?.Dispose();
            lock (gate) active = false;
        }
    }

    public async Task<bool> RequestCancellationAsync(CancellationToken cancellationToken = default)
    {
        ActionRequestLifecycle current;
        lock (gate)
        {
            if (!active || lifecycle is null || paths is null) return false;
            current = lifecycle;
        }
        var created = await store.CreateCancellationMarkerAsync(current.Request.RequestId, cancellationToken).ConfigureAwait(false);
        if (current.State is ActionLifecycleState.Persisted or ActionLifecycleState.AwaitingWorker or ActionLifecycleState.Running)
            current.RequestCancellation(current.Request.RequestId);
        SetSnapshot(CompiledActionState.CancellationRequested, current.Request.RequestId,
            "Cancellation requested; the worker will stop between packages.", Snapshot.Progress, Snapshot.Result);
        return created;
    }

    private async Task<ActionFinalResult> ObserveAsync(
        ActionRequest request,
        ICompiledWorkerSession session,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetUtcNow();
        var parseState = ActionProgressParseState.Empty;
        var observedRecords = new List<ActionProgressRecord>();
        while (timeProvider.GetUtcNow() - started < resultTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progress = await store.TryReadArtifactAsync(request.RequestId, ActionArtifactKind.Progress, cancellationToken).ConfigureAwait(false);
            if (progress is not null)
            {
                if (progress.Length < parseState.BytesConsumed)
                    throw new ActionProtocolValidationException(ActionProtocolFailure.InvalidTransition, "The progress artifact was truncated during observation.");
                var unread = progress.AsSpan(checked((int)parseState.BytesConsumed));
                var batch = progressCodec.ParseIncremental(unread, request, parseState);
                if (batch.Issues.Count > 0)
                    throw new ActionProtocolValidationException(batch.Issues[0].Failure, batch.Issues[0].Detail);
                parseState = batch.State;
                foreach (var record in batch.Records)
                {
                    lifecycle!.AttachProgress(request.RequestId, record);
                    observedRecords.Add(record);
                }
                if (batch.Records.Count > 0)
                    SetSnapshot(Snapshot.State, request.RequestId, batch.Records[^1].Message, observedRecords, null);
            }

            var resultPayload = await store.TryReadArtifactAsync(request.RequestId, ActionArtifactKind.Result, cancellationToken).ConfigureAwait(false);
            if (resultPayload is not null)
            {
                var result = resultCodec.Parse(resultPayload, request, paths!);
                lifecycle!.AttachFinalResult(result);
                return result;
            }

            if (session.HasExited)
                throw new InvalidOperationException($"The compiled worker exited with code {session.ExitCode?.ToString() ?? "unknown"} without a correlated final result.");
            await Task.Delay(pollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("The compiled worker did not produce a final result within the bounded observation period.");
    }

    private void SetSnapshot(
        CompiledActionState state,
        string requestId,
        string status,
        IReadOnlyList<ActionProgressRecord> progress,
        ActionFinalResult? result)
    {
        Snapshot = new(state, requestId, Diagnostics.DiagnosticsRedactor.Sanitize(status),
            Array.AsReadOnly(progress.ToArray()), result);
        StateChanged?.Invoke(this, Snapshot);
    }
}
