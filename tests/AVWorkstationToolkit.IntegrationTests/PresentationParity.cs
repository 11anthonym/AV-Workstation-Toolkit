using System.ComponentModel;
using System.Text.Json.Serialization;
using AVWorkstationToolkit.App.ViewModels;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;

namespace AVWorkstationToolkit.IntegrationTests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PresentationParityFixture(
    int SchemaVersion,
    string ScenarioId,
    IReadOnlyList<PresentationPackageFixture> Packages,
    IReadOnlyList<PresentationCaseFixture> Cases);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PresentationPackageFixture(
    string Id, string Name, string Vendor, string Profile, string Priority, string Risk,
    string Status, string Action, bool Installed, string InstalledVersion, string AvailableVersion,
    IReadOnlyList<string> ApplicationTypes, IReadOnlyList<string> Roles);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PresentationCaseFixture(
    string Id, IReadOnlyList<string> Profiles, string Priority, string Manufacturer,
    string Discipline, string Role, string Search, string QuickView,
    string SortMember, string SortDirection, IReadOnlyList<string> SelectedIds, bool RebootPending);

public sealed record PresentationParityResult(int SchemaVersion, string ScenarioId, IReadOnlyList<PresentationCaseResult> Cases);
public sealed record PresentationCaseResult(
    string Id, IReadOnlyList<string> VisibleIds, IReadOnlyList<string> SelectedIds,
    int InstallCount, int UpdateCount, bool InstallEnabled, bool UpdateEnabled,
    string InstallLabel, string UpdateLabel, string SelectionSummary, string QuickView, bool WarningVisible);

public static class PresentationParityEvaluator
{
    public static async Task<PresentationParityResult> EvaluateAsync(PresentationParityFixture fixture)
    {
        if (fixture.SchemaVersion != 1) throw new InvalidDataException($"Unsupported presentation parity schema: {fixture.SchemaVersion}.");
        var results = new List<PresentationCaseResult>();
        foreach (var presentationCase in fixture.Cases)
        {
            using var viewModel = new MainWindowViewModel(new FixedCoordinator(CreatePlan(fixture.Packages, presentationCase.RebootPending)));
            await viewModel.RefreshAsync().ConfigureAwait(false);
            viewModel.StandardProfile = presentationCase.Profiles.Contains("Standard", StringComparer.Ordinal);
            viewModel.FieldProfile = presentationCase.Profiles.Contains("Field", StringComparer.Ordinal);
            viewModel.DeveloperProfile = presentationCase.Profiles.Contains("Developer", StringComparer.Ordinal);
            viewModel.OptionalProfile = presentationCase.Profiles.Contains("Optional", StringComparer.Ordinal);
            viewModel.SelectedPriority = viewModel.PriorityOptions.Single(item => item.Value?.ToToken() == presentationCase.Priority ||
                item.Value is null && presentationCase.Priority == "All");
            viewModel.SelectedManufacturer = viewModel.ManufacturerOptions.Single(item => item.Value == presentationCase.Manufacturer);
            viewModel.SelectedDiscipline = viewModel.DisciplineOptions.Single(item => item.Value == CatalogTokens.Parse<CatalogDiscipline>(presentationCase.Discipline, "Discipline"));
            viewModel.SelectedRole = viewModel.RoleOptions.Single(item => item.Value?.ToToken() == presentationCase.Role || item.Value is null && presentationCase.Role == "All");
            viewModel.SearchText = presentationCase.Search;
            viewModel.QuickViewCommand.Execute(presentationCase.QuickView);
            foreach (var id in presentationCase.SelectedIds)
            {
                var row = viewModel.Packages.Single(item => item.Id == id);
                row.Selected = true;
            }
            if (presentationCase.SortMember.Length > 0)
                viewModel.SetSort(presentationCase.SortMember, Enum.Parse<ListSortDirection>(presentationCase.SortDirection, false));
            results.Add(new(
                presentationCase.Id,
                viewModel.VisiblePackages.Select(item => item.Id).ToArray(),
                viewModel.Packages.Where(item => item.Selected).Select(item => item.Id).ToArray(),
                viewModel.InstallCount,
                viewModel.UpdateCount,
                viewModel.CanInstall,
                viewModel.CanUpdate,
                viewModel.InstallButtonText,
                viewModel.UpdateButtonText,
                viewModel.SelectionSummary,
                viewModel.QuickView.ToToken(),
                viewModel.WarningVisible));
        }
        return new(1, fixture.ScenarioId, results);
    }

    private static WorkstationPlan CreatePlan(IReadOnlyList<PresentationPackageFixture> packages, bool rebootPending)
    {
        var states = packages.Select(item =>
        {
            var definition = new PackageDefinition(
                item.Id, item.Name, item.Vendor, item.Name, item.Name,
                ProviderKind.WinGet, CatalogAuthority.ManagedWinGet,
                CatalogTokens.Parse<PackageProfile>(item.Profile, $"{item.Id}.Profile"),
                CatalogTokens.Parse<PackagePriority>(item.Priority, $"{item.Id}.Priority"),
                CatalogTokens.Parse<PackageRisk>(item.Risk, $"{item.Id}.Risk"),
                DeploymentPolicy.Allowlisted, MaintenancePolicy.Allowlisted, DeploymentClass.Managed,
                CatalogMaintenancePolicy.Managed, VersionRule.Latest, VersionCouplingMode.Independent, string.Empty,
                Lifecycle.Current,
                item.ApplicationTypes.Select(value => CatalogTokens.Parse<ApplicationType>(value, $"{item.Id}.ApplicationTypes")).ToArray(),
                item.Roles.Select(value => CatalogTokens.Parse<PackageRole>(value, $"{item.Id}.Roles")).ToArray(),
                [], [LicensingModel.Free], ["PUBLIC-DL"], DistributionPolicy.PackageManagerOnly,
                [InstallationForm.WinGet], [SupportedOperatingSystem.Windows], DeliveryMode.None, ReleaseMode.None,
                DetectionMode.WinGet, DetectionVersionPolicy.None, string.Empty, string.Empty, string.Empty, string.Empty, [],
                false, false, false, null, null,
                item.Risk == "Driver", item.Risk == "Service", item.Risk == "Listener", false, string.Empty, []);
            var status = CatalogTokens.Parse<PackageStatus>(item.Status, $"{item.Id}.Status");
            var action = CatalogTokens.Parse<PackageAction>(item.Action, $"{item.Id}.Action");
            return new PackageState(definition, item.Installed, item.InstalledVersion,
                item.Installed ? [item.InstalledVersion] : [], item.AvailableVersion,
                status == PackageStatus.UpdateAvailable, status, status.ToToken(), status.ToToken(), action, InventoryQuality.Complete);
        }).ToArray();
        var summary = new WorkstationPlanSummary(
            states.Length,
            Count(PackageStatus.Current), Count(PackageStatus.Missing), Count(PackageStatus.UpdateAvailable),
            Count(PackageStatus.Manual), Count(PackageStatus.ManualUpdate), Count(PackageStatus.Held),
            Count(PackageStatus.Inventory), Count(PackageStatus.NotDetected), Count(PackageStatus.InventoryIncomplete),
            Count(PackageStatus.InventoryUnavailable), Count(PackageStatus.CheckUnavailable), Count(PackageStatus.Awareness), Count(PackageStatus.Error));
        var reboot = rebootPending ? new RebootState(true, [RebootReason.WindowsUpdate], "Windows Update") : RebootState.Clear;
        return new(states, summary, reboot,
            new(ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, []));

        int Count(PackageStatus status) => states.Count(item => item.Status == status);
    }

    private sealed class FixedCoordinator(WorkstationPlan plan) : IWorkstationPlanningCoordinator
    {
        public Task<WorkstationPlan> RefreshAsync(IProgress<PlanningRefreshStage>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(plan);
    }
}
