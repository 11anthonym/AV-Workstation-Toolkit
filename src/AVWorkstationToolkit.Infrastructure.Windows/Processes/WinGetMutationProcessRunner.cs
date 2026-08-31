using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Workers;
using AVWorkstationToolkit.Infrastructure.Windows.WinGet;

namespace AVWorkstationToolkit.Infrastructure.Windows.Processes;

public sealed record WinGetMutationProcessResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut,
    bool Cancelled,
    bool OutputLimitExceeded,
    ProviderFailureKind Failure);

public interface IWinGetMutationProcessRunner
{
    Task<WinGetMutationProcessResult> RunAsync(PackageExecutionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Non-shipping, exact-ID WinGet mutation boundary. The executable is supplied
/// only by the trusted Desktop App Installer resolver and every argument is
/// generated from a typed package request.
/// </summary>
public sealed class WinGetMutationProcessRunner : IWinGetMutationProcessRunner
{
    private const int OutputCharacterLimit = 2 * 1024 * 1024;
    private readonly IWinGetResolver resolver;
    private readonly IWinGetMutationProcessHost processHost;
    private readonly TimeSpan timeout;
    private readonly Func<bool> elevationCheck;

    public WinGetMutationProcessRunner(IWinGetResolver resolver, TimeSpan? timeout = null)
        : this(resolver, new SystemWinGetMutationProcessHost(), timeout, IsElevated)
    {
    }

    internal WinGetMutationProcessRunner(
        IWinGetResolver resolver,
        IWinGetMutationProcessHost processHost,
        TimeSpan? timeout = null,
        Func<bool>? elevationCheck = null)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        this.processHost = processHost ?? throw new ArgumentNullException(nameof(processHost));
        this.timeout = timeout ?? TimeSpan.FromMinutes(30);
        this.elevationCheck = elevationCheck ?? IsElevated;
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(timeout), "WinGet mutation timeout must be between zero and thirty minutes.");
    }

    public async Task<WinGetMutationProcessResult> RunAsync(PackageExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var arguments = ManagedWinGetArgumentPolicy.Create(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (elevationCheck()) return Failure(126, "Compiled WinGet mutation must run as a standard user.", ProviderFailureKind.TrustFailure);

        var resolved = await resolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (!resolved.Trusted || !WindowsWinGetResolver.IsExpectedExecutablePath(resolved.ExecutablePath))
            return Failure(127, resolved.Detail, ProviderFailureKind.TrustFailure);

        return await processHost.RunAsync(resolved.ExecutablePath, arguments, timeout, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static WinGetMutationProcessResult Failure(int exitCode, string detail, ProviderFailureKind failure) =>
        new(exitCode, string.Empty, DiagnosticText.Sanitize(detail), false, false, false, failure);

    internal interface IWinGetMutationProcessHost
    {
        Task<WinGetMutationProcessResult> RunAsync(
            string trustedExecutablePath,
            IReadOnlyList<string> reviewedArguments,
            TimeSpan timeout,
            CancellationToken cancellationToken);
    }

    private sealed class SystemWinGetMutationProcessHost : IWinGetMutationProcessHost
    {
        public async Task<WinGetMutationProcessResult> RunAsync(
            string trustedExecutablePath,
            IReadOnlyList<string> reviewedArguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = trustedExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false
            };
            foreach (var argument in reviewedArguments) startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start()) return Failure(1, "Trusted WinGet process could not be started.", ProviderFailureKind.ExecutionFailed);
            var stdout = ReadBoundedAsync(process.StandardOutput, OutputCharacterLimit);
            var stderr = ReadBoundedAsync(process.StandardError, OutputCharacterLimit);
            var processExit = process.WaitForExitAsync();
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            var stopSignal = Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            try
            {
                var completion = Task.WhenAll(processExit, stdout, stderr);
                var completed = await Task.WhenAny(completion, stopSignal).ConfigureAwait(false);
                if (completed == stopSignal) throw new OperationCanceledException(linked.Token);
                await completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await TerminateAsync(process).ConfigureAwait(false);
                var cancelled = cancellationToken.IsCancellationRequested;
                return new(-1, string.Empty, string.Empty, !cancelled, cancelled, false,
                    cancelled ? ProviderFailureKind.Cancelled : ProviderFailureKind.TimedOut);
            }
            catch (OutputLimitException)
            {
                await TerminateAsync(process).ConfigureAwait(false);
                return new(-1, string.Empty, string.Empty, false, false, true, ProviderFailureKind.OutputLimitExceeded);
            }

            return new(process.ExitCode, DiagnosticText.Sanitize(stdout.Result), DiagnosticText.Sanitize(stderr.Result),
                false, false, false, process.ExitCode == 0 ? ProviderFailureKind.None : ProviderFailureKind.ExecutionFailed);
        }

        private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters)
        {
            var buffer = new char[4096];
            var output = new StringBuilder(Math.Min(maximumCharacters, 65536));
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (read == 0) return output.ToString();
                if (output.Length + read > maximumCharacters) throw new OutputLimitException();
                output.Append(buffer, 0, read);
            }
        }

        private static async Task TerminateAsync(Process process)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            try { await process.WaitForExitAsync().ConfigureAwait(false); }
            catch (InvalidOperationException) { }
        }

        private sealed class OutputLimitException : Exception;
    }
}

/// <summary>
/// Maps the bounded process result into the worker's typed execution contract.
/// The production compiled worker composes this only after strict request and live-plan authorization.
/// </summary>
public sealed class WinGetPackageActionExecutor(IWinGetMutationProcessRunner runner) : IPackageActionExecutor
{
    private readonly IWinGetMutationProcessRunner runner = runner ?? throw new ArgumentNullException(nameof(runner));

    public async ValueTask<PackageExecutionResult> ExecuteAsync(
        PackageExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Cancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException("The WinGet process was cancelled outside the cooperative package boundary.");
        }
        if (result.TimedOut)
            return PackageExecutionResult.Timeout with { StandardOutput = result.StdOut, StandardError = result.StdErr };
        if (result.ExitCode != 0 || result.Failure != ProviderFailureKind.None)
        {
            var exitCode = result.ExitCode == 0 ? -1 : result.ExitCode;
            return PackageExecutionResult.Failure(exitCode) with { StandardOutput = result.StdOut, StandardError = result.StdErr };
        }
        return PackageExecutionResult.Success with { StandardOutput = result.StdOut, StandardError = result.StdErr };
    }
}
