using AVWorkstationToolkit.Application.Actions;

namespace AVWorkstationToolkit.Infrastructure.Windows.Files;

/// <summary>
/// Derives every action-protocol path from an already validated RequestId. It
/// has no arbitrary filename or destination-path escape hatch.
/// </summary>
public sealed class ActionArtifactPathPolicy
{
    private readonly Func<string, FileAttributes> getAttributes;
    private readonly Func<string, bool> fileExists;
    private readonly Func<string, bool> directoryExists;
    private readonly Func<string, long> getLength;

    public ActionArtifactPathPolicy()
        : this(File.GetAttributes, File.Exists, Directory.Exists, path => new FileInfo(path).Length)
    {
    }

    internal ActionArtifactPathPolicy(
        Func<string, FileAttributes> getAttributes,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        Func<string, long> getLength)
    {
        this.getAttributes = getAttributes ?? throw new ArgumentNullException(nameof(getAttributes));
        this.fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
        this.directoryExists = directoryExists ?? throw new ArgumentNullException(nameof(directoryExists));
        this.getLength = getLength ?? throw new ArgumentNullException(nameof(getLength));
    }

    public string GetRequestsRoot(string dataRoot)
    {
        var fullDataRoot = RequireAbsoluteNonRoot(dataRoot, "data root");
        return Path.GetFullPath(Path.Combine(fullDataRoot, "logs", "requests"));
    }

    public ActionArtifactPaths GetPaths(string dataRoot, string requestId)
    {
        ActionRequestRules.ValidateRequestId(requestId);
        var requestsRoot = GetRequestsRoot(dataRoot);
        var names = ActionRequestArtifactNames.FromRequestId(requestId);
        return new(
            requestId,
            Path.Combine(requestsRoot, names.RequestFileName),
            Path.Combine(requestsRoot, names.ProgressFileName),
            Path.Combine(requestsRoot, names.ResultFileName),
            Path.Combine(requestsRoot, names.CancelFileName),
            Path.Combine(requestsRoot, names.WingetLogFileName));
    }

    public string GetPath(string dataRoot, string requestId, ActionArtifactKind kind)
    {
        var paths = GetPaths(dataRoot, requestId);
        return kind switch
        {
            ActionArtifactKind.Request => paths.RequestPath,
            ActionArtifactKind.Progress => paths.ProgressPath,
            ActionArtifactKind.Result => paths.ResultPath,
            ActionArtifactKind.Cancellation => paths.CancellationPath,
            ActionArtifactKind.WinGetLog => paths.WinGetLogPath,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    public string ValidateExistingArtifactPath(string dataRoot, string requestId, ActionArtifactKind kind, string artifactPath)
    {
        var fullDataRoot = RequireAbsoluteNonRoot(dataRoot, "data root");
        var logsRoot = Path.GetFullPath(Path.Combine(fullDataRoot, "logs"));
        var requestsRoot = Path.GetFullPath(Path.Combine(logsRoot, "requests"));
        var expectedPath = GetPath(fullDataRoot, requestId, kind);
        var fullArtifactPath = RequireAbsoluteNonRoot(artifactPath, "artifact path");
        if (!string.Equals(fullArtifactPath, expectedPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(fullArtifactPath), requestsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ActionProtocolValidationException(ActionProtocolFailure.RequestMismatch, "The artifact path does not match the canonical direct-child path for this request.");
        }
        if (!fileExists(fullArtifactPath)) throw new FileNotFoundException("The action protocol artifact was not found.", fullArtifactPath);
        RejectReparsePoint(fullDataRoot, "data root");
        RejectReparsePoint(logsRoot, "logs root");
        RejectReparsePoint(requestsRoot, "requests root");
        RejectReparsePoint(fullArtifactPath, "artifact");
        if ((getAttributes(fullArtifactPath) & FileAttributes.Directory) != 0)
            throw new IOException("An action protocol artifact must be a regular file.");
        var limit = MaximumBytes(kind);
        if (getLength(fullArtifactPath) > limit)
            throw new ActionProtocolValidationException(ActionProtocolFailure.Oversized, $"The {kind} artifact exceeds its {limit}-byte limit.");
        return fullArtifactPath;
    }

    public string ValidateExistingArtifact(string dataRoot, string requestId, ActionArtifactKind kind) =>
        ValidateExistingArtifactPath(dataRoot, requestId, kind, GetPath(dataRoot, requestId, kind));

    internal void ValidateOrCreateRequestsRoot(string dataRoot)
    {
        var fullDataRoot = RequireAbsoluteNonRoot(dataRoot, "data root");
        if (!directoryExists(fullDataRoot)) throw new DirectoryNotFoundException("The explicitly injected data root does not exist.");
        RejectReparsePoint(fullDataRoot, "data root");
        var logsRoot = Path.Combine(fullDataRoot, "logs");
        CreateAndValidateDirectory(logsRoot, "logs root");
        CreateAndValidateDirectory(Path.Combine(logsRoot, "requests"), "requests root");
    }

    internal static int MaximumBytes(ActionArtifactKind kind) => kind switch
    {
        ActionArtifactKind.Request => ActionRequestRules.MaximumPayloadBytes,
        ActionArtifactKind.Progress => ActionProtocolLimits.MaximumProgressBytes,
        ActionArtifactKind.Result => ActionProtocolLimits.MaximumResultBytes,
        ActionArtifactKind.Cancellation => ActionProtocolLimits.MaximumCancellationBytes,
        ActionArtifactKind.WinGetLog => ActionProtocolLimits.MaximumProgressBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private void CreateAndValidateDirectory(string path, string description)
    {
        if (!directoryExists(path)) Directory.CreateDirectory(path);
        if (!directoryExists(path)) throw new DirectoryNotFoundException($"The action protocol {description} could not be created.");
        RejectReparsePoint(path, description);
    }

    private void RejectReparsePoint(string path, string description)
    {
        if ((getAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"The action protocol {description} cannot be a reparse point.");
    }

    internal static string RequireAbsoluteNonRoot(string? path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1_024 || path != path.Trim() || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
            throw new IOException($"The action protocol {description} must be a bounded absolute path.");
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var volumeRoot = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(fullPath) || string.Equals(fullPath, volumeRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"The action protocol {description} cannot be a filesystem root.");
        return fullPath;
    }
}
