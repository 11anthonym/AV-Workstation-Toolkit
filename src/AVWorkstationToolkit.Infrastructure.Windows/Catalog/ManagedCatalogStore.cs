using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AVWorkstationToolkit.Application.Catalog;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Infrastructure.Windows.Catalog;

/// <summary>
/// Selects the newest verified managed catalog from the signed embedded baseline and at most one
/// retained download. Activation commits with one atomic directory move; callers must restart so
/// the application and worker independently select the same immutable revision.
/// </summary>
public sealed class ManagedCatalogStore : IManagedCatalogUpdateService
{
    public const string EmbeddedBundleRelativePath = "managed-catalog/AVWT-Managed-Catalog.avwtmanaged";
    private const string StoredBundleName = "AVWT-Managed-Catalog.avwtmanaged";
    private readonly string applicationRoot;
    private readonly string root;
    private readonly ManagedCatalogVerifier verifier;
    private readonly IManagedCatalogChannelClient? channel;
    private readonly bool requireSignedBaseline;
    private readonly TimeSpan mutationLockTimeout;
    private PendingCatalog? pending;
    private LocalSelection? effective;

    public ManagedCatalogStore(
        string applicationRoot,
        string dataRoot,
        ManagedCatalogVerifier verifier,
        IManagedCatalogChannelClient? channel = null,
        bool requireSignedBaseline = true,
        TimeSpan? mutationLockTimeout = null)
    {
        this.applicationRoot = RequireExistingRoot(applicationRoot, "application");
        root = Path.Combine(RequireDataRoot(dataRoot), "ManagedCatalog");
        this.verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        this.channel = channel;
        this.requireSignedBaseline = requireSignedBaseline;
        this.mutationLockTimeout = mutationLockTimeout ?? TimeSpan.FromSeconds(5);
        if (this.mutationLockTimeout <= TimeSpan.Zero || this.mutationLockTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(mutationLockTimeout));
    }

    public ManagedCatalogUpdateStatus Status { get; private set; } =
        new(ManagedCatalogUpdateState.Idle, 0, string.Empty, "Unavailable", 0, string.Empty, false, false,
            "No managed catalog has been loaded.");

    public ManagedCatalogSet LoadActiveOrEmbedded()
    {
        if (effective is not null)
            return ToCatalogSet(effective);

        EnsureDirectories();
        TryCleanStaging();
        var embedded = LoadEmbedded();
        if (requireSignedBaseline && embedded is null)
            throw new CatalogValidationException("The required signed embedded managed-catalog baseline is unavailable.");
        var stored = LoadStored();
        var selected = stored.Concat(embedded is null ? [] : new[] { embedded })
            .OrderByDescending(item => item.Revision)
            .ThenByDescending(item => item.IsEmbedded)
            .FirstOrDefault();

        if (selected is null)
        {
            if (requireSignedBaseline)
                throw new CatalogValidationException("The required signed embedded managed-catalog baseline is unavailable.");
            var bootstrap = new RepositoryCatalogLoader().LoadManaged(applicationRoot);
            const string detail = "Development source managed catalog is active; packaged runtimes require a signed embedded baseline.";
            effective = null;
            Status = new(ManagedCatalogUpdateState.NotConfigured, 0, "Development source", "Development source", 0, string.Empty,
                false, false, detail);
            return new(bootstrap, new(0, "Development source", true, detail));
        }

        effective = selected;
        TryDeleteStoredExcept(selected.IsEmbedded ? 0 : selected.Revision);
        var source = selected.IsEmbedded ? "Signed embedded baseline" : "Verified downloaded catalog";
        var detailText = $"{source} revision {selected.Revision} is active.";
        Status = new(ManagedCatalogUpdateState.Current, selected.Revision, selected.Version, source, 0, string.Empty,
            true, false, detailText);
        return ToCatalogSet(selected);
    }

    public async Task<ManagedCatalogUpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Status.RestartRequired) return Status;
        if (channel is null)
        {
            Status = Status with { State = ManagedCatalogUpdateState.NotConfigured, Detail = "No production managed-catalog channel is configured." };
            return Status;
        }

        try
        {
            if (effective is null) _ = LoadActiveOrEmbedded();
            var current = effective ?? throw new CatalogValidationException("No verified managed catalog is available.");
            Status = Status with { State = ManagedCatalogUpdateState.Checking, Detail = "Checking the signed managed-catalog channel…", RestartRequired = false };
            var available = await channel.GetLatestAsync(current.Revision, cancellationToken).ConfigureAwait(false);
            if (available is null)
            {
                pending = null;
                Status = Status with
                {
                    State = ManagedCatalogUpdateState.Current,
                    AvailableRevision = 0,
                    AvailableVersion = string.Empty,
                    Verified = true,
                    RestartRequired = false,
                    Detail = "The signed managed application catalog is current."
                };
                return Status;
            }

            var actualHash = Convert.ToHexString(SHA256.HashData(available.BundleBytes));
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actualHash),
                    Encoding.ASCII.GetBytes(available.BundleSha256.ToUpperInvariant())))
                throw new CatalogValidationException("Managed catalog channel bundle hash verification failed.");

            using var stream = new MemoryStream(available.BundleBytes, writable: false);
            var bundle = verifier.VerifyArchive(stream);
            if (bundle.Manifest.Revision != available.Revision || bundle.Manifest.PreviousRevision != available.PreviousRevision ||
                !string.Equals(bundle.Manifest.CatalogVersion, available.Version, StringComparison.Ordinal) ||
                !string.Equals(bundle.Manifest.MinimumAppVersion, available.MinimumAppVersion, StringComparison.Ordinal) ||
                bundle.Manifest.CreatedUtc != available.CreatedUtc ||
                !string.Equals(bundle.Manifest.SigningKeyId, available.SigningKeyId, StringComparison.Ordinal))
                throw new CatalogValidationException("Signed channel metadata does not match the signed managed catalog bundle.");
            if (bundle.Manifest.Revision <= current.Revision)
                throw new CatalogValidationException("Managed catalog rollback or same-revision activation is not permitted.");
            pending = new(bundle, available.BundleBytes.ToArray());
            Status = Status with
            {
                State = ManagedCatalogUpdateState.UpdateAvailable,
                AvailableRevision = bundle.Manifest.Revision,
                AvailableVersion = bundle.Manifest.CatalogVersion,
                Verified = true,
                RestartRequired = false,
                Detail = "A verified signed managed application catalog update is available."
            };
        }
        catch (ManagedCatalogRequiresNewerApplicationException exception)
        {
            pending = null;
            Status = Status with { State = ManagedCatalogUpdateState.RequiresNewerApp, Verified = false, RestartRequired = false, Detail = exception.Message };
        }
        catch (Exception exception) when (exception is HttpRequestException ||
            (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            pending = null;
            Status = Status with
            {
                State = ManagedCatalogUpdateState.Offline,
                Verified = false,
                RestartRequired = false,
                Detail = "The signed managed-catalog channel is unavailable; the active verified catalog remains in use."
            };
        }
        catch (Exception exception) when (IsCatalogFailure(exception))
        {
            pending = null;
            Status = Status with
            {
                State = ManagedCatalogUpdateState.Rejected,
                Verified = false,
                RestartRequired = false,
                Detail = $"Managed catalog rejected: {exception.Message}"
            };
        }
        return Status;
    }

    public Task<ManagedCatalogUpdateStatus> InstallAvailableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Status.RestartRequired) return Task.FromResult(Status);
        if (pending is null)
        {
            Status = Status with
            {
                State = ManagedCatalogUpdateState.Rejected,
                Verified = false,
                Detail = "No verified downloaded managed catalog is pending activation."
            };
            return Task.FromResult(Status);
        }

        try
        {
            Status = Status with { State = ManagedCatalogUpdateState.Validating, Detail = "Revalidating and activating the signed managed catalog…" };
            using var lease = new MutationLease(root, mutationLockTimeout);
            var current = LoadSelectionOnly();
            if (pending.Bundle.Manifest.Revision <= current.Revision)
                throw new CatalogValidationException("Managed catalog rollback or same-revision activation is not permitted.");
            Activate(pending);
            Status = new(ManagedCatalogUpdateState.Completed, current.Revision, current.Version, Status.Source,
                pending.Bundle.Manifest.Revision, pending.Bundle.Manifest.CatalogVersion, true, true,
                $"Verified managed catalog revision {pending.Bundle.Manifest.Revision} was activated. Restart AV Workstation Toolkit before using it.");
            pending = null;
        }
        catch (Exception exception) when (IsMutationFailure(exception))
        {
            pending = null;
            Status = Status with
            {
                State = ManagedCatalogUpdateState.Rejected,
                Verified = false,
                RestartRequired = false,
                Detail = $"Managed catalog activation failed: {exception.Message}"
            };
        }
        return Task.FromResult(Status);
    }

    private void Activate(PendingCatalog value)
    {
        EnsureDirectories();
        var revision = value.Bundle.Manifest.Revision;
        var final = CatalogDirectory(revision);
        var staging = Path.Combine(root, "staging", $"{revision}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var stagedBundle = Path.Combine(staging, StoredBundleName);
            using (var output = new FileStream(stagedBundle, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384, FileOptions.WriteThrough))
            {
                output.Write(value.Bytes);
                output.Flush(flushToDisk: true);
            }
            var verified = verifier.VerifyFile(stagedBundle);
            if (verified.Manifest.Revision != revision)
                throw new CatalogValidationException("Staged managed catalog revision changed during activation.");
            Directory.Move(staging, final);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        try { DeleteStoredExcept(revision); }
        catch (Exception exception) when (IsMutationFailure(exception)) { }
    }

    private static ManagedCatalogSet ToCatalogSet(LocalSelection selected)
    {
        var source = selected.IsEmbedded ? "Signed embedded baseline" : "Verified downloaded catalog";
        var detail = $"{source} revision {selected.Revision} is active.";
        return new(selected.Bundle.Catalog, new(selected.Revision, selected.Version, selected.IsEmbedded, detail));
    }

    private LocalSelection? LoadEmbedded()
    {
        var path = Path.Combine(applicationRoot, EmbeddedBundleRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return null;
        try
        {
            var bundle = verifier.VerifyFile(path);
            return new(bundle.Manifest.Revision, bundle.Manifest.CatalogVersion, true, bundle);
        }
        catch (ManagedCatalogRequiresNewerApplicationException)
        {
            throw new CatalogValidationException("The packaged signed managed catalog is not compatible with this application release.");
        }
    }

    private IReadOnlyList<LocalSelection> LoadStored()
    {
        var result = new List<LocalSelection>();
        foreach (var revision in EnumerateStoredRevisions())
        {
            try
            {
                var bundle = verifier.VerifyFile(Path.Combine(CatalogDirectory(revision), StoredBundleName));
                if (bundle.Manifest.Revision != revision)
                    throw new CatalogValidationException("Stored managed catalog revision does not match its directory.");
                result.Add(new(revision, bundle.Manifest.CatalogVersion, false, bundle));
            }
            catch (Exception exception) when (exception is ManagedCatalogRequiresNewerApplicationException || IsCatalogFailure(exception))
            {
                TryDeleteStored(revision);
            }
        }
        return result;
    }

    private LocalSelection LoadSelectionOnly()
    {
        var embedded = LoadEmbedded();
        var selection = LoadStored().Concat(embedded is null ? [] : new[] { embedded })
            .OrderByDescending(item => item.Revision).ThenByDescending(item => item.IsEmbedded).FirstOrDefault();
        if (selection is null)
            throw new CatalogValidationException("No verified managed catalog is available.");
        return selection;
    }

    private IEnumerable<long> EnumerateStoredRevisions()
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

    private void TryCleanStaging()
    {
        try
        {
            using var lease = new MutationLease(root, mutationLockTimeout);
            foreach (var path in Directory.EnumerateFileSystemEntries(Path.Combine(root, "staging"), "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path);
            }
        }
        catch (Exception exception) when (IsMutationFailure(exception)) { }
    }

    private void TryDeleteStoredExcept(long revision)
    {
        try { using var lease = new MutationLease(root, mutationLockTimeout); DeleteStoredExcept(revision); }
        catch (Exception exception) when (IsMutationFailure(exception)) { }
    }

    private void DeleteStoredExcept(long revision)
    {
        foreach (var candidate in EnumerateStoredRevisions().Where(item => item != revision).ToArray())
            DeleteStored(candidate);
    }

    private void TryDeleteStored(long revision)
    {
        try { using var lease = new MutationLease(root, mutationLockTimeout); DeleteStored(revision); }
        catch (Exception exception) when (IsMutationFailure(exception)) { }
    }

    private void DeleteStored(long revision)
    {
        var path = CatalogDirectory(revision);
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            Directory.Delete(path, true);
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

    private string CatalogDirectory(long revision) => Path.Combine(root, "catalogs", revision.ToString(CultureInfo.InvariantCulture));

    private static bool IsCatalogFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or CatalogValidationException or CryptographicException or InvalidDataException;

    private static bool IsMutationFailure(Exception exception) => IsCatalogFailure(exception) || exception is TimeoutException;

    private static string RequireExistingRoot(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new IOException($"Managed catalog {description} root must be absolute.");
        var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Managed catalog {description} root is unavailable or is a reparse point.");
        return full;
    }

    private static string RequireDataRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new IOException("Managed catalog data root must be absolute.");
        var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Managed catalog data root cannot be a filesystem root.");
        Directory.CreateDirectory(full);
        RejectReparse(full);
        return full;
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Managed catalog storage cannot use reparse points.");
    }

    private sealed record PendingCatalog(VerifiedManagedCatalogBundle Bundle, byte[] Bytes);
    private sealed record LocalSelection(long Revision, string Version, bool IsEmbedded, VerifiedManagedCatalogBundle Bundle);

    private sealed class MutationLease : IDisposable
    {
        private readonly Mutex mutex;
        private bool acquired;

        internal MutationLease(string catalogRoot, TimeSpan timeout)
        {
            var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(catalogRoot).ToUpperInvariant())))[..32];
            mutex = new Mutex(false, $"Local\\AVWT.ManagedCatalog.{suffix}");
            try { acquired = mutex.WaitOne(timeout); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                mutex.Dispose();
                throw new TimeoutException("Another AV Workstation Toolkit instance is updating the managed catalog.");
            }
        }

        public void Dispose()
        {
            if (acquired) { mutex.ReleaseMutex(); acquired = false; }
            mutex.Dispose();
        }
    }
}
