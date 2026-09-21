using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Application.Catalog;

public enum ManagedCatalogUpdateState
{
    Idle,
    Checking,
    Current,
    UpdateAvailable,
    Validating,
    Completed,
    Rejected,
    Offline,
    RequiresNewerApp,
    NotConfigured
}

public sealed record ManagedCatalogSource(
    long Revision,
    string Version,
    bool IsEmbedded,
    string Detail);

public sealed record ManagedCatalogSet(
    PackageCatalog Catalog,
    ManagedCatalogSource Source);

public sealed record ManagedCatalogUpdateStatus(
    ManagedCatalogUpdateState State,
    long CurrentRevision,
    string CurrentVersion,
    string Source,
    long AvailableRevision,
    string AvailableVersion,
    bool Verified,
    bool RestartRequired,
    string Detail);

public sealed record ManagedCatalogChannelPackage(
    long Revision,
    long PreviousRevision,
    string Version,
    string MinimumAppVersion,
    DateTimeOffset CreatedUtc,
    string SigningKeyId,
    string BundleSha256,
    byte[] BundleBytes);

/// <summary>Retrieves only the configured fixed-origin signed managed-catalog channel.</summary>
public interface IManagedCatalogChannelClient
{
    Task<ManagedCatalogChannelPackage?> GetLatestAsync(long currentRevision, CancellationToken cancellationToken = default);
}

public interface IManagedCatalogUpdateService
{
    ManagedCatalogUpdateStatus Status { get; }
    ManagedCatalogSet LoadActiveOrEmbedded();
    Task<ManagedCatalogUpdateStatus> CheckAsync(CancellationToken cancellationToken = default);
    Task<ManagedCatalogUpdateStatus> InstallAvailableAsync(CancellationToken cancellationToken = default);
}
