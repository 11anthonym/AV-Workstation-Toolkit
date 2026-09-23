using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AVWorkstationToolkit.Application.Compatibility;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Infrastructure.Windows.Catalog;

/// <summary>
/// Stores at most one downloaded signed reference catalog alongside the signed embedded baseline.
/// Descriptive catalog data can never grant execution authority, so recovery is deliberately boring:
/// anything that does not verify is deleted and the next best verified catalog is used.
/// </summary>
public sealed class ReferenceCatalogStore : IReferenceCatalogUpdateService
{
    public const string EmbeddedBundleRelativePath = "reference-catalog/AVWT-Reference-Catalog.avwtcatalog";
    public static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);
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
    private readonly string embeddedApplicationRoot;
    private readonly string root;
    private readonly ReferenceCatalogBundleVerifier verifier;
    private readonly IReferenceCatalogChannelClient? channel;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan mutationLockTimeout;
    private VerifiedReferenceCatalogBundle? pending;
    private LocalSelection? effectiveSelection;

    public ReferenceCatalogStore(
        string embeddedApplicationRoot,
        string dataRoot,
        ReferenceCatalogBundleVerifier verifier,
        IReferenceCatalogChannelClient? channel = null,
        TimeProvider? timeProvider = null,
        TimeSpan? mutationLockTimeout = null)
    {
        this.embeddedApplicationRoot = RequireExistingRoot(embeddedApplicationRoot, "application");
        root = Path.Combine(RequireDataRoot(dataRoot), "ReferenceCatalog");
        this.verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        this.channel = channel;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.mutationLockTimeout = mutationLockTimeout ?? TimeSpan.FromSeconds(5);
        if (this.mutationLockTimeout <= TimeSpan.Zero || this.mutationLockTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(mutationLockTimeout));
    }

    public ReferenceCatalogUpdateStatus Status { get; private set; } =
        new(ReferenceCatalogUpdateState.Idle, 0, "Embedded", 0, string.Empty, "Embedded reference catalog is active.");

    public ReferenceCatalogSet LoadActiveOrEmbedded()
    {
        EnsureDirectories();
        TryPerformConservativeCleanup();
        var state = ReadStateRecoveringMalformed();
        var embedded = LoadEmbeddedCandidate();
        var stored = LoadStoredCandidates();
        var selected = SelectEffectiveCatalog(stored, embedded);

        if (selected is not null)
        {
            effectiveSelection = selected;
            TryDeleteSupersededStoredRevisions(selected);
            var source = selected.IsEmbedded ? "signed embedded" : "retained signed";
            var detail = $"The {source} reference catalog revision {selected.Revision} is active.";
            if (state.ActiveRevision != selected.Revision && !selected.IsEmbedded)
                TryWriteState(new(selected.Revision, state.LastCheckUtc));
            Status = new(ReferenceCatalogUpdateState.Current, selected.Revision, selected.Version, 0, string.Empty, detail);
            return new(selected.Bundle.Hardware, selected.Bundle.Compatibility,
                new(selected.Revision, selected.Version, selected.IsEmbedded, detail));
        }

        var hardware = new RepositoryHardwareIdentityCatalogLoader().Load(embeddedApplicationRoot);
        var compatibility = new RepositoryCompatibilityCatalogLoader().Load(embeddedApplicationRoot);
        effectiveSelection = null;
        var bootstrapDetail = "Development bootstrap reference manifests are active; production releases require a signed embedded catalog.";
        Status = new(ReferenceCatalogUpdateState.Current, 0, "Development bootstrap", 0, string.Empty, bootstrapDetail);
        return new(hardware, compatibility, new(0, "Development bootstrap", true, bootstrapDetail));
    }

    public Task<ReferenceCatalogUpdateStatus> CheckAsync(CancellationToken cancellationToken = default) =>
        CheckCoreAsync(quietFailure: false, cancellationToken);

    public async Task<ReferenceCatalogUpdateStatus> CheckInBackgroundIfDueAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (channel is null) return Status;
        var state = ReadStateRecoveringMalformed();
        var now = timeProvider.GetUtcNow();
        if (state.LastCheckUtc != DateTimeOffset.MinValue &&
            (state.LastCheckUtc > now.AddMinutes(5) || now - state.LastCheckUtc < AutomaticCheckInterval))
            return Status;
        return await CheckCoreAsync(quietFailure: true, cancellationToken).ConfigureAwait(false);
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
            using var mutation = AcquireMutationLock();
            var state = ReadState();
            RequireForwardRevision(pending.Manifest.Revision, state);
            Activate(pending, state);
            Status = new(ReferenceCatalogUpdateState.Completed, pending.Manifest.Revision, pending.Manifest.CatalogVersion, 0, string.Empty,
                "Signed reference catalog activated on disk. Restart AV Workstation Toolkit to use it for Device Lookup.", pending.Changes);
            pending = null;
        }
        catch (Exception exception) when (IsMutationFailure(exception))
        {
            pending = null;
            Status = Status with { State = ReferenceCatalogUpdateState.Rejected, Detail = $"Reference catalog activation failed: {exception.Message}" };
        }
        return Task.FromResult(Status);
    }

    private async Task<ReferenceCatalogUpdateStatus> CheckCoreAsync(bool quietFailure, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (channel is null)
        {
            if (!quietFailure)
                Status = Status with { State = ReferenceCatalogUpdateState.NotConfigured, Detail = "No public signed reference-catalog channel is configured for this private repository." };
            return Status;
        }
        var priorStatus = Status;
        try
        {
            if (!quietFailure)
                Status = Status with { State = ReferenceCatalogUpdateState.Checking, Detail = "Checking the signed reference-catalog channel…" };
            var state = ReadStateRecoveringMalformed();
            var available = await channel.GetLatestAsync(HighestRetainedRevision(state), cancellationToken).ConfigureAwait(false);
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
            RequireForwardRevision(verified.Manifest.Revision, state);
            pending = verified;
            Status = new(ReferenceCatalogUpdateState.UpdateAvailable, effectiveSelection?.Revision ?? state.ActiveRevision, Status.CurrentVersion,
                verified.Manifest.Revision, verified.Manifest.CatalogVersion, "A signed descriptive reference-catalog update is available.", verified.Changes);
        }
        catch (ReferenceCatalogRequiresNewerApplicationException exception)
        {
            // Never retained: a catalog that needs a newer application is re-downloaded after the upgrade.
            pending = null;
            Status = quietFailure ? priorStatus : Status with { State = ReferenceCatalogUpdateState.RequiresNewerApp, Detail = exception.Message };
        }
        catch (Exception exception) when (exception is HttpRequestException ||
            (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            pending = null;
            Status = quietFailure ? priorStatus : Status with { State = ReferenceCatalogUpdateState.Offline, Detail = "The signed reference-catalog channel is unavailable." };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CatalogValidationException or CryptographicException or InvalidDataException)
        {
            pending = null;
            Status = quietFailure ? priorStatus : Status with { State = ReferenceCatalogUpdateState.Rejected, Detail = $"Reference catalog rejected: {exception.Message}" };
        }
        finally
        {
            TryRecordCheckTime();
        }
        return Status;
    }

    /// <summary>
    /// Activation has one commit point: the atomic move of the verified staging directory into
    /// <c>catalogs</c>. Before it, any failure leaves nothing behind and the caller reports rejection.
    /// After it, the revision is durable and startup selection will choose it, so the remaining
    /// bookkeeping is best-effort - startup already repairs a stale state pointer and deletes
    /// superseded revisions. Treating a bookkeeping failure as rejection would report that activation
    /// did not happen while the new revision was in fact already live on the next launch.
    /// </summary>
    private void Activate(VerifiedReferenceCatalogBundle bundle, StateDocument state)
    {
        EnsureDirectories();
        var final = CatalogDirectory(bundle.Manifest.Revision);
        var staging = Path.Combine(root, "staging", $"{bundle.Manifest.Revision}-{Guid.NewGuid():N}");
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
            // Directory.Move refuses to overwrite an existing revision and fails without side effects,
            // so no separate existence pre-check is needed to keep committed revisions immutable.
            Directory.Move(staging, final);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }

        effectiveSelection = null;
        // Already holding the mutation lock, so these use the non-locking forms.
        try { WriteState(new(bundle.Manifest.Revision, state.LastCheckUtc)); }
        catch (Exception exception) when (IsMutationFailure(exception)) { }
        try { DeleteStoredRevisionsExcept(bundle.Manifest.Revision); }
        catch (Exception exception) when (IsMutationFailure(exception)) { }
    }

    private LocalSelection? LoadEmbeddedCandidate()
    {
        var path = EmbeddedBundlePath();
        if (!File.Exists(path)) return null;
        try
        {
            var verified = verifier.VerifyFile(path);
            return new(verified.Manifest.Revision, verified.Manifest.CatalogVersion, true, verified);
        }
        catch (ReferenceCatalogRequiresNewerApplicationException)
        {
            throw new CatalogValidationException("The packaged signed reference catalog is not compatible with this application release.");
        }
    }

    private List<LocalSelection> LoadStoredCandidates()
    {
        var candidates = new List<LocalSelection>();
        foreach (var revision in EnumerateStoredRevisionDirectories())
        {
            try
            {
                var verified = verifier.VerifyDirectory(CatalogDirectory(revision));
                if (verified.Manifest.Revision != revision)
                    throw new CatalogValidationException("Stored reference catalog revision does not match its directory.");
                candidates.Add(new(revision, verified.Manifest.CatalogVersion, false, verified));
            }
            catch (Exception exception) when (exception is ReferenceCatalogRequiresNewerApplicationException || IsCatalogFailure(exception))
            {
                TryDeleteStoredRevision(revision);
            }
        }
        return candidates;
    }

    private static LocalSelection? SelectEffectiveCatalog(IEnumerable<LocalSelection> stored, LocalSelection? embedded)
    {
        var candidates = new List<LocalSelection>(stored);
        if (embedded is not null) candidates.Add(embedded);
        return candidates.OrderByDescending(candidate => candidate.Revision).ThenBy(candidate => candidate.IsEmbedded).FirstOrDefault();
    }

    private void RequireForwardRevision(long revision, StateDocument state)
    {
        if (revision <= HighestRetainedRevision(state))
            throw new CatalogValidationException("Reference catalog rollback or same-revision activation is not permitted.");
    }

    private long HighestRetainedRevision(StateDocument state)
    {
        var highest = state.ActiveRevision;
        try { highest = Math.Max(highest, LoadEmbeddedCandidate()?.Revision ?? 0); }
        catch (CatalogValidationException) { }
        foreach (var revision in EnumerateStoredRevisionDirectories())
            highest = Math.Max(highest, revision);
        return highest;
    }

    private IEnumerable<long> EnumerateStoredRevisionDirectories()
    {
        var catalogs = Path.Combine(root, "catalogs");
        if (!Directory.Exists(catalogs)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(catalogs, "*", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            if (long.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var revision) && revision > 0)
                yield return revision;
        }
    }

    private StateDocument ReadStateRecoveringMalformed()
    {
        try { return ReadState(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CatalogValidationException or JsonException)
        {
            try
            {
                using var mutation = AcquireMutationLock();
                var path = Path.Combine(root, "state.json");
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path);
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException or TimeoutException) { }
            return new(0, DateTimeOffset.MinValue);
        }
    }

    private StateDocument ReadState()
    {
        var path = Path.Combine(root, "state.json");
        if (!File.Exists(path)) return new(0, DateTimeOffset.MinValue);
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
            if (!names.SetEquals(["ActiveRevision", "LastCheckUtc"]))
                throw new CatalogValidationException("Reference catalog state fields are incomplete.");
            var state = JsonSerializer.Deserialize<StateDocument>(json, StateOptions)
                ?? throw new CatalogValidationException("Reference catalog state file is empty.");
            if (state.ActiveRevision < 0)
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

    private void TryWriteState(StateDocument state)
    {
        try { using var mutation = AcquireMutationLock(); WriteState(state); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException) { }
    }

    private void TryRecordCheckTime()
    {
        try
        {
            using var mutation = AcquireMutationLock();
            WriteState(ReadState() with { LastCheckUtc = timeProvider.GetUtcNow() });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CatalogValidationException or TimeoutException) { }
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(root);
        RejectReparse(root);
        foreach (var name in new[] { "catalogs", "staging" })
        {
            var path = Path.Combine(root, name);
            Directory.CreateDirectory(path);
            RejectReparse(path);
        }
    }

    private void TryPerformConservativeCleanup()
    {
        try
        {
            using var mutation = AcquireMutationLock();
            foreach (var path in Directory.EnumerateFileSystemEntries(Path.Combine(root, "staging"), "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true); else File.Delete(path);
            }
            foreach (var temporary in Directory.EnumerateFiles(root, "state.*.tmp", SearchOption.TopDirectoryOnly))
                if ((File.GetAttributes(temporary) & FileAttributes.ReparsePoint) == 0) File.Delete(temporary);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException) { }
    }

    /// <summary>
    /// Selection happens before this lock is taken, so another instance may have activated a newer
    /// revision in between. Only revisions older than the selection are removed; a newer one is kept
    /// for the next start.
    /// </summary>
    private void TryDeleteSupersededStoredRevisions(LocalSelection selected)
    {
        try
        {
            using var mutation = AcquireMutationLock();
            foreach (var revision in EnumerateStoredRevisionDirectories().Where(value => value < selected.Revision).ToArray())
                DeleteStoredRevision(revision);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException) { }
    }

    private void DeleteStoredRevisionsExcept(long retainedRevision)
    {
        foreach (var revision in EnumerateStoredRevisionDirectories().Where(value => value != retainedRevision).ToArray())
            DeleteStoredRevision(revision);
    }

    private void TryDeleteStoredRevision(long revision)
    {
        try { using var mutation = AcquireMutationLock(); DeleteStoredRevision(revision); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException) { }
    }

    private void DeleteStoredRevision(long revision)
    {
        var path = CatalogDirectory(revision);
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            Directory.Delete(path, recursive: true);
    }

    private string CatalogDirectory(long revision) => Path.Combine(root, "catalogs", revision.ToString(CultureInfo.InvariantCulture));
    private string EmbeddedBundlePath() => Path.Combine(embeddedApplicationRoot, EmbeddedBundleRelativePath.Replace('/', Path.DirectorySeparatorChar));

    private MutationLease AcquireMutationLock() => new(root, mutationLockTimeout);

    private static bool IsCatalogFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or CatalogValidationException or CryptographicException or InvalidDataException;

    private static bool IsMutationFailure(Exception exception) => IsCatalogFailure(exception) || exception is TimeoutException;

    private static string RequireExistingRoot(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) throw new IOException($"Reference catalog {description} root must be absolute.");
        var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Reference catalog {description} root is unavailable or is a reparse point.");
        return full;
    }

    private static string RequireDataRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) throw new IOException("Reference catalog data root must be absolute.");
        var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Reference catalog data root cannot be a filesystem root.");
        Directory.CreateDirectory(full);
        RejectReparse(full);
        return full;
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reference catalog storage cannot use reparse points.");
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StateDocument(long ActiveRevision, DateTimeOffset LastCheckUtc);

    private sealed record LocalSelection(long Revision, string Version, bool IsEmbedded, VerifiedReferenceCatalogBundle Bundle);

    private sealed class MutationLease : IDisposable
    {
        private readonly Mutex mutex;
        private bool acquired;

        public MutationLease(string catalogRoot, TimeSpan timeout)
        {
            var normalized = Path.GetFullPath(catalogRoot).ToUpperInvariant();
            var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32];
            mutex = new Mutex(false, $"Local\\AVWT.ReferenceCatalog.{suffix}");
            try { acquired = mutex.WaitOne(timeout); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                mutex.Dispose();
                throw new TimeoutException("Another AV Workstation Toolkit instance is updating the reference catalog.");
            }
        }

        public void Dispose()
        {
            if (acquired) { mutex.ReleaseMutex(); acquired = false; }
            mutex.Dispose();
        }
    }
}
