using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Workstation;

namespace AVWorkstationToolkit.Application.Workstation;

public interface IWorkstationMachineInfoProvider
{
    WorkstationMachine Read();
}

/// <summary>
/// The read-only workstation inventory use case. It consumes the same providers as the catalog plan — the Windows
/// uninstall registry (HKLM 64-bit, HKLM 32-bit, HKCU) and WinGet's installed export — and produces every application
/// the workstation has, whether or not WinGet or the catalog recognizes it. Uninstall registrations are the
/// completeness boundary; WinGet and the catalog only add identity to them.
/// </summary>
public sealed class WorkstationInventoryService
{
    private readonly IInstalledPackageInventory winGet;
    private readonly IExternalApplicationInventory registry;
    private readonly IWorkstationMachineInfoProvider machine;
    private readonly TimeProvider timeProvider;
    private readonly WorkstationInventoryBuilder builder;

    public WorkstationInventoryService(
        PackageCatalog catalog,
        IInstalledPackageInventory winGet,
        IExternalApplicationInventory registry,
        IWorkstationMachineInfoProvider machine,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        this.winGet = winGet ?? throw new ArgumentNullException(nameof(winGet));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        Identities = new ApplicationIdentityCatalog(catalog);
        builder = new WorkstationInventoryBuilder(Identities);
    }

    public ApplicationIdentityCatalog Identities { get; }

    public async Task<WorkstationInventory> ScanAsync(CancellationToken cancellationToken = default)
    {
        var registryResult = await registry.ReadAsync(cancellationToken).ConfigureAwait(false);
        var winGetResult = await winGet.ReadAsync(cancellationToken).ConfigureAwait(false);
        return Build(winGetResult, registryResult);
    }

    /// <summary>Builds the inventory from the provider results a catalog-plan refresh already read.</summary>
    public WorkstationInventory Build(WorkstationPlanEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return Build(evidence.WinGetInventory, evidence.RegistryInventory);
    }

    public WorkstationInventory Build(InstalledPackageInventoryResult winGetResult, RegistryInventoryResult registryResult)
    {
        ArgumentNullException.ThrowIfNull(winGetResult);
        ArgumentNullException.ThrowIfNull(registryResult);
        var registrations = registryResult.Records.Select(record => new UninstallRegistration(
            record.Source switch
            {
                RegistryInventorySource.Hklm64 => UninstallHive.Machine64,
                RegistryInventorySource.Hklm32 => UninstallHive.Machine32,
                _ => UninstallHive.User
            },
            ApplicationNames.Clean(record.KeyName),
            ApplicationNames.Clean(record.DisplayName),
            ApplicationNames.Clean(record.DisplayVersion),
            ApplicationNames.Clean(record.Publisher),
            record.SystemComponent,
            record.WindowsInstaller,
            ApplicationNames.Clean(record.ParentKeyName),
            ApplicationNames.Clean(record.ReleaseType),
            record.MsiUpgradeCode));
        var packages = winGetResult.Packages.Select(package => new WinGetPackageEvidence(package.Id, ApplicationNames.Clean(package.InstalledVersion)));
        var registryQuality = Quality(registryResult.Quality);
        var winGetQuality = Quality(winGetResult.Quality);
        var detail = (registryQuality, winGetQuality) switch
        {
            (EvidenceQuality.Complete, EvidenceQuality.Complete) => "Windows installed-app registrations and WinGet identities were read completely.",
            (EvidenceQuality.Complete, _) => $"Windows installed-app registrations were read completely. WinGet identities are incomplete: {winGetResult.Detail}",
            _ => $"Some Windows installed-app registrations couldn't be read: {registryResult.Detail}"
        };
        return builder.Build(registrations, registryQuality, packages, winGetQuality, machine.Read(), timeProvider.GetUtcNow(), detail);
    }

    private static EvidenceQuality Quality(ProviderQuality quality) => quality switch
    {
        ProviderQuality.Complete => EvidenceQuality.Complete,
        ProviderQuality.Partial => EvidenceQuality.Partial,
        _ => EvidenceQuality.Unavailable
    };
}
