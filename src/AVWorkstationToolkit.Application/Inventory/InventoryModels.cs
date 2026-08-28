using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Application.Inventory;

public enum ProviderQuality { Complete, Partial, Unavailable, Malformed }
public enum ProviderFailureKind { None, ProviderUnavailable, ExecutionFailed, TimedOut, MalformedOutput, PartialInventory, TrustFailure, Cancelled, OutputLimitExceeded }
public enum RegistryInventorySource { Hklm64, Hklm32, Hkcu }
public enum WinGetReadOnlyOperation { Version, InstalledInventory, AvailableUpdates }

public sealed record InstalledPackageRecord(string Id, string InstalledVersion);
public sealed record AvailableUpdateRecord(string Id, string InstalledVersion, string AvailableVersion);

public sealed record InstalledPackageInventoryResult(
    ProviderQuality Quality,
    ProviderFailureKind Failure,
    IReadOnlyList<InstalledPackageRecord> Packages,
    string Detail,
    string DiagnosticOutput);

public sealed record AvailableUpdateInventoryResult(
    ProviderQuality Quality,
    ProviderFailureKind Failure,
    IReadOnlyList<AvailableUpdateRecord> Updates,
    string Detail,
    string DiagnosticOutput);

public sealed record RegistryUninstallRecord(
    RegistryInventorySource Source,
    string DisplayName,
    string DisplayVersion);

public sealed record RegistrySourceStatus(
    RegistryInventorySource Source,
    bool Available,
    int EntryCount,
    string Detail);

public sealed record RegistryInventoryResult(
    ProviderQuality Quality,
    ProviderFailureKind Failure,
    IReadOnlyList<RegistryUninstallRecord> Records,
    IReadOnlyList<RegistrySourceStatus> Sources,
    string Detail);

public sealed record ExternalPackageInventoryEvidence(
    string Id,
    bool Reliable,
    bool Installed,
    string InstalledVersion,
    IReadOnlyList<string> InstalledVersions,
    InventoryQuality InventoryQuality,
    string Detail);

public sealed record RebootDetectionResult(
    bool Pending,
    IReadOnlyList<RebootReason> Reasons,
    ProviderQuality Quality,
    ProviderFailureKind Failure,
    string Detail);

public sealed record TrustedWinGetResolution(
    bool Trusted,
    string ExecutablePath,
    ProviderFailureKind Failure,
    string Detail);

public sealed record WinGetProcessResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    string ExportJson,
    bool TimedOut,
    bool Cancelled,
    bool OutputLimitExceeded,
    ProviderFailureKind Failure);

public interface IInstalledPackageInventory
{
    Task<InstalledPackageInventoryResult> ReadAsync(CancellationToken cancellationToken = default);
}

public interface IAvailableUpdateInventory
{
    Task<AvailableUpdateInventoryResult> ReadAsync(CancellationToken cancellationToken = default);
}

public interface IExternalApplicationInventory
{
    Task<RegistryInventoryResult> ReadAsync(CancellationToken cancellationToken = default);
}

public interface IRebootStateProvider
{
    Task<RebootDetectionResult> ReadAsync(CancellationToken cancellationToken = default);
}

public interface IWinGetResolver
{
    Task<TrustedWinGetResolution> ResolveAsync(CancellationToken cancellationToken = default);
}

public interface IWinGetReadOnlyProcessRunner
{
    Task<WinGetProcessResult> RunAsync(WinGetReadOnlyOperation operation, CancellationToken cancellationToken = default);
}

// Later phases own these mutation and transport boundaries. Phase 3 does not implement them.
public interface IActionWorkerBoundary;
public interface IVendorProvider;
public interface ICredentialStore;
