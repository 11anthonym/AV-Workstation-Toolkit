using AVWorkstationToolkit.Application.Actions;

namespace AVWorkstationToolkit.Infrastructure.Windows.Files;

/// <summary>
/// Read-only validation for the legacy worker request-file boundary. This type
/// never creates a live request file and never starts or signals a worker.
/// </summary>
public sealed class ActionRequestFilePolicy
{
    private readonly ActionArtifactPathPolicy pathPolicy;

    public ActionRequestFilePolicy()
        : this(new ActionArtifactPathPolicy())
    {
    }

    internal ActionRequestFilePolicy(
        Func<string, FileAttributes> getAttributes,
        Func<string, bool> fileExists,
        Func<string, long> getLength)
        : this(new ActionArtifactPathPolicy(getAttributes, fileExists, Directory.Exists, getLength))
    {
    }

    private ActionRequestFilePolicy(ActionArtifactPathPolicy pathPolicy)
    {
        this.pathPolicy = pathPolicy;
    }

    public string GetRequestsRoot(string dataRoot)
    {
        return pathPolicy.GetRequestsRoot(dataRoot);
    }

    public string ValidateExistingRequestPath(string dataRoot, string requestPath)
    {
        var fullRequestPath = ActionArtifactPathPolicy.RequireAbsoluteNonRoot(requestPath, "request path");
        var fileName = Path.GetFileName(fullRequestPath);
        if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new ActionRequestValidationException(ActionRequestFailure.InvalidRequestId, "The request file must use the .json extension.");
        var requestId = fileName[..^5];
        ActionRequestRules.ValidateRequestId(requestId);
        try
        {
            return pathPolicy.ValidateExistingArtifactPath(dataRoot, requestId, ActionArtifactKind.Request, fullRequestPath);
        }
        catch (ActionProtocolValidationException exception) when (exception.Failure == ActionProtocolFailure.RequestMismatch)
        {
            throw new ActionRequestValidationException(ActionRequestFailure.InvalidRequestId, exception.Message, exception);
        }
        catch (ActionProtocolValidationException exception) when (exception.Failure == ActionProtocolFailure.Oversized)
        {
            throw new ActionRequestValidationException(ActionRequestFailure.Oversized, exception.Message, exception);
        }
    }

    public async Task<ActionRequest> ReadValidatedAsync(
        string dataRoot,
        string requestPath,
        ActionRequestCodec codec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(codec);
        var validatedPath = ValidateExistingRequestPath(dataRoot, requestPath);
        var expectedRequestId = Path.GetFileNameWithoutExtension(validatedPath);

        await using var stream = new FileStream(
            validatedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > ActionRequestRules.MaximumPayloadBytes)
        {
            throw new ActionRequestValidationException(ActionRequestFailure.Oversized, "The action request exceeds the maximum permitted size.");
        }

        using var buffer = new MemoryStream(capacity: checked((int)Math.Min(stream.Length, ActionRequestRules.MaximumPayloadBytes)));
        var chunk = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > ActionRequestRules.MaximumPayloadBytes)
            {
                throw new ActionRequestValidationException(ActionRequestFailure.Oversized, "The action request exceeds the maximum permitted size.");
            }
            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }

        return codec.Parse(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), expectedRequestId);
    }

}
