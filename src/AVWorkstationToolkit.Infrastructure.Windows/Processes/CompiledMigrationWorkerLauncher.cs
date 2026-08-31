using System.Diagnostics;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Infrastructure.Windows.Files;

namespace AVWorkstationToolkit.Infrastructure.Windows.Processes;

/// <summary>
/// Starts only the fixed non-shipping worker test host from its reviewed build
/// location. Callers cannot provide an executable name or argument vector.
/// </summary>
public sealed class CompiledMigrationWorkerLauncher : ICompiledWorkerLauncher
{
    public const string WorkerFileName = "AVWorkstationToolkit.Worker.exe";
    private readonly string dataRoot;
    private readonly string workerPath;
    private readonly ActionRequestFilePolicy requestPolicy = new();

    public CompiledMigrationWorkerLauncher(string repositoryRoot, string explicitTestRoot)
    {
        var repository = RequireDirectory(repositoryRoot, "repository root");
        dataRoot = RequireDirectory(explicitTestRoot, "migration test root");
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!dataRoot.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(dataRoot).StartsWith("awt-phase11-", StringComparison.Ordinal))
            throw new IOException("The compiled action flow can use only an isolated Phase 11 temporary root.");
        workerPath = Path.GetFullPath(Path.Combine(repository, "src", "AVWorkstationToolkit.Worker", "bin", "Release",
            "net10.0-windows", WorkerFileName));
        if (!File.Exists(workerPath) || !string.Equals(Path.GetFileName(workerPath), WorkerFileName, StringComparison.Ordinal) ||
            (File.GetAttributes(workerPath) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new FileNotFoundException("The exact compiled migration worker has not been built or is unsafe.", workerPath);
        RejectReparseChain(repository, workerPath);
        var metadata = FileVersionInfo.GetVersionInfo(workerPath);
        if (!string.Equals(metadata.ProductName, "AV Workstation Toolkit compiled worker", StringComparison.Ordinal))
            throw new InvalidDataException("The compiled migration worker identity is not the reviewed test host.");
    }

    public ValueTask<ICompiledWorkerSession> LaunchAsync(ActionArtifactPaths paths, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(paths);
        var requestPath = requestPolicy.ValidateExistingRequestPath(dataRoot, paths.RequestPath);
        if (!string.Equals(paths.RequestId, Path.GetFileNameWithoutExtension(requestPath), StringComparison.Ordinal))
            throw new ActionRequestValidationException(ActionRequestFailure.InvalidRequestId, "The worker request identity does not match its canonical path.");
        var start = new ProcessStartInfo
        {
            FileName = workerPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(workerPath)!
        };
        start.ArgumentList.Add("--test-mode");
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(dataRoot);
        start.ArgumentList.Add("--request");
        start.ArgumentList.Add(requestPath);
        var process = Process.Start(start) ?? throw new InvalidOperationException("The exact compiled migration worker did not start.");
        return ValueTask.FromResult<ICompiledWorkerSession>(new CompiledWorkerSession(process));
    }

    private static string RequireDirectory(string value, string label)
    {
        var full = ActionArtifactPathPolicy.RequireAbsoluteNonRoot(value, label);
        if (!Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new DirectoryNotFoundException($"The {label} is missing or unsafe.");
        return full;
    }

    private static void RejectReparseChain(string root, string candidate)
    {
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var current = Path.GetFullPath(candidate);
        if (!current.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The compiled migration worker escaped the repository build root.");
        while (!string.Equals(current.TrimEnd(Path.DirectorySeparatorChar), boundary, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The compiled migration worker path contains an unsupported reparse point.");
            current = Directory.GetParent(current)?.FullName ?? throw new IOException("The compiled migration worker path is not contained.");
        }
    }

    private sealed class CompiledWorkerSession(Process process) : ICompiledWorkerSession
    {
        public int ProcessId => process.Id;
        public bool HasExited => process.HasExited;
        public int? ExitCode => process.HasExited ? process.ExitCode : null;
        public void Dispose() => process.Dispose();
    }
}
