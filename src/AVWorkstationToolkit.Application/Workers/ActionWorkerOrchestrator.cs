using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;

namespace AVWorkstationToolkit.Application.Workers;

public interface IActionWorkerPlanProvider
{
    ValueTask<WorkstationPlan> ReadFreshPlanAsync(CancellationToken cancellationToken = default);
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
    internal PackageExecutionRequest(string id, string name, ManagedRequestAction action, PackageRisk risk)
    {
        Id = id;
        Name = name;
        Action = action;
        Risk = risk;
    }

    public string Id { get; }
    public string Name { get; }
    public ManagedRequestAction Action { get; }
    public PackageRisk Risk { get; }
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
/// Non-shipping worker orchestration. It independently reauthorizes the complete
/// request and every individual package, but delegates package behavior to an
/// injected executor. The non-shipping worker host intentionally supplies only
/// its deterministic fake; the real migration executor is not composed there.
/// </summary>
public sealed class ActionWorkerOrchestrator
{
    private readonly IActionWorkerPlanProvider planProvider;
    private readonly IPackageActionExecutor executor;
    private readonly IActionWorkerProtocol protocol;
    private readonly ActionRequestAuthorizationService authorization;
    private readonly TimeProvider timeProvider;
    private readonly string computerName;

    public ActionWorkerOrchestrator(
        IActionWorkerPlanProvider planProvider,
        IPackageActionExecutor executor,
        IActionWorkerProtocol protocol,
        string computerName,
        ActionRequestAuthorizationService? authorization = null,
        TimeProvider? timeProvider = null)
    {
        this.planProvider = planProvider ?? throw new ArgumentNullException(nameof(planProvider));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        this.authorization = authorization ?? new ActionRequestAuthorizationService();
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
            $"Preparing {request.Action.ToString().ToLowerInvariant()} request for {request.PackageIds.Count} package(s).", cancellationToken).ConfigureAwait(false);

        AuthorizedActionRequest initiallyAuthorized;
        try
        {
            var initialPlan = await planProvider.ReadFreshPlanAsync(cancellationToken).ConfigureAwait(false);
            initiallyAuthorized = authorization.Authorize(request, initialPlan);
        }
        catch (Exception exception) when (exception is ActionRequestValidationException or InvalidOperationException)
        {
            return await CompleteAsync(request, ActionResultStatus.Rejected, 1, exception.Message, outcomes, cancellationToken).ConfigureAwait(false);
        }

        string blockReason = string.Empty;
        var cancellationObserved = false;
        foreach (var initiallyPlannedPackage in initiallyAuthorized.Packages)
        {
            if (await protocol.IsCancellationRequestedAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationObserved = true;
                await ProgressAsync(request, ActionProgressLevel.Warning, "Cancelled", string.Empty,
                    "Stopped before starting the next package.", cancellationToken).ConfigureAwait(false);
                break;
            }

            PackageState package;
            try
            {
                var freshPlan = await planProvider.ReadFreshPlanAsync(cancellationToken).ConfigureAwait(false);
                var singleRequest = new ActionRequest(request.SchemaVersion, request.RequestId, request.Action,
                    [initiallyPlannedPackage.Package.Id], request.RiskAcknowledged, request.DryRun);
                package = authorization.Authorize(singleRequest, freshPlan).Packages.Single();
            }
            catch (Exception exception) when (exception is ActionRequestValidationException or InvalidOperationException)
            {
                blockReason = "Stopped before the next package: " + exception.Message;
                outcomes.Add(CreateOutcome(initiallyPlannedPackage, request.Action, PackageOutcomeStatus.Blocked, 3, false, null, []));
                await ProgressAsync(request, ActionProgressLevel.Warning, "Blocked", initiallyPlannedPackage.Package.Id,
                    blockReason, cancellationToken).ConfigureAwait(false);
                break;
            }

            var startedAt = timeProvider.GetUtcNow();
            var arguments = ReviewedArgumentEvidence(package, request.Action);
            await ProgressAsync(request, ActionProgressLevel.Info, "Starting", package.Package.Id,
                $"{request.Action}: {package.Package.Name}", cancellationToken).ConfigureAwait(false);

            if (request.DryRun)
            {
                outcomes.Add(CreateOutcome(package, request.Action, PackageOutcomeStatus.Planned, 0, false, startedAt, arguments));
                await ProgressAsync(request, ActionProgressLevel.Success, "Planned", package.Package.Id,
                    "Dry run validated; no change executed.", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var execution = await executor.ExecuteAsync(
                new PackageExecutionRequest(package.Package.Id, package.Package.Name, request.Action, package.Package.Risk),
                cancellationToken).ConfigureAwait(false);
            var verified = false;
            if (execution.Disposition == PackageExecutionDisposition.Succeeded)
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
                PackageExecutionDisposition.Succeeded when verified => PackageOutcomeStatus.Succeeded,
                PackageExecutionDisposition.Succeeded => PackageOutcomeStatus.Unverified,
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
            outcomes.Add(CreateOutcome(package, request.Action, status, execution.ExitCode, verified, startedAt, arguments));

            var (level, stage, message) = status switch
            {
                PackageOutcomeStatus.Succeeded => (ActionProgressLevel.Success, "Verified", $"Verified {request.Action.ToString().ToLowerInvariant()}."),
                PackageOutcomeStatus.Unverified => (ActionProgressLevel.Error, "Verification", "winget returned success, but post-action verification failed."),
                PackageOutcomeStatus.Failed when execution.Disposition == PackageExecutionDisposition.TimedOut =>
                    (ActionProgressLevel.Error, "Failed", "winget execution exceeded the bounded timeout."),
                _ => (ActionProgressLevel.Error, "Failed", $"The injected executor failed with code {execution.ExitCode}.")
            };
            await ProgressAsync(request, level, stage, package.Package.Id, message, cancellationToken).ConfigureAwait(false);
        }

        var failed = outcomes.Any(item => item.Status is PackageOutcomeStatus.Failed or PackageOutcomeStatus.Unverified);
        var blocked = outcomes.Any(item => item.Status == PackageOutcomeStatus.Blocked);
        var cancellationPresent = cancellationObserved || await protocol.IsCancellationRequestedAsync(cancellationToken).ConfigureAwait(false);
        if (failed)
            return await CompleteAsync(request, ActionResultStatus.Failed, 1,
                $"{outcomes.Count(item => item.Status is PackageOutcomeStatus.Failed or PackageOutcomeStatus.Unverified)} package(s) failed or could not be verified.", outcomes, cancellationToken).ConfigureAwait(false);
        if (blocked)
            return await CompleteAsync(request, ActionResultStatus.Blocked, 3, blockReason, outcomes, cancellationToken).ConfigureAwait(false);
        if (cancellationPresent)
            return await CompleteAsync(request, ActionResultStatus.Cancelled, 2, "Stopped after the current package.", outcomes, cancellationToken).ConfigureAwait(false);

        var successMessage = request.DryRun ? "Dry run completed." : "All requested packages completed and were verified.";
        await ProgressAsync(request, ActionProgressLevel.Success, "Complete", string.Empty, successMessage, cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(request, ActionResultStatus.Succeeded, 0, successMessage, outcomes, cancellationToken).ConfigureAwait(false);
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
            ActionRequestRules.CurrentSchemaVersion,
            request.RequestId,
            timeProvider.GetUtcNow(),
            computerName,
            status,
            message,
            exitCode,
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

    private static IReadOnlyList<string> ReviewedArgumentEvidence(PackageState package, ManagedRequestAction action) =>
        ManagedWinGetArgumentPolicy.Create(new PackageExecutionRequest(package.Package.Id, package.Package.Name, action, package.Package.Risk));

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
}
