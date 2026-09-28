using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;

namespace AVWorkstationToolkit.Application.Workers;

public interface IActionWorkerPlanProvider
{
    long ManagedCatalogRevision => 0;

    /// <summary>
    /// A plan that reflects the workstation as it is now: read after the last <see cref="StateMayChange"/>. A provider may
    /// return the plan it read since then instead of reading again, because nothing that changes installed state has run.
    /// </summary>
    ValueTask<WorkstationPlan> ReadFreshPlanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The worker is about to run an installer or wait, so any plan read so far may no longer be current. The next
    /// <see cref="ReadFreshPlanAsync"/> must read a new one.
    /// </summary>
    void StateMayChange() { }
}

public interface IPackageActionExecutor
{
    ValueTask<PackageExecutionResult> ExecuteAsync(PackageExecutionRequest request, CancellationToken cancellationToken = default);
}

public interface IActionWorkerProtocol
{
    ActionArtifactPaths Paths { get; }
    ValueTask InitializeAsync(ActionRequest request, CancellationToken cancellationToken = default);
    ValueTask<bool> IsCancellationRequestedAsync(CancellationToken cancellationToken = default);
    ValueTask AppendProgressAsync(ActionRequest request, ActionProgressRecord record, CancellationToken cancellationToken = default);
    ValueTask PersistFinalResultAsync(ActionRequest request, ActionFinalResult result, CancellationToken cancellationToken = default);
}

public enum PackageExecutionDisposition
{
    Succeeded,
    Failed,
    VerificationFailed,
    TimedOut
}

public sealed record PackageExecutionRequest
{
    internal PackageExecutionRequest(
        string id,
        string name,
        ManagedRequestAction action,
        PackageRisk risk,
        InstallerExecutionMode installerMode = InstallerExecutionMode.Silent)
    {
        Id = id;
        Name = name;
        Action = action;
        Risk = risk;
        InstallerMode = installerMode;
    }

    public string Id { get; }
    public string Name { get; }
    public ManagedRequestAction Action { get; }
    public PackageRisk Risk { get; }
    public InstallerExecutionMode InstallerMode { get; }
}

public sealed record PackageExecutionResult(
    PackageExecutionDisposition Disposition,
    int ExitCode,
    string StandardOutput = "",
    string StandardError = "")
{
    public static PackageExecutionResult Success { get; } = new(PackageExecutionDisposition.Succeeded, 0);
    public static PackageExecutionResult VerificationFailure { get; } = new(PackageExecutionDisposition.VerificationFailed, 0);
    public static PackageExecutionResult Timeout { get; } = new(PackageExecutionDisposition.TimedOut, -1);

    public static PackageExecutionResult Failure(int exitCode)
    {
        if (exitCode == 0) throw new ArgumentOutOfRangeException(nameof(exitCode), "A failed execution requires a nonzero exit code.");
        return new(PackageExecutionDisposition.Failed, exitCode);
    }
}

public sealed record ActionWorkerRunResult(ActionResultStatus Status, int ExitCode, IReadOnlyList<ActionPackageOutcome> Packages);

/// <summary>
/// Worker orchestration. It independently resolves the complete request (every ID must be exactly one managed WinGet
/// package with execution authority), reauthorizes each package against a fresh plan just before it runs, and delegates
/// package behavior to the configured constrained executor. A package already where the request wanted it is settled
/// without running anything; recognized WinGet results get a plain reason and, when they usually clear, a bounded retry
/// of the same reviewed vector after the same recheck.
/// </summary>
public sealed class ActionWorkerOrchestrator
{
    // Well inside ActionProtocolLimits.MaximumMessageCharacters so the surrounding sentence, the
    // exit codes and the shortening marker cannot push a record past what the codec accepts.
    private const int MaximumDiagnosticExcerptCharacters = 1_200;

    private readonly IActionWorkerPlanProvider planProvider;
    private readonly IPackageActionExecutor executor;
    private readonly IActionWorkerProtocol protocol;
    private readonly ActionRequestAuthorizationService authorization;
    private readonly IOpenApplicationService openApplications;
    private readonly WorkerRetryPolicy retryPolicy;
    private readonly TimeProvider timeProvider;
    private readonly string computerName;

    public ActionWorkerOrchestrator(
        IActionWorkerPlanProvider planProvider,
        IPackageActionExecutor executor,
        IActionWorkerProtocol protocol,
        string computerName,
        ActionRequestAuthorizationService? authorization = null,
        TimeProvider? timeProvider = null,
        IOpenApplicationService? openApplications = null,
        WorkerRetryPolicy? retryPolicy = null)
    {
        this.planProvider = planProvider ?? throw new ArgumentNullException(nameof(planProvider));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        this.authorization = authorization ?? new ActionRequestAuthorizationService();
        this.openApplications = openApplications ?? NoOpenApplications.Instance;
        this.retryPolicy = retryPolicy ?? WorkerRetryPolicy.Default;
        if (this.retryPolicy.Pauses.Count > 5 || this.retryPolicy.Pauses.Any(pause => pause < TimeSpan.Zero || pause > TimeSpan.FromMinutes(5)))
            throw new ArgumentOutOfRangeException(nameof(retryPolicy), "Retries are bounded to five, each at most five minutes apart.");
        this.timeProvider = timeProvider ?? TimeProvider.System;
        if (string.IsNullOrWhiteSpace(computerName) || computerName.Length > ActionProtocolLimits.MaximumComputerCharacters || computerName.Any(char.IsControl))
            throw new ArgumentException("The worker computer label is invalid.", nameof(computerName));
        this.computerName = computerName;
    }

    public async Task<ActionWorkerRunResult> RunAsync(ActionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ActionRequestRules.Validate(request);
        await protocol.InitializeAsync(request, cancellationToken).ConfigureAwait(false);

        var outcomes = new List<ActionPackageOutcome>();
        await ProgressAsync(request, ActionProgressLevel.Info, "Preflight", string.Empty,
            $"Preparing to {ActionVerb(request.Action)} {PackageCount(request.PackageIds.Count)}.", cancellationToken).ConfigureAwait(false);

        if (request.ManagedCatalogRevision != planProvider.ManagedCatalogRevision)
        {
            return await CompleteAsync(request, ActionResultStatus.Rejected, 1,
                ActionProtocolSemantics.ManagedCatalogRevisionMismatchMessage(
                    request.ManagedCatalogRevision, planProvider.ManagedCatalogRevision),
                outcomes, cancellationToken).ConfigureAwait(false);
        }

        // The whole request is refused only for what live state can't change: an ID that isn't exactly one managed WinGet
        // package with execution authority. Whether each package may run now (action, hold, restart, risk) is decided just
        // before it runs, against a fresh plan, so one stale or blocked package can't sink the others.
        IReadOnlyList<PackageState> planned;
        try
        {
            var initialPlan = await planProvider.ReadFreshPlanAsync(cancellationToken).ConfigureAwait(false);
            planned = authorization.ResolveRequested(request, initialPlan);
        }
        catch (Exception exception) when (exception is ActionRequestValidationException or InvalidOperationException)
        {
            return await CompleteAsync(request, ActionResultStatus.Rejected, 1, exception.Message, outcomes, cancellationToken).ConfigureAwait(false);
        }

        var cancellationObserved = false;
        for (var index = 0; index < planned.Count; index++)
        {
            if (await protocol.IsCancellationRequestedAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationObserved = true;
                // Every requested package stays in the result, so the ones the run never reached are recorded too.
                foreach (var remaining in planned.Skip(index))
                    outcomes.Add(CreateOutcome(remaining, request.Action, PackageOutcomeStatus.NotStarted, 2, false, null, []));
                await ProgressAsync(request, ActionProgressLevel.Warning, "Cancelled", string.Empty,
                    $"Stopped before starting the next package. {PackageCount(planned.Count - index)} not started.", cancellationToken).ConfigureAwait(false);
                break;
            }
            outcomes.Add(await RunPackageAsync(request, planned[index], cancellationToken).ConfigureAwait(false));
        }

        var failedCount = outcomes.Count(item => item.Status is PackageOutcomeStatus.Failed or PackageOutcomeStatus.Unverified);
        var heldCount = outcomes.Count(item => item.Status is PackageOutcomeStatus.Blocked or PackageOutcomeStatus.InUse or PackageOutcomeStatus.RestartRequired);
        var cancellationPresent = cancellationObserved || await protocol.IsCancellationRequestedAsync(cancellationToken).ConfigureAwait(false);
        if (failedCount > 0)
            return await CompleteAsync(request, ActionResultStatus.Failed, 1, Summary(outcomes), outcomes, cancellationToken).ConfigureAwait(false);
        if (heldCount > 0)
            return await CompleteAsync(request, ActionResultStatus.Blocked, 3, Summary(outcomes), outcomes, cancellationToken).ConfigureAwait(false);
        if (cancellationPresent)
        {
            var stopped = outcomes.Any(item => item.Status == PackageOutcomeStatus.NotStarted)
                ? $"Stopped after the current package. {Summary(outcomes)}"
                : "Stopped after the current package.";
            return await CompleteAsync(request, ActionResultStatus.Cancelled, 2, stopped, outcomes, cancellationToken).ConfigureAwait(false);
        }

        var successMessage = request.DryRun ? "Test run completed. No changes were made." : "All selected apps were completed and verified.";
        await ProgressAsync(request, ActionProgressLevel.Success, "Complete", string.Empty, successMessage, cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(request, ActionResultStatus.Succeeded, 0, successMessage, outcomes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One package, start to finish: a fresh recheck, the open-app rule, the installer with bounded recovery, and a fresh
    /// verification. Recovery never widens authority: a retry is the same reviewed one-package vector after the same recheck,
    /// and an app is closed only for a consented package, only by asking.
    /// </summary>
    private async Task<ActionPackageOutcome> RunPackageAsync(ActionRequest request, PackageState planned, CancellationToken cancellationToken)
    {
        var recheck = await RecheckAsync(request, planned, cancellationToken).ConfigureAwait(false);
        if (recheck.Done is { } done) return done;
        var package = recheck.Package!;
        var arguments = ReviewedArgumentEvidence(package, request.Action);
        if (request.DryRun)
        {
            var plannedAt = timeProvider.GetUtcNow();
            await ProgressAsync(request, ActionProgressLevel.Info, "Starting", package.Package.Id,
                $"{ActionInProgress(request.Action)} {package.Package.Name}.", cancellationToken).ConfigureAwait(false);
            await ProgressAsync(request, ActionProgressLevel.Success, "Planned", package.Package.Id,
                "Safety checks passed. No change was made during this test run.", cancellationToken).ConfigureAwait(false);
            return CreateOutcome(package, request.Action, PackageOutcomeStatus.Planned, 0, false, plannedAt, arguments);
        }

        // Many installers refuse to replace a program that is running. An app is closed only for a package the technician
        // agreed to, only by asking it, and only just before that package's own installer runs.
        var consented = request.CloseOpenAppsFor.Contains(package.Package.Id, StringComparer.OrdinalIgnoreCase);
        IOpenApplicationClosure? closure = null;
        var open = openApplications.FindOpen(package.Package.Id);
        if (open.Count > 0)
        {
            if (!consented)
            {
                await ProgressAsync(request, ActionProgressLevel.Warning, "InUse", package.Package.Id,
                    $"{OpenApplicationText.Names(open)} {IsAre(open)} open, so {package.Package.Name} wasn't {PastLower(request.Action)}. " +
                    $"Close {ItThem(open)} and {ActionVerb(request.Action)} it again.", cancellationToken).ConfigureAwait(false);
                return CreateOutcome(package, request.Action, PackageOutcomeStatus.InUse, 3, false, null, []);
            }
            await ProgressAsync(request, ActionProgressLevel.Info, "Closing", package.Package.Id,
                $"Asking {OpenApplicationText.Names(open)} to close so {package.Package.Name} can be {PastLower(request.Action)}.", cancellationToken).ConfigureAwait(false);
            closure = openApplications.Close(package.Package.Id);
            if (closure.StillOpen.Count > 0)
            {
                var stillOpen = closure.StillOpen;
                var reopenedEarly = closure.Reopen();
                closure.Dispose();
                await ProgressAsync(request, ActionProgressLevel.Warning, "InUse", package.Package.Id,
                    $"{OpenApplicationText.Names(stillOpen)} didn't close when asked, so {package.Package.Name} wasn't {PastLower(request.Action)}. " +
                    $"{(OpenApplicationText.IsPlural(stillOpen) ? "They" : "It")} may have unsaved work: close {ItThem(stillOpen)} yourself, then {ActionVerb(request.Action)} it again." +
                    ReopenedNote(closure.Closed, reopenedEarly), cancellationToken).ConfigureAwait(false);
                return CreateOutcome(package, request.Action, PackageOutcomeStatus.InUse, 3, false, null, []);
            }
        }

        var startedAt = timeProvider.GetUtcNow();
        await ProgressAsync(request, ActionProgressLevel.Info, "Starting", package.Package.Id,
            $"{ActionInProgress(request.Action)} {package.Package.Name}.", cancellationToken).ConfigureAwait(false);
        PackageExecutionResult execution;
        IReadOnlyList<string> reopened = [];
        IReadOnlyList<string> openAfterFailure = [];
        try
        {
            var retries = 0;
            while (true)
            {
                // Any plan read so far describes the workstation before this installer; the next check must read again.
                planProvider.StateMayChange();
                execution = await executor.ExecuteAsync(
                    new PackageExecutionRequest(package.Package.Id, package.Package.Name, request.Action, package.Package.Risk, package.Package.InstallerMode),
                    cancellationToken).ConfigureAwait(false);
                if (execution.Disposition != PackageExecutionDisposition.Failed) break;
                var known = WinGetOutcomes.For(execution.ExitCode);

                // Conditions that usually clear by themselves: pause, recheck against a fresh plan, and try the same request again.
                if (known?.Recovery == WinGetRecovery.RetryLater && retries < retryPolicy.Pauses.Count)
                {
                    var pause = retryPolicy.Pauses[retries++];
                    await ProgressAsync(request, ActionProgressLevel.Warning, "Retrying", package.Package.Id,
                        $"{package.Package.Name}: {known.Reason}. Trying again in {Describe(pause)} (try {retries + 1} of {retryPolicy.Pauses.Count + 1}).",
                        cancellationToken).ConfigureAwait(false);
                    if (pause > TimeSpan.Zero) await Task.Delay(pause, timeProvider, cancellationToken).ConfigureAwait(false);
                    if (await protocol.IsCancellationRequestedAsync(cancellationToken).ConfigureAwait(false)) break;
                    var again = await RecheckAsync(request, planned, cancellationToken).ConfigureAwait(false);
                    if (again.Done is { } settled) return settled;
                    package = again.Package!;
                    continue;
                }

                // A failure while the package's app is open: with consent, ask it to close and try once more.
                if (known?.Recovery is null or WinGetRecovery.CloseOpenApps or WinGetRecovery.None)
                {
                    var nowOpen = openApplications.FindOpen(package.Package.Id);
                    if (nowOpen.Count > 0 && consented && closure is null)
                    {
                        await ProgressAsync(request, ActionProgressLevel.Warning, "Closing", package.Package.Id,
                            $"{OpenApplicationText.Names(nowOpen)} {IsAre(nowOpen)} open, which can stop the installer. Asking {ItThem(nowOpen)} to close and trying again.",
                            cancellationToken).ConfigureAwait(false);
                        closure = openApplications.Close(package.Package.Id);
                        if (closure.StillOpen.Count == 0) continue;
                        openAfterFailure = closure.StillOpen;
                    }
                    else if (nowOpen.Count > 0)
                    {
                        openAfterFailure = nowOpen;
                    }
                }
                break;
            }
        }
        finally
        {
            // Whatever the installer did, the apps it needed closed are offered back as soon as it has finished.
            if (closure is not null)
            {
                reopened = closure.Reopen();
                closure.Dispose();
            }
        }
        if (closure is not null && closure.Closed.Count > 0)
            await ProgressAsync(request, ActionProgressLevel.Info, "Reopened", package.Package.Id,
                ReopenedNote(closure.Closed, reopened).TrimStart(), cancellationToken).ConfigureAwait(false);

        var outcome = WinGetOutcomes.For(execution.ExitCode);
        var verified = false;
        if (execution.Disposition == PackageExecutionDisposition.Succeeded ||
            execution.Disposition == PackageExecutionDisposition.Failed && outcome?.Recovery is WinGetRecovery.CheckIfCurrent or WinGetRecovery.RestartRequired)
        {
            try
            {
                var verificationPlan = await planProvider.ReadFreshPlanAsync(cancellationToken).ConfigureAwait(false);
                verified = VerifyPostActionState(request.Action, package.Package.Id, verificationPlan);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                verified = false;
            }
        }
        var status = execution.Disposition switch
        {
            // WinGet treats "restart required to finish" as success and only prints a notice, so the notice is what says so.
            PackageExecutionDisposition.Succeeded when WinGetOutcomes.ReportsRestartToFinish(execution) => PackageOutcomeStatus.RestartRequired,
            PackageExecutionDisposition.Succeeded when verified => PackageOutcomeStatus.Succeeded,
            PackageExecutionDisposition.Succeeded => PackageOutcomeStatus.Unverified,
            PackageExecutionDisposition.Failed when outcome?.Recovery == WinGetRecovery.RestartRequired => PackageOutcomeStatus.RestartRequired,
            PackageExecutionDisposition.Failed when verified => PackageOutcomeStatus.AlreadyCurrent,
            PackageExecutionDisposition.Failed => PackageOutcomeStatus.Failed,
            PackageExecutionDisposition.VerificationFailed => PackageOutcomeStatus.Unverified,
            PackageExecutionDisposition.TimedOut => PackageOutcomeStatus.Failed,
            _ => throw new InvalidOperationException("The package executor returned an unknown disposition.")
        };
        if (execution.Disposition == PackageExecutionDisposition.Failed && execution.ExitCode == 0)
            throw new InvalidOperationException("The package executor returned a failed disposition with a zero exit code.");
        if (execution.Disposition == PackageExecutionDisposition.TimedOut && execution.ExitCode != -1)
            throw new InvalidOperationException("The package executor returned an invalid timeout exit code.");
        if (execution.Disposition is PackageExecutionDisposition.Succeeded or PackageExecutionDisposition.VerificationFailed && execution.ExitCode != 0)
            throw new InvalidOperationException("The package executor returned a non-failed disposition with a nonzero exit code.");

        var (level, stage, message) = status switch
        {
            PackageOutcomeStatus.Succeeded => (ActionProgressLevel.Success, "Verified", $"{ActionPastTense(request.Action)} and verified."),
            PackageOutcomeStatus.AlreadyCurrent => (ActionProgressLevel.Success, "AlreadyCurrent",
                $"{package.Package.Name} is already {(request.Action == ManagedRequestAction.Install ? "installed" : "up to date")}; nothing needed changing."),
            PackageOutcomeStatus.RestartRequired => (ActionProgressLevel.Warning, "RestartRequired", verified
                ? $"{package.Package.Name} is now detected, but Windows must restart before the installation is complete."
                : $"WinGet changed {package.Package.Name}, but Windows must restart before the installation is complete."),
            PackageOutcomeStatus.Unverified => (ActionProgressLevel.Error, "Verification", "WinGet finished, but AVWT couldn't confirm the installed version."),
            PackageOutcomeStatus.Failed when execution.Disposition == PackageExecutionDisposition.TimedOut =>
                (ActionProgressLevel.Error, "Failed", "WinGet didn't finish within the allowed time."),
            _ => (ActionProgressLevel.Error, "Failed", FailureMessage(package.Package.Name, request.Action, execution, outcome, openAfterFailure))
        };
        await ProgressAsync(request, level, stage, package.Package.Id, message, cancellationToken).ConfigureAwait(false);
        return CreateOutcome(package, request.Action, status, execution.ExitCode, verified, startedAt, arguments);
    }

    /// <summary>
    /// The per-package recheck against a fresh plan. A package that is already where the request wanted it is settled
    /// without running anything; one that may not run now is skipped with its reason; otherwise the reauthorized state.
    /// </summary>
    private async Task<(PackageState? Package, ActionPackageOutcome? Done)> RecheckAsync(
        ActionRequest request, PackageState planned, CancellationToken cancellationToken)
    {
        try
        {
            var freshPlan = await planProvider.ReadFreshPlanAsync(cancellationToken).ConfigureAwait(false);
            if (VerifyPostActionState(request.Action, planned.Package.Id, freshPlan))
            {
                await ProgressAsync(request, ActionProgressLevel.Success, "AlreadyCurrent", planned.Package.Id,
                    $"{planned.Package.Name} is already {(request.Action == ManagedRequestAction.Install ? "installed" : "up to date")}; nothing needed changing.",
                    cancellationToken).ConfigureAwait(false);
                return (null, CreateOutcome(planned, request.Action, PackageOutcomeStatus.AlreadyCurrent, 0, true, null, []));
            }
            var singleRequest = new ActionRequest(request.SchemaVersion, request.RequestId, request.Action,
                [planned.Package.Id], request.RiskAcknowledged, request.DryRun, request.ManagedCatalogRevision);
            return (authorization.Authorize(singleRequest, freshPlan).Packages.Single(), null);
        }
        catch (Exception exception) when (exception is ActionRequestValidationException or InvalidOperationException)
        {
            // A package that fails its recheck is skipped on its own; the rest of the run continues, and each later
            // package is rechecked against a fresh plan as always.
            await ProgressAsync(request, ActionProgressLevel.Warning, "Blocked", planned.Package.Id,
                $"Skipped {planned.Package.Name}. {BlockedReason(exception)}", cancellationToken).ConfigureAwait(false);
            return (null, CreateOutcome(planned, request.Action, PackageOutcomeStatus.Blocked, 3, false, null, []));
        }
    }

    private static string Describe(TimeSpan pause) =>
        pause >= TimeSpan.FromMinutes(1) && pause.Seconds == 0
            ? pause.TotalMinutes == 1 ? "a minute" : $"{(int)pause.TotalMinutes} minutes"
            : $"{(int)Math.Ceiling(pause.TotalSeconds)} seconds";

    private static string IsAre(IReadOnlyList<string> names) => OpenApplicationText.IsPlural(names) ? "are" : "is";
    private static string ItThem(IReadOnlyList<string> names) => OpenApplicationText.IsPlural(names) ? "them" : "it";
    private static string PastLower(ManagedRequestAction action) => ActionPastTense(action).ToLowerInvariant();

    /// <summary>One sentence per kind of outcome that needs the technician, most serious first.</summary>
    private static string Summary(IReadOnlyList<ActionPackageOutcome> outcomes)
    {
        var sentences = new List<string>();
        var failed = outcomes.Count(item => item.Status is PackageOutcomeStatus.Failed or PackageOutcomeStatus.Unverified);
        var inUse = outcomes.Count(item => item.Status == PackageOutcomeStatus.InUse);
        var blocked = outcomes.Count(item => item.Status == PackageOutcomeStatus.Blocked);
        var restartRequired = outcomes.Count(item => item.Status == PackageOutcomeStatus.RestartRequired);
        var notStarted = outcomes.Count(item => item.Status == PackageOutcomeStatus.NotStarted);
        if (failed > 0) sentences.Add($"{PackageCount(failed)} couldn't be completed or verified.");
        if (inUse > 0) sentences.Add(inUse == 1 ? "1 app was open, so it wasn't changed." : $"{inUse} apps were open, so they weren't changed.");
        if (blocked > 0) sentences.Add(blocked == 1 ? "1 app was skipped when it was rechecked." : $"{blocked} apps were skipped when they were rechecked.");
        if (restartRequired > 0) sentences.Add(restartRequired == 1
            ? "1 app needs Windows restarted to finish."
            : $"{restartRequired} apps need Windows restarted to finish.");
        if (notStarted > 0) sentences.Add(notStarted == 1 ? "1 app wasn't started." : $"{notStarted} apps weren't started.");
        return string.Join(' ', sentences);
    }

    private static string BlockedReason(Exception exception) => exception switch
    {
        ActionRequestValidationException { ReasonCode: "PendingRebootRiskBlocked" } =>
            "Windows is waiting for a restart, and this app can install a driver, add a background service, or accept network connections. Restart Windows, then try it again.",
        _ => $"It was blocked when it was rechecked: {exception.Message}"
    };

    private static string ReopenedNote(IReadOnlyList<string> closed, IReadOnlyList<string> reopened)
    {
        if (closed.Count == 0) return string.Empty;
        var notReopened = closed.Where(name => !reopened.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();
        var note = reopened.Count > 0 ? $" Reopened {OpenApplicationText.Names(reopened)}." : string.Empty;
        if (notReopened.Length > 0)
            note += $" {OpenApplicationText.Names(notReopened)} {(OpenApplicationText.IsPlural(notReopened) ? "were" : "was")} closed and can't reopen by itself; open {(OpenApplicationText.IsPlural(notReopened) ? "them" : "it")} again when you need {(OpenApplicationText.IsPlural(notReopened) ? "them" : "it")}.";
        return note;
    }

    private async ValueTask ProgressAsync(
        ActionRequest request,
        ActionProgressLevel level,
        string stage,
        string packageId,
        string message,
        CancellationToken cancellationToken)
    {
        var record = new ActionProgressRecord(timeProvider.GetUtcNow(), level, stage, packageId, message);
        await protocol.AppendProgressAsync(request, record, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ActionWorkerRunResult> CompleteAsync(
        ActionRequest request,
        ActionResultStatus status,
        int exitCode,
        string message,
        IReadOnlyList<ActionPackageOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        var result = new ActionFinalResult(
            ActionProtocolLimits.CurrentResultSchemaVersion,
            request.RequestId,
            timeProvider.GetUtcNow(),
            computerName,
            status,
            message,
            exitCode,
            planProvider.ManagedCatalogRevision,
            protocol.Paths.RequestPath,
            protocol.Paths.ProgressPath,
            protocol.Paths.WinGetLogPath,
            Array.AsReadOnly(outcomes.ToArray()));
        await protocol.PersistFinalResultAsync(request, result, cancellationToken).ConfigureAwait(false);
        return new(status, exitCode, result.Packages);
    }

    private ActionPackageOutcome CreateOutcome(
        PackageState package,
        ManagedRequestAction action,
        PackageOutcomeStatus status,
        int exitCode,
        bool verified,
        DateTimeOffset? startedAt,
        IReadOnlyList<string> arguments) =>
        new(package.Package.Id, package.Package.Name, action, status, exitCode, verified, startedAt,
            timeProvider.GetUtcNow(), Array.AsReadOnly(arguments.ToArray()));

    /// <summary>
    /// The bounded progress message is the only failure detail an operator sees. An exit code alone
    /// cannot say which installer step failed, so WinGet's own output travels with it. The code is
    /// also given in hexadecimal because WinGet documents its results that way - -1978335184 is
    /// 0x8A150030 - and the signed decimal on its own is not searchable.
    /// </summary>
    private static string FailureMessage(
        string name,
        ManagedRequestAction action,
        PackageExecutionResult execution,
        WinGetOutcome? outcome,
        IReadOnlyList<string> openApps)
    {
        var codes = $"Exit code: {execution.ExitCode} (0x{execution.ExitCode:X8}).";
        var excerpt = DiagnosticExcerpt(execution);
        var reason = outcome is null ? string.Empty : $"{name} couldn't be {PastLower(action)}: {outcome.Reason}. ";
        var open = openApps.Count == 0 ? string.Empty
            : $"{OpenApplicationText.Names(openApps)} {IsAre(openApps)} open, which can stop its installer: close {ItThem(openApps)} and {ActionVerb(action)} it again. ";
        return excerpt.Length == 0
            ? $"{reason}{open}WinGet couldn't complete the change. {codes}"
            : $"{reason}{open}WinGet couldn't complete the change. {codes} WinGet reported: {excerpt}";
    }

    /// <summary>
    /// Standard error comes first because a failing installer step reports there and the excerpt
    /// budget is small. Sanitizing here is not a repeat of the adapter's own redaction: this method
    /// is the boundary that publishes process output into an operator-visible artifact, and
    /// <see cref="IPackageActionExecutor"/> does not itself promise sanitized text.
    /// Whitespace is collapsed because <see cref="DiagnosticsRedactor"/> deliberately preserves tab,
    /// carriage return and line feed, while a progress message may carry no control character at all.
    /// </summary>
    private static string DiagnosticExcerpt(PackageExecutionResult execution)
    {
        var parts = new[] { execution.StandardError, execution.StandardOutput }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        if (parts.Length == 0) return string.Empty;
        var sanitized = DiagnosticsRedactor.Sanitize(string.Join(" ", parts));
        var collapsed = string.Join(' ', sanitized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > MaximumDiagnosticExcerptCharacters
            ? collapsed[..MaximumDiagnosticExcerptCharacters] + " [excerpt shortened]"
            : collapsed;
    }

    private static IReadOnlyList<string> ReviewedArgumentEvidence(PackageState package, ManagedRequestAction action) =>
        ManagedWinGetArgumentPolicy.Create(new PackageExecutionRequest(package.Package.Id, package.Package.Name, action, package.Package.Risk, package.Package.InstallerMode));

    private static bool VerifyPostActionState(ManagedRequestAction action, string packageId, WorkstationPlan plan)
    {
        var matches = plan.Packages.Where(item => item.Package.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].Package.Authority != CatalogAuthority.ManagedWinGet ||
            matches[0].Package.Provider != ProviderKind.WinGet ||
            plan.Providers.WinGetInventoryQuality != ProviderQuality.Complete)
            return false;

        var package = matches[0];
        return action switch
        {
            ManagedRequestAction.Install => package.Installed,
            ManagedRequestAction.Update => package.Installed &&
                plan.Providers.WinGetUpdateQuality == ProviderQuality.Complete && !package.UpgradeAvailable,
            _ => false
        };
    }

    private static string ActionVerb(ManagedRequestAction action) => action == ManagedRequestAction.Install ? "install" : "update";
    private static string ActionInProgress(ManagedRequestAction action) => action == ManagedRequestAction.Install ? "Installing" : "Updating";
    private static string ActionPastTense(ManagedRequestAction action) => action == ManagedRequestAction.Install ? "Installed" : "Updated";
    private static string PackageCount(int count) => count == 1 ? "1 app" : $"{count} apps";
}
