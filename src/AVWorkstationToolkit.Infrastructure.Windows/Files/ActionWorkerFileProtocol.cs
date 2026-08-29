using System.Text;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Workers;

namespace AVWorkstationToolkit.Infrastructure.Windows.Files;

/// <summary>
/// Non-shipping worker-side file protocol bound to one injected test root and
/// one RequestId. It exposes no default production root and no arbitrary path.
/// </summary>
public sealed class ActionWorkerFileProtocol : IActionWorkerProtocol, IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false, true);
    private readonly string dataRoot;
    private readonly ActionArtifactPathPolicy pathPolicy;
    private readonly ActionProgressCodec progressCodec;
    private readonly ActionResultCodec resultCodec;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private bool initialized;
    private bool progressCreated;
    private bool finalWritten;

    public ActionWorkerFileProtocol(
        string explicitDataRoot,
        string requestId,
        ActionArtifactPathPolicy? pathPolicy = null,
        ActionProgressCodec? progressCodec = null,
        ActionResultCodec? resultCodec = null)
    {
        dataRoot = ActionArtifactPathPolicy.RequireAbsoluteNonRoot(explicitDataRoot, "explicit worker test root");
        this.pathPolicy = pathPolicy ?? new ActionArtifactPathPolicy();
        this.progressCodec = progressCodec ?? new ActionProgressCodec();
        this.resultCodec = resultCodec ?? new ActionResultCodec();
        Paths = this.pathPolicy.GetPaths(dataRoot, requestId);
    }

    public ActionArtifactPaths Paths { get; }

    public ValueTask InitializeAsync(ActionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireRequest(request);
        pathPolicy.ValidateExistingArtifact(dataRoot, request.RequestId, ActionArtifactKind.Request);
        RejectStaleArtifact(ActionArtifactKind.Progress, Paths.ProgressPath);
        RejectStaleArtifact(ActionArtifactKind.Result, Paths.ResultPath);
        RejectStaleArtifact(ActionArtifactKind.WinGetLog, Paths.WinGetLogPath);
        if (File.Exists(Paths.CancellationPath))
            pathPolicy.ValidateExistingArtifact(dataRoot, request.RequestId, ActionArtifactKind.Cancellation);
        initialized = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> IsCancellationRequestedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireInitialized();
        if (!File.Exists(Paths.CancellationPath)) return ValueTask.FromResult(false);
        pathPolicy.ValidateExistingArtifact(dataRoot, Paths.RequestId, ActionArtifactKind.Cancellation);
        return ValueTask.FromResult(true);
    }

    public async ValueTask AppendProgressAsync(
        ActionRequest request,
        ActionProgressRecord record,
        CancellationToken cancellationToken = default)
    {
        RequireInitialized();
        RequireRequest(request);
        var payload = progressCodec.Serialize(record, request);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var mode = progressCreated ? FileMode.Append : FileMode.CreateNew;
            if (progressCreated)
                pathPolicy.ValidateExistingArtifact(dataRoot, request.RequestId, ActionArtifactKind.Progress);
            await using var stream = new FileStream(Paths.ProgressPath, mode, FileAccess.Write, FileShare.Read, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            if (stream.Length > ActionProtocolLimits.MaximumProgressBytes - payload.Length - 1)
                throw new ActionProtocolValidationException(ActionProtocolFailure.Oversized, "Progress output would exceed the bounded protocol file size.");
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            progressCreated = true;
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask PersistFinalResultAsync(
        ActionRequest request,
        ActionFinalResult result,
        CancellationToken cancellationToken = default)
    {
        RequireInitialized();
        RequireRequest(request);
        if (finalWritten || File.Exists(Paths.ResultPath))
            throw new IOException("The final action result already exists and will not be overwritten.");
        var payload = resultCodec.Serialize(result, request, Paths);
        var requestsRoot = Path.GetDirectoryName(Paths.ResultPath)!;
        var temporaryPath = Path.Combine(requestsRoot, $".{request.RequestId}.result.{Guid.NewGuid():N}.tmp");
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, Paths.ResultPath, overwrite: false);
            pathPolicy.ValidateExistingArtifact(dataRoot, request.RequestId, ActionArtifactKind.Result);
            finalWritten = true;
        }
        finally
        {
            writeGate.Release();
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public ValueTask DisposeAsync()
    {
        writeGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private void RejectStaleArtifact(ActionArtifactKind kind, string path)
    {
        if (!File.Exists(path)) return;
        pathPolicy.ValidateExistingArtifact(dataRoot, Paths.RequestId, kind);
        throw new IOException($"A stale {kind} artifact already exists for this request.");
    }

    private void RequireRequest(ActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ActionRequestRules.Validate(request);
        if (!string.Equals(request.RequestId, Paths.RequestId, StringComparison.Ordinal))
            throw new ActionProtocolValidationException(ActionProtocolFailure.RequestMismatch, "The worker protocol is bound to a different RequestId.");
    }

    private void RequireInitialized()
    {
        if (!initialized) throw new InvalidOperationException("The worker file protocol has not been initialized.");
    }
}
