using System.Diagnostics;
using System.Security.Cryptography;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Infrastructure.Windows.Files;

namespace AVWorkstationToolkit.Infrastructure.Windows.Processes;

/// <summary>Launches only the integrity-pinned compiled worker extracted from the packaged runtime.</summary>
public sealed class ProductionCompiledWorkerLauncher : ICompiledWorkerLauncher
{
    public const string WorkerFileName = "AVWorkstationToolkit.Worker.exe";
    private readonly string dataRoot;
    private readonly string applicationRoot;
    private readonly string workerPath;
    private readonly string expectedSha256;
    private readonly ActionRequestFilePolicy requestPolicy = new();

    public ProductionCompiledWorkerLauncher(string dataRoot, string applicationRoot, string expectedWorkerSha256)
    {
        this.dataRoot = ProductionRuntimePolicy.RequireDataRoot(dataRoot);
        this.applicationRoot = ProductionRuntimePolicy.RequireApplicationRoot(this.dataRoot, applicationRoot);
        expectedSha256 = RequireSha256(expectedWorkerSha256);
        workerPath = Path.Combine(this.applicationRoot, "worker", WorkerFileName);
        ValidateWorker();
    }

    public ValueTask<ICompiledWorkerSession> LaunchAsync(ActionArtifactPaths paths, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(paths);
        var requestPath = requestPolicy.ValidateExistingRequestPath(dataRoot, paths.RequestPath);
        if (!string.Equals(paths.RequestId, Path.GetFileNameWithoutExtension(requestPath), StringComparison.Ordinal))
            throw new ActionRequestValidationException(ActionRequestFailure.InvalidRequestId, "The worker request identity does not match its canonical path.");
        ValidateWorker();
        var start = CreateStartInfo(requestPath);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the packaged compiled worker.");
        return ValueTask.FromResult<ICompiledWorkerSession>(new WorkerSession(process));
    }

    internal ProcessStartInfo CreateStartInfo(string requestPath)
    {
        var start = new ProcessStartInfo
        {
            FileName = workerPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(workerPath)!
        };
        start.ArgumentList.Add("--production");
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(dataRoot);
        start.ArgumentList.Add("--request");
        start.ArgumentList.Add(requestPath);
        start.ArgumentList.Add("--application-root");
        start.ArgumentList.Add(applicationRoot);
        return start;
    }

    private void ValidateWorker()
    {
        if (!File.Exists(workerPath) ||
            !string.Equals(Path.GetFileName(workerPath), WorkerFileName, StringComparison.Ordinal) ||
            (File.GetAttributes(workerPath) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new FileNotFoundException("The exact packaged compiled worker is missing or unsafe.", workerPath);
        using var workerStream = File.OpenRead(workerPath);
        var actual = Convert.ToHexString(SHA256.HashData(workerStream));
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expectedSha256)))
            throw new InvalidDataException("The packaged compiled worker failed its embedded integrity check.");
        var metadata = FileVersionInfo.GetVersionInfo(workerPath);
        if (!string.Equals(metadata.ProductName, "AV Workstation Toolkit compiled worker", StringComparison.Ordinal))
            throw new InvalidDataException("The packaged worker identity is not the reviewed production host.");
    }

    private static string RequireSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit) ? value.ToUpperInvariant() :
            throw new ArgumentException("The packaged worker SHA-256 is invalid.", nameof(value));

    private sealed class WorkerSession(Process process) : ICompiledWorkerSession
    {
        public int ProcessId => process.Id;
        public bool HasExited => process.HasExited;
        public int? ExitCode => process.HasExited ? process.ExitCode : null;
        public void Dispose() => process.Dispose();
    }
}
