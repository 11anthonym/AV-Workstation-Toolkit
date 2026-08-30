using System.Diagnostics;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Infrastructure.Windows.Files;

namespace AVWorkstationToolkit.Infrastructure.Windows.Processes;

/// <summary>
/// Starts the exact non-shipping compiled worker in an explicit developer-only
/// live rehearsal mode. The caller cannot supply an executable or argument list.
/// </summary>
public sealed class CompiledLiveRehearsalWorkerLauncher : ICompiledWorkerLauncher
{
    private const string WorkerFileName = "AVWorkstationToolkit.Worker.exe";
    private readonly string repositoryRoot;
    private readonly string dataRoot;
    private readonly string workerPath;
    private readonly ActionRequestFilePolicy requestPolicy = new();

    public CompiledLiveRehearsalWorkerLauncher(string repositoryRoot, string explicitRehearsalRoot)
    {
        this.repositoryRoot = RequireRepositoryRoot(repositoryRoot);
        dataRoot = LiveRehearsalRootPolicy.RequireExisting(explicitRehearsalRoot);
        workerPath = Path.GetFullPath(Path.Combine(this.repositoryRoot, "src", "AVWorkstationToolkit.Worker", "bin", "Release",
            "net10.0-windows", WorkerFileName));
        if (!File.Exists(workerPath) || !string.Equals(Path.GetFileName(workerPath), WorkerFileName, StringComparison.Ordinal) ||
            (File.GetAttributes(workerPath) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new FileNotFoundException("The exact compiled rehearsal worker has not been built or is unsafe.", workerPath);
        RejectReparseChain(this.repositoryRoot, workerPath);
        var metadata = FileVersionInfo.GetVersionInfo(workerPath);
        if (!string.Equals(metadata.ProductName, "AV Workstation Toolkit non-shipping compiled worker", StringComparison.Ordinal))
            throw new InvalidDataException("The compiled rehearsal worker identity is not the reviewed host.");
    }

    public ValueTask<ICompiledWorkerSession> LaunchAsync(ActionArtifactPaths paths, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = CreateStartInfo(paths);
        var process = Process.Start(start) ?? throw new InvalidOperationException("The exact compiled live rehearsal worker did not start.");
        return ValueTask.FromResult<ICompiledWorkerSession>(new CompiledWorkerSession(process));
    }

    internal ProcessStartInfo CreateStartInfo(ActionArtifactPaths paths)
    {
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
        start.ArgumentList.Add("--live-rehearsal");
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(dataRoot);
        start.ArgumentList.Add("--request");
        start.ArgumentList.Add(requestPath);
        start.ArgumentList.Add("--repository-root");
        start.ArgumentList.Add(repositoryRoot);
        return start;
    }

    private static string RequireRepositoryRoot(string value)
    {
        var root = ActionArtifactPathPolicy.RequireAbsoluteNonRoot(value, "repository root");
        if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, "VERSION")) ||
            !File.Exists(Path.Combine(root, "scripts", "AppProfiles.psd1")) ||
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new DirectoryNotFoundException("The live rehearsal repository root is missing or unsafe.");
        return root;
    }

    private static void RejectReparseChain(string root, string candidate)
    {
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = Path.GetFullPath(candidate);
        if (!current.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The compiled rehearsal worker escaped the repository build root.");
        while (!string.Equals(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), boundary, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The compiled rehearsal worker path contains an unsupported reparse point.");
            current = Directory.GetParent(current)?.FullName ?? throw new IOException("The compiled rehearsal worker path is not contained.");
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
