using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Application.Compatibility;

public enum ReferenceCatalogUpdateState
{
    Idle,
    Checking,
    Current,
    UpdateAvailable,
    Downloading,
    Validating,
    Completed,
    Rejected,
    Offline,
    RequiresNewerApp,
    NotConfigured
}

public sealed record ReferenceCatalogSource(
    long Revision,
    string Version,
    bool IsEmbedded,
    string Detail);

public sealed record ReferenceCatalogSet(
    HardwareIdentityCatalog Hardware,
    SoftwareCompatibilityCatalog Compatibility,
    ReferenceCatalogSource Source);

public sealed record ReferenceCatalogUpdateStatus(
    ReferenceCatalogUpdateState State,
    long CurrentRevision,
    string CurrentVersion,
    long AvailableRevision,
    string AvailableVersion,
    string Detail,
    ReferenceCatalogChangeSummary? Changes = null);

public sealed record ReferenceCatalogChannelPackage(
    long Revision,
    long PreviousRevision,
    string Version,
    string MinimumAppVersion,
    string BundleSha256,
    byte[] BundleBytes);

/// <summary>Retrieves only the one source-controlled signed reference-catalog channel.</summary>
public interface IReferenceCatalogChannelClient
{
    Task<ReferenceCatalogChannelPackage?> GetLatestAsync(long currentRevision, CancellationToken cancellationToken = default);
}

public interface IReferenceCatalogUpdateService
{
    ReferenceCatalogUpdateStatus Status { get; }
    ReferenceCatalogSet LoadActiveOrEmbedded();
    Task<ReferenceCatalogUpdateStatus> CheckAsync(CancellationToken cancellationToken = default);
    Task<ReferenceCatalogUpdateStatus> InstallAvailableAsync(CancellationToken cancellationToken = default);
    Task<ReferenceCatalogUpdateStatus> ImportAsync(string bundlePath, CancellationToken cancellationToken = default);
}
