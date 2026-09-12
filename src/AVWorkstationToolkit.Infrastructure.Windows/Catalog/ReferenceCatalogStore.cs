using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using AVWorkstationToolkit.Application.Compatibility;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Infrastructure.Windows.Catalog;

public sealed class ReferenceCatalogStore(
    string embeddedApplicationRoot,
    string dataRoot,
    ReferenceCatalogBundleVerifier verifier,
    IReferenceCatalogChannelClient? channel = null) : IReferenceCatalogUpdateService
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions StateOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 8
    };
    private readonly string embeddedApplicationRoot = RequireRoot(embeddedApplicationRoot, "application");
    private readonly string root = Path.Combine(RequireRoot(dataRoot, "data"), "ReferenceCatalog");
    private readonly ReferenceCatalogBundleVerifier verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    private readonly IReferenceCatalogChannelClient? channel = channel;
    private VerifiedReferenceCatalogBundle? pending;

    public ReferenceCatalogUpdateStatus Status { get; private set; } = new(ReferenceCatalogUpdateState.Idle, 0, "Embedded", 0, string.Empty, "Embedded reference catalog is active.");

    public ReferenceCatalogSet LoadActiveOrEmbedded()
    {
        StateDocument state;
        try { state = ReadState(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CatalogValidationException or JsonException)
        {
            QuarantineState();
            state = new(0, 0, 0, DateTimeOffset.MinValue);
        }
        foreach (var revision in new[] { state.ActiveRevision, state.PreviousRevision }.Where(value => value > 0).Distinct())
        {
            try
            {
                var verified = verifier.VerifyDirectory(CatalogDirectory(revision));
                if (verified.Manifest.Revision != revision) throw new CatalogValidationException("Stored reference catalog revision does not match its directory.");
                if (revision != state.ActiveRevision) WriteState(new(revision, 0, state.SuppressedRevision, DateTimeOffset.UtcNow));
                Status = new(ReferenceCatalogUpdateState.Current, revision, verified.Manifest.CatalogVersion, 0, string.Empty,
                    revision == state.ActiveRevision ? "Signed reference catalog is active." : "Previous signed reference catalog restored after active-catalog validation failed.");
                return new(verified.Hardware, verified.Compatibility, new(revision, verified.Manifest.CatalogVersion, false, Status.Detail));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CatalogValidationException or CryptographicException or ReferenceCatalogRequiresNewerApplicationException)
            {
                QuarantineStoredRevision(revision);
            }
        }

        var hardware = new RepositoryHardwareIdentityCatalogLoader().Load(embeddedApplicationRoot);
        var compatibility = new RepositoryCompatibilityCatalogLoader().Load(embeddedApplicationRoot);
        Status = new(ReferenceCatalogUpdateState.Current, 0, "Embedded", 0, string.Empty, "Verified embedded reference catalog is active.");
        return new(hardware, compatibility, new(0, "Embedded", true, Status.Detail));
    }

    public async Task<ReferenceCatalogUpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (channel is null)
        {
            Status = Status with { State = ReferenceCatalogUpdateState.NotConfigured, Detail = "No public signed reference-catalog channel is configured for this private repository." };
            return Status;
        }
        try
        {
            Status = Status with { State = ReferenceCatalogUpdateState.Checking, Detail = "Checking the signed reference-catalog channel…" };
            var available = await channel.GetLatestAsync(Status.CurrentRevision, cancellationToken).ConfigureAwait(false);
            if (available is null)
            {
                pending = null;
                Status = Status with { State = ReferenceCatalogUpdateState.Current, AvailableRevision = 0, AvailableVersion = string.Empty, Detail = "The signed reference catalog is current.", Changes = null };
                return Status;
            }
            using var stream = new MemoryStream(available.BundleBytes, writable: false);
            var verified = verifier.VerifyArchive(stream);
            if (verified.Manifest.Revision != available.Revision || verified.Manifest.PreviousRevision != available.PreviousRevision ||
                !string.Equals(verified.Manifest.CatalogVersion, available.Version, StringComparison.Ordinal) ||
                !string.Equals(verified.Manifest.MinimumAppVersion, available.MinimumAppVersion, StringComparison.Ordinal))
                throw new CatalogValidationException("Signed channel metadata does not match the signed reference catalog bundle.");
            var state = ReadState();
            ValidateRevisionChain(verified.Manifest, state);
            pending = verified;
            Status = new(ReferenceCatalogUpdateState.UpdateAvailable, state.ActiveRevision, Status.CurrentVersion,
                verified.Manifest.Revision, verified.Manifest.CatalogVersion, "A signed descriptive reference-catalog update is available.", verified.Changes);
        }
        catch (ReferenceCatalogRequiresNewerApplicationException exception)
        {
            pending = null;
            Status = Status with { State = ReferenceCatalogUpdateState.RequiresNewerApp, Detail = exception.Message };
        }
        catch (Exception exception) when (exception is HttpRequestException ||
            (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            pending = null;
            Status = Status with { State = ReferenceCatalogUpdateState.Offline, Detail = "The signed reference-catalog channel is unavailable." };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CatalogValidationException or CryptographicException or InvalidDataException)
        {
            pending = null;
            Status = Status with { State = ReferenceCatalogUpdateState.Rejected, Detail = $"Reference catalog rejected: {exception.Message}" };
        }
        return Status;
    }

    public Task<ReferenceCatalogUpdateStatus> InstallAvailableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pending is null)
        {
            Status = Status with { State = ReferenceCatalogUpdateState.Rejected, Detail = "No verified downloaded catalog is pending activation." };
            return Task.FromResult(Status);
        }
        try
        {
            Status = Status with { State = ReferenceCatalogUpdateState.Validating, Detail = "Revalidating and activating the signed reference catalog…" };
            var state = ReadState();
            ValidateRevisionChain(pending.Manifest, state);
            Activate(pending, state);
            Status = new(ReferenceCatalogUpdateState.Completed, pending.Manifest.Revision, pending.Manifest.CatalogVersion, 0, string.Empty,
                "Signed reference catalog activated. Restart AV Workstation Toolkit to use it.", pending.Changes);
            pending = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CatalogValidationException or CryptographicException or InvalidDataException)
        {
            pending = null;
            Status = Status with { State = ReferenceCatalogUpdateState.Rejected, Detail = $"Reference catalog activation failed: {exception.Message}" };
        }
        return Task.FromResult(Status);
    }

    public Task<ReferenceCatalogUpdateStatus> ImportAsync(string bundlePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Status = Status with { State = ReferenceCatalogUpdateState.Validating, Detail = "Validating signed reference catalog…" };
            pending = verifier.VerifyFile(bundlePath);
            var state = ReadState();
            ValidateRevisionChain(pending.Manifest, state);
            Activate(pending, state);
            Status = new(ReferenceCatalogUpdateState.Completed, pending.Manifest.Revision, pending.Manifest.CatalogVersion, 0, string.Empty,
                "Signed reference catalog imported and activated.", pending.Changes);
            pending = null;
        }
        catch (ReferenceCatalogRequiresNewerApplicationException exception)
        {
            pending = null;
            Status = Status with { State = ReferenceCatalogUpdateState.RequiresNewerApp, Detail = exception.Message };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CatalogValidationException or CryptographicException or InvalidDataException)
        {
            pending = null;
            Status = Status with { State = ReferenceCatalogUpdateState.Rejected, Detail = $"Reference catalog rejected: {exception.Message}" };
        }
        return Task.FromResult(Status);
    }

    private void Activate(VerifiedReferenceCatalogBundle bundle, StateDocument previous)
    {
        EnsureDirectories();
        var final = CatalogDirectory(bundle.Manifest.Revision);
        if (Directory.Exists(final)) throw new CatalogValidationException("Reference catalog revision already exists.");
        var staging = Path.Combine(root, "staging", $"{bundle.Manifest.Revision}-{Guid.NewGuid():N}");
        var moved = false;
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (name, bytes) in bundle.Files)
            {
                var path = Path.Combine(staging, name);
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384, FileOptions.WriteThrough);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            _ = verifier.VerifyDirectory(staging);
            Directory.Move(staging, final);
            moved = true;
            WriteState(new(bundle.Manifest.Revision, previous.ActiveRevision, previous.SuppressedRevision, DateTimeOffset.UtcNow));
        }
        catch
        {
            if (moved && Directory.Exists(final))
            {
                var target = Path.Combine(root, "quarantine", $"{bundle.Manifest.Revision}-activation-{Guid.NewGuid():N}");
                Directory.Move(final, target);
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static void ValidateRevisionChain(ReferenceCatalogBundleManifest manifest, StateDocument state)
    {
        if (manifest.Revision <= state.ActiveRevision)
            throw new CatalogValidationException("Reference catalog rollback or same-revision activation is not permitted.");
        if (state.ActiveRevision > 0 && manifest.PreviousRevision != state.ActiveRevision)
            throw new CatalogValidationException("Reference catalog PreviousRevision does not match the active revision.");
    }

    private StateDocument ReadState()
    {
        var path = Path.Combine(root, "state.json");
        if (!File.Exists(path)) return new(0, 0, 0, DateTimeOffset.MinValue);
        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > 16_384 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new CatalogValidationException("Reference catalog state file is invalid.");
            var json = File.ReadAllText(path, Utf8);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new CatalogValidationException("Reference catalog state must be a JSON object.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!names.Add(property.Name)) throw new CatalogValidationException($"Reference catalog state repeats JSON property '{property.Name}'.");
            if (!names.SetEquals(["ActiveRevision", "PreviousRevision", "SuppressedRevision", "LastCheckUtc"]))
                throw new CatalogValidationException("Reference catalog state fields are incomplete.");
            var state = JsonSerializer.Deserialize<StateDocument>(json, StateOptions)
                ?? throw new CatalogValidationException("Reference catalog state file is empty.");
            if (state.ActiveRevision < 0 || state.PreviousRevision < 0 || state.SuppressedRevision < 0 ||
                (state.ActiveRevision > 0 && state.ActiveRevision == state.PreviousRevision))
                throw new CatalogValidationException("Reference catalog state revisions are invalid.");
            return state;
        }
        catch (JsonException exception) { throw new CatalogValidationException($"Reference catalog state JSON is invalid: {exception.Message}"); }
        catch (DecoderFallbackException exception) { throw new CatalogValidationException($"Reference catalog state is not valid UTF-8: {exception.Message}"); }
    }

    private void WriteState(StateDocument state)
    {
        EnsureDirectories();
        var path = Path.Combine(root, "state.json");
        var temporary = Path.Combine(root, $"state.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, Utf8))
            {
                writer.Write(JsonSerializer.Serialize(state, StateOptions));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(root);
        RejectReparse(root);
        foreach (var name in new[] { "catalogs", "staging", "quarantine" })
        {
            var path = Path.Combine(root, name);
            Directory.CreateDirectory(path);
            RejectReparse(path);
        }
    }

    private string CatalogDirectory(long revision) => Path.Combine(root, "catalogs", revision.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private void QuarantineStoredRevision(long revision)
    {
        var source = CatalogDirectory(revision);
        if (!Directory.Exists(source)) return;
        EnsureDirectories();
        var target = Path.Combine(root, "quarantine", $"{revision}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
        if (!Directory.Exists(target)) Directory.Move(source, target);
    }

    private void QuarantineState()
    {
        var source = Path.Combine(root, "state.json");
        if (!File.Exists(source)) return;
        EnsureDirectories();
        var target = Path.Combine(root, "quarantine", $"state-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
        File.Move(source, target);
    }

    private static string RequireRoot(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) throw new IOException($"Reference catalog {description} root must be absolute.");
        var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Reference catalog {description} root is unavailable or is a reparse point.");
        return full;
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Reference catalog storage cannot use reparse points.");
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StateDocument(long ActiveRevision, long PreviousRevision, long SuppressedRevision, DateTimeOffset LastCheckUtc);
}
