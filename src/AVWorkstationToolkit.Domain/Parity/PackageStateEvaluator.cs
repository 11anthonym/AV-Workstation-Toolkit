using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Domain.Policies;

namespace AVWorkstationToolkit.Domain.Parity;

public static class PackageStateEvaluator
{
    public static CanonicalParityResult Evaluate(ParityFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (fixture.SchemaVersion != 1) throw new InvalidDataException($"Unsupported parity fixture schema: {fixture.SchemaVersion}.");

        var installed = fixture.Winget.Installed.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var upgrades = fixture.Winget.Upgrades.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var reboot = new RebootState(fixture.Reboot.Pending, fixture.Reboot.Reasons.Select(ParseRebootReason).ToArray(), fixture.Reboot.Summary);
        var planner = new PlanningService();
        var policy = new SelectionPolicy();
        var states = fixture.Packages.Select(item =>
        {
            var package = Definition(item);
            var isInstalled = installed.TryGetValue(item.Id, out var installedRecord);
            upgrades.TryGetValue(item.Id, out var upgradeRecord);
            var evidence = new PackageEvidence(
                fixture.Winget.Available, isInstalled, fixture.Winget.Available,
                fixture.Winget.Available ? InventoryQuality.Complete : InventoryQuality.Unavailable,
                isInstalled, installedRecord?.Version ?? string.Empty,
                installedRecord is null ? [] : [installedRecord.Version], upgradeRecord is not null,
                upgradeRecord?.AvailableVersion ?? string.Empty, string.Empty, string.Empty);
            var state = planner.Evaluate(package, evidence);
            var decision = policy.Evaluate(state, reboot, true);
            return new CanonicalPackageState(
                package.Id, package.Provider.ToToken(), state.Installed, state.InstalledVersion, state.AvailableVersion,
                state.Status.ToToken(), state.StatusDetail, state.ReasonCode, package.Risk.ToToken(), state.CanSelect,
                state.Action.ToToken(), package.DeliveryMode.ToToken(), state.InventoryQuality.ToToken(), decision.IsAllowed);
        }).ToArray();
        return new CanonicalParityResult(1, fixture.ScenarioId, states);
    }

    private static PackageDefinition Definition(PackageFixture item)
    {
        var provider = CatalogTokens.Parse<ProviderKind>(item.Provider, $"{item.Id}.Provider");
        if (provider != ProviderKind.WinGet) throw new NotSupportedException("Planning fixture schema 1 supports managed WinGet records only.");
        return new(item.Id, item.Id, "Fixture", string.Empty, "Parity fixture", provider, CatalogAuthority.ManagedWinGet,
            PackageProfile.Standard, PackagePriority.P2, CatalogTokens.Parse<PackageRisk>(item.Risk, $"{item.Id}.Risk"),
            CatalogTokens.Parse<DeploymentPolicy>(item.Deployment, $"{item.Id}.Deployment"),
            CatalogTokens.Parse<MaintenancePolicy>(item.Maintenance, $"{item.Id}.Maintenance"),
            DeploymentClass.Managed, CatalogMaintenancePolicy.Managed, VersionRule.Latest, VersionCouplingMode.Independent,
            string.Empty, Lifecycle.Current, [], [], [], [LicensingModel.Free], ["PUBLIC-DL"], DistributionPolicy.PackageManagerOnly,
            [InstallationForm.WinGet], [SupportedOperatingSystem.Windows], DeliveryMode.None, ReleaseMode.None, DetectionMode.WinGet,
            DetectionVersionPolicy.None, string.Empty, string.Empty, string.Empty, string.Empty, [], null, null, null, null, null,
            item.Risk == "Driver", item.Risk == "Service", item.Risk == "Listener", null, string.Empty, []);
    }

    private static RebootReason ParseRebootReason(string reason) => reason switch
    {
        "Windows Update" or "WindowsUpdate" => RebootReason.WindowsUpdate,
        "Component Based Servicing" or "ComponentBasedServicing" or "CBS" => RebootReason.ComponentBasedServicing,
        _ => throw new InvalidDataException($"Unsupported reboot reason in parity fixture: {reason}.")
    };
}
