using System.Text;
using AVWorkstationToolkit.Application.Actions;

namespace AVWorkstationToolkit.Infrastructure.Windows.Files;

/// <summary>
/// Non-shipping file-protocol primitive. It has no default data root and can
/// act only beneath the explicit root supplied by its caller. The compiled App
/// does not compose this type.
/// </summary>
public sealed class ActionProtocolStore : IActionProtocolStore
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false, true);
    private readonly string dataRoot;
    private readonly ActionArtifactPathPolicy pathPolicy;
    private readonly ActionRequestCodec requestCodec;

    public ActionProtocolStore(string explicitDataRoot, ActionArtifactPathPolicy? pathPolicy = null, ActionRequestCodec? requestCodec = null)
    {
        dataRoot = ActionArtifactPathPolicy.RequireAbsoluteNonRoot(explicitDataRoot, "explicit data root");
        this.pathPolicy = pathPolicy ?? new ActionArtifactPathPolicy();
        this.requestCodec = requestCodec ?? new ActionRequestCodec();
    }

    public ActionArtifactPaths GetPaths(string requestId) => pathPolicy.GetPaths(dataRoot, requestId);

    public async Task<ActionRequest> ReadRequestAsync(string requestId, CancellationToken cancellationToken = default)
    {
        var payload = await ReadArtifactAsync(requestId, ActionArtifactKind.Request, cancellationToken).ConfigureAwait(false);
        return requestCodec.Parse(payload, requestId);
    }

    public async Task<ActionArtifactPaths> PersistRequestAsync(AuthorizedActionRequest authorizedRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorizedRequest);
        ActionRequestRules.Validate(authorizedRequest.Request);
        if (authorizedRequest.Packages.Count == 0)
            throw new ActionRequestValidationException(ActionRequestFailure.PackageNotEligible, "An authorized request must contain validated plan packages.");
        var authorizedIds = authorizedRequest.Packages.Select(item => item.Package.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requestedIds = authorizedRequest.Request.PackageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedAction = authorizedRequest.Request.Action == ManagedRequestAction.Install
            ? AVWorkstationToolkit.Domain.Catalog.PackageAction.Install
            : AVWorkstationToolkit.Domain.Catalog.PackageAction.Update;
        if (authorizedRequest.Packages.Count != authorizedIds.Count || !requestedIds.SetEquals(authorizedIds) || authorizedRequest.Packages.Any(item =>
                item.Package.Authority != AVWorkstationToolkit.Domain.Catalog.CatalogAuthority.ManagedWinGet ||
                item.Package.Provider != AVWorkstationToolkit.Domain.Catalog.ProviderKind.WinGet ||
                item.Action != expectedAction))
            throw new ActionRequestValidationException(ActionRequestFailure.PackageNotEligible, "Every request package must be represented by an authorized plan package.");

        pathPolicy.ValidateOrCreateRequestsRoot(dataRoot);
        var paths = GetPaths(authorizedRequest.Request.RequestId);
        if (File.Exists(paths.RequestPath)) throw new IOException("The action request already exists and will not be overwritten.");
        var payload = requestCodec.Serialize(authorizedRequest.Request);
        if (payload.Length > ActionRequestRules.MaximumPayloadBytes)
            throw new ActionRequestValidationException(ActionRequestFailure.Oversized, "The action request exceeds the maximum permitted size.");

        var requestsRoot = Path.GetDirectoryName(paths.RequestPath)!;
        var temporaryPath = Path.Combine(requestsRoot, $".{authorizedRequest.Request.RequestId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, paths.RequestPath, overwrite: false);
            pathPolicy.ValidateExistingArtifact(dataRoot, authorizedRequest.Request.RequestId, ActionArtifactKind.Request);
            return paths;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public async Task<bool> CreateCancellationMarkerAsync(string requestId, CancellationToken cancellationToken = default)
    {
        pathPolicy.ValidateOrCreateRequestsRoot(dataRoot);
        pathPolicy.ValidateExistingArtifact(dataRoot, requestId, ActionArtifactKind.Request);
        var path = pathPolicy.GetPath(dataRoot, requestId, ActionArtifactKind.Cancellation);
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 256, FileOptions.Asynchronous | FileOptions.WriteThrough);
            var payload = Utf8WithoutBom.GetBytes("Stop requested by user.\r\n");
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
    }

    public async Task<byte[]> ReadArtifactAsync(string requestId, ActionArtifactKind kind, CancellationToken cancellationToken = default)
    {
        var path = pathPolicy.ValidateExistingArtifact(dataRoot, requestId, kind);
        var maximum = ActionArtifactPathPolicy.MaximumBytes(kind);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximum) throw new ActionProtocolValidationException(ActionProtocolFailure.Oversized, $"The {kind} artifact exceeds its read limit.");
        using var output = new MemoryStream(checked((int)Math.Min(stream.Length, maximum)));
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length > maximum - read) throw new ActionProtocolValidationException(ActionProtocolFailure.Oversized, $"The {kind} artifact exceeds its read limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    public async Task<byte[]?> TryReadArtifactAsync(string requestId, ActionArtifactKind kind, CancellationToken cancellationToken = default)
    {
        var path = pathPolicy.GetPath(dataRoot, requestId, kind);
        if (!File.Exists(path)) return null;
        return await ReadArtifactAsync(requestId, kind, cancellationToken).ConfigureAwait(false);
    }
}
