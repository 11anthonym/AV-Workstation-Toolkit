using System.Diagnostics;
using System.Text;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Infrastructure.Windows.WinGet;

namespace AVWorkstationToolkit.Infrastructure.Windows.Processes;

public sealed class WinGetReadOnlyProcessRunner : IWinGetReadOnlyProcessRunner
{
    private const int OutputCharacterLimit = 2 * 1024 * 1024;
    private const int ExportByteLimit = 16 * 1024 * 1024;
    private readonly IWinGetResolver resolver;
    private readonly TimeSpan timeout;

    public WinGetReadOnlyProcessRunner(IWinGetResolver resolver, TimeSpan? timeout = null)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        this.timeout = timeout ?? TimeSpan.FromSeconds(90);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Read-only WinGet timeout must be between zero and five minutes.");
    }

    public async Task<WinGetProcessResult> RunAsync(WinGetReadOnlyOperation operation, CancellationToken cancellationToken = default)
    {
        var resolved = await resolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (!resolved.Trusted || !WindowsWinGetResolver.IsExpectedExecutablePath(resolved.ExecutablePath))
            return Failure(127, resolved.Detail, ProviderFailureKind.TrustFailure);

        var exportPath = operation == WinGetReadOnlyOperation.InstalledInventory
            ? Path.Combine(Path.GetTempPath(), $"AVWorkstationToolkit-winget-export-{Guid.NewGuid():N}.json")
            : string.Empty;
        try
        {
            var startInfo = CreateStartInfo(resolved.ExecutablePath, operation, exportPath);
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
                return new(-1, string.Empty, string.Empty, string.Empty, !cancelled, cancelled, false,
                    cancelled ? ProviderFailureKind.Cancelled : ProviderFailureKind.TimedOut);
            }
            catch (OutputLimitException)
            {
                await TerminateAsync(process).ConfigureAwait(false);
                return new(-1, string.Empty, string.Empty, string.Empty, false, false, true, ProviderFailureKind.OutputLimitExceeded);
            }

            var exportJson = string.Empty;
            if (operation == WinGetReadOnlyOperation.InstalledInventory && File.Exists(exportPath))
            {
                var file = new FileInfo(exportPath);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > ExportByteLimit)
                    return new(process.ExitCode, DiagnosticText.Sanitize(stdout.Result), DiagnosticText.Sanitize(stderr.Result), string.Empty,
                        false, false, true, ProviderFailureKind.OutputLimitExceeded);
                exportJson = await File.ReadAllTextAsync(exportPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            }
            return new(process.ExitCode, DiagnosticText.Sanitize(stdout.Result), DiagnosticText.Sanitize(stderr.Result), exportJson,
                false, false, false, process.ExitCode == 0 ? ProviderFailureKind.None : ProviderFailureKind.ExecutionFailed);
        }
        finally
        {
            if (exportPath.Length > 0 && File.Exists(exportPath)) File.Delete(exportPath);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string executable, WinGetReadOnlyOperation operation, string exportPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false
        };
        var arguments = WinGetReadOnlyInvocationPolicy.GetArguments(operation, exportPath);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
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

    private static WinGetProcessResult Failure(int exitCode, string detail, ProviderFailureKind failure) =>
        new(exitCode, string.Empty, DiagnosticText.Sanitize(detail), string.Empty, false, false, false, failure);

    private sealed class OutputLimitException : Exception;
}

public static class WinGetReadOnlyInvocationPolicy
{
    public static IReadOnlyList<string> GetArguments(WinGetReadOnlyOperation operation, string exportPath = "")
    {
        return operation switch
        {
            WinGetReadOnlyOperation.Version => ["--version"],
            WinGetReadOnlyOperation.InstalledInventory => InstalledArguments(exportPath),
            WinGetReadOnlyOperation.AvailableUpdates => ["list", "--upgrade-available", "--source", "winget", "--disable-interactivity", "--accept-source-agreements"],
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    private static IReadOnlyList<string> InstalledArguments(string exportPath)
    {
        if (string.IsNullOrWhiteSpace(exportPath) || !Path.IsPathFullyQualified(exportPath))
            throw new ArgumentException("Installed inventory requires a generated absolute export path.", nameof(exportPath));
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(exportPath);
        if (!fullPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).StartsWith("AVWorkstationToolkit-winget-export-", StringComparison.Ordinal) ||
            !Path.GetExtension(fullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Installed inventory export path violates the fixed temporary-file policy.", nameof(exportPath));
        return ["export", "--output", fullPath, "--source", "winget", "--include-versions", "--accept-source-agreements", "--disable-interactivity"];
    }
}
