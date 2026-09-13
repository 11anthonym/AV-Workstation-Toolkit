using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AVWorkstationToolkit.Application.Compatibility;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Infrastructure.Windows.Catalog;

public sealed class ReferenceCatalogStore : IReferenceCatalogUpdateService
{
    public const string EmbeddedBundleRelativePath = "reference-catalog/AVWT-Reference-Catalog.avwtcatalog";
    public static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);
    private const int MaximumQuarantineEntries = 8;
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
        LocalSelection? embedded = null;
        var embeddedIncompatible = false;
        try { embedded = LoadEmbeddedCandidate(); }
        catch (ReferenceCatalogRequiresNewerApplicationException) { embeddedIncompatible = true; }
        var compatible = new List<LocalSelection>();
        var incompatibleRevisions = new HashSet<long>();

        foreach (var revision in EnumerateStoredRevisions(state))
        {
            try
            {
                var verified = verifier.VerifyDirectory(CatalogDirectory(revision));
                if (verified.Manifest.Revision != revision)
                    throw new CatalogValidationException("Stored reference catalog revision does not match its directory.");
                compatible.Add(new(revision, verified.Manifest.CatalogVersion, false, verified));
            }
            catch (ReferenceCatalogRequiresNewerApplicationException exception) when (exception.Revision == revision)
            {
                // A fully verified snapshot can be incompatible with this executable and valid again after an app upgrade.
                incompatibleRevisions.Add(revision);
            }
            catch (Exception exception) when (IsCatalogFailure(exception))
            {
                TryQuarantineStoredRevision(revision);
            }
        }

        if (embedded is not null) compatible.Add(embedded);
        var selected = SelectEffectiveCatalog(compatible, state);
        if (selected is not null)
        {
            effectiveSelection = selected;
            TryPruneObsoleteCompatibleCatalogs(state, selected, compatible, incompatibleRevisions);
            var source = selected.IsEmbedded ? "signed embedded" : "retained signed";
            var detail = $"The {source} reference catalog revision {selected.Revision} is active.";
            if (incompatibleRevisions.Contains(state.ActiveRevision))
                detail += " A newer retained catalog is valid but requires a newer AV Workstation Toolkit version.";
            var restorable = GetRestorableRevision(state, selected, embedded);
            Status = new(ReferenceCatalogUpdateState.Current, selected.Revision, selected.Version, 0, string.Empty, detail,
                RestorableRevision: restorable);
            return new(selected.Bundle.Hardware, selected.Bundle.Compatibility,
                new(selected.Revision, selected.Version, selected.IsEmbedded, detail));
        }

        if (embeddedIncompatible)
            throw new CatalogValidationException("The packaged signed reference catalog is not compatible with this application release.");

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
            ValidateRevisionChain(pending.Manifest, state, HighestRetainedRevision());
            Activate(pending, state);
            var activatedState = ReadState();
            Status = new(ReferenceCatalogUpdateState.Completed, pending.Manifest.Revision, pending.Manifest.CatalogVersion, 0, string.Empty,
                "Signed reference catalog activated on disk. Restart AV Workstation Toolkit to use it for Device Lookup.", pending.Changes,
                GetValidPreviousRevision(activatedState));
            pending = null;
        }
        catch (Exception exception) when (IsMutationFailure(exception))
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
            var verified = verifier.VerifyFile(bundlePath);
            using var mutation = AcquireMutationLock();
            var state = ReadState();
            ValidateRevisionChain(verified.Manifest, state, HighestRetainedRevision());
            Activate(verified, state);
            var activatedState = ReadState();
            Status = new(ReferenceCatalogUpdateState.Completed, verified.Manifest.Revision, verified.Manifest.CatalogVersion, 0, string.Empty,
                "Signed reference catalog imported and activated on disk. Restart AV Workstation Toolkit to use it for Device Lookup.", verified.Changes,
                GetValidPreviousRevision(activatedState));
            pending = null;
        }
        catch (ReferenceCatalogRequiresNewerApplicationException exception)
        {
            pending = null;
            Status = Status with { State = ReferenceCatalogUpdateState.RequiresNewerApp, Detail = exception.Message };
        }
        catch (Exception exception) when (IsMutationFailure(exception))
        {
            pending = null;
            Status = Status with { State = ReferenceCatalogUpdateState.Rejected, Detail = $"Reference catalog rejected: {exception.Message}" };
        }
        return Task.FromResult(Status);
    }

    public Task<ReferenceCatalogUpdateStatus> RestorePreviousAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutation = AcquireMutationLock();
            var state = ReadState();
            var current = ResolveCurrentForMutation(state);
            var embedded = LoadEmbeddedCandidate();
            var restorableRevision = current is null ? 0 : GetRestorableRevision(state, current, embedded);
            if (current is null || restorableRevision <= 0)
                throw new CatalogValidationException("No previous signed reference catalog is available to restore.");

            LocalSelection previous;
            if (embedded is not null && embedded.Revision == restorableRevision)
                previous = embedded;
            else
            {
                var verified = verifier.VerifyDirectory(CatalogDirectory(restorableRevision));
                previous = new(restorableRevision, verified.Manifest.CatalogVersion, false, verified);
            }

            WriteState(new(previous.IsEmbedded ? 0 : previous.Revision, 0, current.Revision, state.LastCheckUtc));
            pending = null;
            Status = new(ReferenceCatalogUpdateState.Completed, previous.Revision, previous.Version, 0, string.Empty,
                "Previous signed reference catalog restored on disk. Restart AV Workstation Toolkit to use it for Device Lookup.");
        }
        catch (Exception exception) when (IsMutationFailure(exception))
        {
            pending = null;
            Status = Status with { State = ReferenceCatalogUpdateState.Rejected, RestorableRevision = 0, Detail = $"Previous reference catalog could not be restored: {exception.Message}" };
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
            var available = await channel.GetLatestAsync(Math.Max(HighestAcceptedRevision(state), HighestRetainedRevision()), cancellationToken).ConfigureAwait(false);
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
            ValidateRevisionChain(verified.Manifest, state, HighestRetainedRevision());
            pending = verified;
            Status = new(ReferenceCatalogUpdateState.UpdateAvailable, effectiveSelection?.Revision ?? state.ActiveRevision, Status.CurrentVersion,
                verified.Manifest.Revision, verified.Manifest.CatalogVersion, "A signed descriptive reference-catalog update is available.", verified.Changes,
                GetValidPreviousRevision(state));
        }
        catch (ReferenceCatalogRequiresNewerApplicationException exception)
        {
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

    private void Activate(VerifiedReferenceCatalogBundle bundle, StateDocument state)
    {
        EnsureDirectories();
        var current = ResolveCurrentForMutation(state);
        var final = CatalogDirectory(bundle.Manifest.Revision);
        if (Directory.Exists(final)) throw new CatalogValidationException("Reference catalog revision already exists.");
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
            Directory.Move(staging, final);
            WriteState(new(bundle.Manifest.Revision, current is { IsEmbedded: false } ? current.Revision : 0, 0, state.LastCheckUtc));
            effectiveSelection = null;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private LocalSelection? ResolveCurrentForMutation(StateDocument state)
    {
        if (effectiveSelection is not null) return effectiveSelection;
        var embedded = LoadEmbeddedCandidate();
        var candidates = new List<LocalSelection>();
        foreach (var revision in EnumerateStoredRevisions(state))
        {
            try
            {
                var verified = verifier.VerifyDirectory(CatalogDirectory(revision));
                if (verified.Manifest.Revision == revision)
                    candidates.Add(new(revision, verified.Manifest.CatalogVersion, false, verified));
            }
            catch (ReferenceCatalogRequiresNewerApplicationException) { }
            catch (Exception exception) when (IsCatalogFailure(exception)) { }
        }
        if (embedded is not null) candidates.Add(embedded);
        return SelectEffectiveCatalog(candidates, state);
    }

    private LocalSelection? LoadEmbeddedCandidate()
    {
        var path = EmbeddedBundlePath();
        if (!File.Exists(path)) return null;
        var verified = verifier.VerifyFile(path);
        return new(verified.Manifest.Revision, verified.Manifest.CatalogVersion, true, verified);
    }

    private static LocalSelection? SelectEffectiveCatalog(IEnumerable<LocalSelection> candidates, StateDocument state) =>
        candidates.Where(candidate => state.SuppressedRevision <= 0 || candidate.Revision == state.ActiveRevision || candidate.Revision > state.SuppressedRevision)
            .OrderByDescending(candidate => candidate.Revision)
            .ThenBy(candidate => candidate.IsEmbedded)
            .FirstOrDefault();

    private long GetRestorableRevision(StateDocument state, LocalSelection current, LocalSelection? embedded)
    {
        var candidates = new List<long>();
        foreach (var revision in new[] { state.ActiveRevision, state.PreviousRevision }.Distinct())
            if (revision > 0 && revision < current.Revision && GetValidStoredRevision(revision) is not null)
                candidates.Add(revision);
        if (embedded is not null && embedded.Revision < current.Revision)
            candidates.Add(embedded.Revision);
        return candidates.Count == 0 ? 0 : candidates.Max();
    }

    private long GetValidPreviousRevision(StateDocument state) =>
        state.PreviousRevision > 0 && GetValidStoredRevision(state.PreviousRevision) is not null ? state.PreviousRevision : 0;

    private VerifiedReferenceCatalogBundle? GetValidStoredRevision(long revision)
    {
        try
        {
            var verified = verifier.VerifyDirectory(CatalogDirectory(revision));
            return verified.Manifest.Revision == revision ? verified : null;
        }
        catch (ReferenceCatalogRequiresNewerApplicationException) { return null; }
        catch (Exception exception) when (IsCatalogFailure(exception))
        {
            TryQuarantineStoredRevision(revision);
            return null;
        }
    }

    private static void ValidateRevisionChain(ReferenceCatalogBundleManifest manifest, StateDocument state, long highestRetainedRevision)
    {
        if (manifest.Revision <= Math.Max(HighestAcceptedRevision(state), highestRetainedRevision))
            throw new CatalogValidationException("Reference catalog rollback or same-revision activation is not permitted.");
    }

    private static long HighestAcceptedRevision(StateDocument state) =>
        Math.Max(state.ActiveRevision, Math.Max(state.PreviousRevision, state.SuppressedRevision));

    private long HighestRetainedRevision()
    {
        var embeddedRevision = 0L;
        try { embeddedRevision = LoadEmbeddedCandidate()?.Revision ?? 0; }
        catch (ReferenceCatalogRequiresNewerApplicationException exception) { embeddedRevision = exception.Revision; }
        var storedRevision = 0L;
        foreach (var revision in EnumerateStoredRevisionDirectories())
        {
            try
            {
                var verified = verifier.VerifyDirectory(CatalogDirectory(revision));
                if (verified.Manifest.Revision == revision) storedRevision = Math.Max(storedRevision, revision);
            }
            catch (ReferenceCatalogRequiresNewerApplicationException exception) when (exception.Revision == revision)
            {
                storedRevision = Math.Max(storedRevision, revision);
            }
            catch (Exception exception) when (IsCatalogFailure(exception)) { }
        }
        return Math.Max(embeddedRevision, storedRevision);
    }

    private IEnumerable<long> EnumerateStoredRevisions(StateDocument state) =>
        EnumerateStoredRevisionDirectories().Concat([state.ActiveRevision, state.PreviousRevision])
            .Where(value => value > 0).Distinct().OrderByDescending(value => value);

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
            try { using var mutation = AcquireMutationLock(); QuarantineState(); }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException or TimeoutException) { }
            return new(0, 0, 0, DateTimeOffset.MinValue);
        }
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
                (state.PreviousRevision > 0 && (state.ActiveRevision <= 0 || state.PreviousRevision >= state.ActiveRevision)) ||
                (state.SuppressedRevision > 0 && state.SuppressedRevision <= state.ActiveRevision))
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
        foreach (var name in new[] { "catalogs", "staging", "quarantine" })
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
            var oldQuarantine = Directory.EnumerateFileSystemEntries(Path.Combine(root, "quarantine"), "*", SearchOption.TopDirectoryOnly)
                .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(File.GetLastWriteTimeUtc).Skip(MaximumQuarantineEntries).ToArray();
            foreach (var path in oldQuarantine)
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true); else File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException) { }
    }

    private void TryPruneObsoleteCompatibleCatalogs(
        StateDocument state,
        LocalSelection selected,
        IReadOnlyCollection<LocalSelection> compatible,
        IReadOnlySet<long> incompatibleRevisions)
    {
        try
        {
            using var mutation = AcquireMutationLock();
            var retained = new HashSet<long>(incompatibleRevisions);
            retained.Add(selected.Revision);
            foreach (var revision in new[] { state.ActiveRevision, state.PreviousRevision, state.SuppressedRevision }.Where(value => value > 0))
                retained.Add(revision);
            foreach (var revision in compatible.Where(item => !item.IsEmbedded).OrderByDescending(item => item.Revision).Take(3).Select(item => item.Revision))
                retained.Add(revision);
            foreach (var obsolete in compatible.Where(item => !item.IsEmbedded && !retained.Contains(item.Revision)))
            {
                var path = CatalogDirectory(obsolete.Revision);
                if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException) { }
    }

    private string CatalogDirectory(long revision) => Path.Combine(root, "catalogs", revision.ToString(CultureInfo.InvariantCulture));
    private string EmbeddedBundlePath() => Path.Combine(embeddedApplicationRoot, EmbeddedBundleRelativePath.Replace('/', Path.DirectorySeparatorChar));

    private void TryQuarantineStoredRevision(long revision)
    {
        try { using var mutation = AcquireMutationLock(); QuarantineStoredRevision(revision); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException) { }
    }

    private void QuarantineStoredRevision(long revision)
    {
        var source = CatalogDirectory(revision);
        if (!Directory.Exists(source)) return;
        EnsureDirectories();
        Directory.Move(source, Path.Combine(root, "quarantine", $"{revision}-{timeProvider.GetUtcNow():yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"));
    }

    private void QuarantineState()
    {
        var source = Path.Combine(root, "state.json");
        if (!File.Exists(source)) return;
        EnsureDirectories();
        File.Move(source, Path.Combine(root, "quarantine", $"state-{timeProvider.GetUtcNow():yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json"));
    }

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
    private sealed record StateDocument(long ActiveRevision, long PreviousRevision, long SuppressedRevision, DateTimeOffset LastCheckUtc);

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
