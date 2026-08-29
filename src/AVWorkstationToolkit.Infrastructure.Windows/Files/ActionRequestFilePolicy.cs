using AVWorkstationToolkit.Application.Actions;

namespace AVWorkstationToolkit.Infrastructure.Windows.Files;

/// <summary>
/// Read-only validation for the legacy worker request-file boundary. This type
/// never creates a live request file and never starts or signals a worker.
/// </summary>
public sealed class ActionRequestFilePolicy
{
    private readonly Func<string, FileAttributes> getAttributes;
    private readonly Func<string, bool> fileExists;
    private readonly Func<string, long> getLength;

    public ActionRequestFilePolicy()
        : this(File.GetAttributes, File.Exists, path => new FileInfo(path).Length)
    {
    }

    internal ActionRequestFilePolicy(
        Func<string, FileAttributes> getAttributes,
        Func<string, bool> fileExists,
        Func<string, long> getLength)
    {
        this.getAttributes = getAttributes ?? throw new ArgumentNullException(nameof(getAttributes));
        this.fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
        this.getLength = getLength ?? throw new ArgumentNullException(nameof(getLength));
    }

    public string GetRequestsRoot(string dataRoot)
    {
        var fullDataRoot = RequireAbsoluteNonRoot(dataRoot, "data root");
        return Path.GetFullPath(Path.Combine(fullDataRoot, "logs", "requests"));
    }

    public string ValidateExistingRequestPath(string dataRoot, string requestPath)
    {
        var fullDataRoot = RequireAbsoluteNonRoot(dataRoot, "data root");
        var logsRoot = Path.GetFullPath(Path.Combine(fullDataRoot, "logs"));
        var requestsRoot = Path.GetFullPath(Path.Combine(logsRoot, "requests"));
        var fullRequestPath = RequireAbsoluteNonRoot(requestPath, "request path");

        if (!string.Equals(Path.GetDirectoryName(fullRequestPath), requestsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ActionRequestValidationException(ActionRequestFailure.InvalidRequestId, "The request file must be a direct child of the application requests directory.");
        }

        var fileName = Path.GetFileName(fullRequestPath);
        if (!string.Equals(Path.GetExtension(fileName), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ActionRequestValidationException(ActionRequestFailure.InvalidRequestId, "The request file must use the .json extension.");
        }
        ActionRequestRules.ValidateRequestId(Path.GetFileNameWithoutExtension(fileName));

        if (!fileExists(fullRequestPath))
        {
            throw new FileNotFoundException("The action request file was not found.", fullRequestPath);
        }

        RejectReparsePoint(fullDataRoot, "data root");
        RejectReparsePoint(logsRoot, "logs root");
        RejectReparsePoint(requestsRoot, "requests root");
        RejectReparsePoint(fullRequestPath, "request file");

        var length = getLength(fullRequestPath);
        if (length > ActionRequestRules.MaximumPayloadBytes)
        {
            throw new ActionRequestValidationException(ActionRequestFailure.Oversized, "The action request exceeds the maximum permitted size.");
        }
        return fullRequestPath;
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

    private void RejectReparsePoint(string path, string description)
    {
        if ((getAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"The action request {description} cannot be a reparse point.");
        }
    }

    private static string RequireAbsoluteNonRoot(string? path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1_024 || path != path.Trim() || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
        {
            throw new IOException($"The action request {description} must be a bounded absolute path.");
        }

        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var volumeRoot = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(fullPath) || string.Equals(fullPath, volumeRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"The action request {description} cannot be a filesystem root.");
        }
        return fullPath;
    }
}
