using System.Security.Principal;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Development;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Files;
using AVWorkstationToolkit.Infrastructure.Windows.WinGet;
using AVWorkstationToolkit.App.Services;

namespace AVWorkstationToolkit.IntegrationTests;

internal sealed record LiveRehearsalResult(
    string Status,
    string CandidateId,
    string CandidateAction,
    string InstalledVersion,
    string AvailableVersion,
    int ProgressRecords,
    int WorkerProcessId,
    bool WorkerIndependent,
    bool RecoveredFromFreshStore,
    bool PostCompletionRefresh,
    bool TrustedWinGet,
    bool MutationPerformed);

internal static class LiveRehearsalBoundary
{
    public static async Task<LiveRehearsalResult> RunAsync(string repositoryRoot)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Live rehearsal requires a standard-user host.");

        var root = Path.Combine(Path.GetTempPath(), $"{LiveRehearsalRootPolicy.DirectoryPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var services = CompiledAppComposition.Create(Path.GetFullPath(repositoryRoot));
            var initialPlan = await services.Planning.RefreshAsync().ConfigureAwait(false);
            var candidate = initialPlan.Packages
                .Where(item => item.Package.Authority == CatalogAuthority.ManagedWinGet &&
                    item.Package.Provider == ProviderKind.WinGet &&
                    item.Package.Risk == PackageRisk.None &&
                    item.CanSelect &&
                    item.Action is PackageAction.Install or PackageAction.Update)
                .OrderBy(item => item.Action == PackageAction.Update ? 0 : 1)
                .ThenBy(item => item.Package.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("No low-risk managed package is available for a non-mutating dry-run rehearsal.");
            var action = candidate.Action == PackageAction.Install ? ManagedRequestAction.Install : ManagedRequestAction.Update;
            var request = new ActionRequestFactory().Create(action, [candidate.Package.Id], riskAcknowledged: false, dryRun: true);
            var authorized = new ActionRequestAuthorizationService().Authorize(request, initialPlan);
            var writer = new ActionProtocolStore(root);
            var paths = await writer.PersistRequestAsync(authorized).ConfigureAwait(false);
            var launcher = new CompiledLiveRehearsalWorkerLauncher(Path.GetFullPath(repositoryRoot), root);
            var session = await launcher.LaunchAsync(paths).ConfigureAwait(false);
            var processId = session.ProcessId;
            session.Dispose(); // Simulates the GUI closing; it deliberately does not terminate the worker.

            var recoveryStore = new ActionProtocolStore(root);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
            byte[]? resultPayload = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                resultPayload = await recoveryStore.TryReadArtifactAsync(request.RequestId, ActionArtifactKind.Result).ConfigureAwait(false);
                if (resultPayload is not null) break;
                await Task.Delay(100).ConfigureAwait(false);
            }
            if (resultPayload is null) throw new TimeoutException("The independent live rehearsal worker did not produce a correlated result.");

            var result = new ActionResultCodec().Parse(resultPayload, request, paths);
            if (result.Status != ActionResultStatus.Succeeded || result.Packages.Count != 1 ||
                result.Packages[0].Status != PackageOutcomeStatus.Planned || !result.Packages[0].Id.Equals(candidate.Package.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The live rehearsal dry-run result is not the expected verified protocol outcome.");
            var progressPayload = await recoveryStore.ReadArtifactAsync(request.RequestId, ActionArtifactKind.Progress).ConfigureAwait(false);
            var progress = new ActionProgressCodec().ParseIncremental(progressPayload, request, ActionProgressParseState.Empty);
            if (progress.Issues.Count != 0 || progress.Records.Count == 0)
                throw new InvalidDataException("The live rehearsal progress stream is missing or malformed.");

            var refreshed = await services.Planning.RefreshAsync().ConfigureAwait(false);
            var postRefresh = refreshed.Packages.Count(item => item.Package.Id.Equals(candidate.Package.Id, StringComparison.OrdinalIgnoreCase)) == 1;
            var trustedWinGet = await new WindowsWinGetResolver().ResolveAsync().ConfigureAwait(false);
            if (!trustedWinGet.Trusted) throw new InvalidOperationException("The live rehearsal host did not resolve a trusted WinGet executable.");

            return new(result.Status.ToString(), candidate.Package.Id, candidate.Action.ToString(), candidate.InstalledVersion,
                candidate.AvailableVersion, progress.Records.Count, processId,
                WorkerIndependent: true, RecoveredFromFreshStore: true, postRefresh, trustedWinGet.Trusted, MutationPerformed: false);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
