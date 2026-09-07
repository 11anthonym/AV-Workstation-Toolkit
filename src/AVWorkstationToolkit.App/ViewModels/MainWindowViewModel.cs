using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using AVWorkstationToolkit.App.Commands;
using AVWorkstationToolkit.Application.Details;
using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Application.Providers;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.Application.Compatibility;

namespace AVWorkstationToolkit.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IWorkstationPlanningCoordinator coordinator;
    private readonly IReadOnlyDiagnosticsService diagnosticsService;
    private readonly CatalogDetailService detailService;
    private readonly IDiagnosticsExportService? diagnosticsExportService;
    private readonly IValidatedUserHandoffService? handoffService;
    private readonly IPackageDeliveryWorkflow? packageDeliveryWorkflow;
    private readonly IApplicationMenuWorkflow? applicationMenuWorkflow;
    private readonly CompatibilityCatalogQueryService? compatibilityService;
    private readonly CatalogQueryService queryService = new();
    private readonly CompiledActionCoordinator? actionCoordinator;
    private readonly ObservableCollection<PackageRowViewModel> packages = [];
    private readonly ObservableCollection<PackageRowViewModel> visiblePackages = [];
    private readonly ObservableCollection<CompatibilitySearchResultViewModel> compatibilityMatches = [];
    private CancellationTokenSource? refreshCancellation;
    private long refreshGeneration;
    private WorkstationPlan? plan;
    private PackageCatalog? planCatalog;
    private DiagnosticsViewModel? diagnostics;
    private CatalogDetailViewModel? selectedDetail;
    private CompatibilityDetailViewModel? selectedCompatibilityDetail;
    private CompatibilitySearchOutcome compatibilitySearchOutcome = CompatibilitySearchOutcome.NoDeviceOrCatalogMatch;
    private bool isBusy;
    private string searchText = string.Empty;
    private bool standardProfile = true;
    private bool fieldProfile = true;
    private bool developerProfile = true;
    private bool optionalProfile = true;
    private FilterOption<PackagePriority?> selectedPriority;
    private FilterOption<CatalogPreset> selectedCatalogPreset;
    private FilterOption<string> selectedManufacturer;
    private FilterOption<CatalogDiscipline> selectedDiscipline;
    private FilterOption<PackageRole?> selectedRole;
    private QuickView quickView;
    private PackageRowViewModel? selectedRow;
    private string sortMemberPath = string.Empty;
    private ListSortDirection? sortDirection;
    private string activityText = string.Empty;
    private string activityState = "Ready";
    private string statusText = "Loading catalog and workstation state...";
    private int mutationRefusalCount;
    private bool actionActive;
    private bool riskAcknowledged;
    private CompiledActionSnapshot actionSnapshot = new(CompiledActionState.Idle, string.Empty, "Migration action mode is idle.", [], null);
    private int refreshInvocationCount;

    public MainWindowViewModel(
        IWorkstationPlanningCoordinator coordinator,
        IReadOnlyDiagnosticsService? diagnosticsService = null,
        CatalogDetailService? detailService = null,
        CompiledActionCoordinator? actionCoordinator = null,
        IDiagnosticsExportService? diagnosticsExportService = null,
        IValidatedUserHandoffService? handoffService = null,
        IPackageDeliveryWorkflow? packageDeliveryWorkflow = null,
        IApplicationMenuWorkflow? applicationMenuWorkflow = null,
        bool liveRehearsalMode = false,
        CompatibilityCatalogQueryService? compatibilityService = null)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.diagnosticsService = diagnosticsService ?? CreateUnavailableDiagnosticsService();
        this.detailService = detailService ?? new CatalogDetailService();
        this.actionCoordinator = actionCoordinator;
        this.diagnosticsExportService = diagnosticsExportService;
        this.handoffService = handoffService;
        this.packageDeliveryWorkflow = packageDeliveryWorkflow;
        this.applicationMenuWorkflow = applicationMenuWorkflow;
        this.compatibilityService = compatibilityService;
        LiveRehearsalMode = liveRehearsalMode;
        if (actionCoordinator is not null) actionCoordinator.StateChanged += ActionCoordinator_StateChanged;
        PriorityOptions =
        [
            new("All priorities", null), new("P1", PackagePriority.P1), new("P2", PackagePriority.P2),
            new("Utility", PackagePriority.Utility), new("Developer", PackagePriority.Dev)
        ];
        CatalogPresetOptions =
        [
            new("All catalog", CatalogPreset.All), new("P1 field candidates", CatalogPreset.P1),
            new("Obtain before onsite", CatalogPreset.Onsite), new("Free tools", CatalogPreset.Free),
            new("Free public downloads", CatalogPreset.FreePublic), new("Dealer login required", CatalogPreset.Dealer),
            new("Licensed software", CatalogPreset.Licensed), new("Drivers", CatalogPreset.Drivers),
            new("Services / listeners", CatalogPreset.Services), new("Firmware utilities", CatalogPreset.Firmware),
            new("Current software", CatalogPreset.Current), new("Legacy / transition", CatalogPreset.Legacy),
            new("Known, not managed", CatalogPreset.Unmanaged), new("Installed, source limited", CatalogPreset.InstalledSourceLimited)
        ];
        DisciplineOptions = Enum.GetValues<CatalogDiscipline>()
            .Select(value => new FilterOption<CatalogDiscipline>(DisciplineLabel(value), value)).ToArray();
        RoleOptions = new[] { new FilterOption<PackageRole?>("All roles", null) }
            .Concat(Enum.GetValues<PackageRole>().Select(value => new FilterOption<PackageRole?>(SplitWords(value.ToString()), value))).ToArray();
        ManufacturerOptions = new ObservableCollection<FilterOption<string>> { new("All manufacturers", "All") };
        selectedPriority = PriorityOptions[0];
        selectedCatalogPreset = CatalogPresetOptions[0];
        selectedManufacturer = ManufacturerOptions[0];
        selectedDiscipline = DisciplineOptions[0];
        selectedRole = RoleOptions[0];

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        QuickViewCommand = new RelayCommand(value => SetQuickView(ParseQuickView(value)), _ => !IsBusy);
        ClearSelectionCommand = new RelayCommand(_ => ClearSelection(), _ => !IsBusy && packages.Any(item => item.Selected));
        InstallCommand = new AsyncRelayCommand(() => RunActionAsync(ManagedRequestAction.Install), () => CanInstall);
        UpdateCommand = new AsyncRelayCommand(() => RunActionAsync(ManagedRequestAction.Update), () => CanUpdate);
        CancelActionCommand = new AsyncRelayCommand(CancelActionAsync, () => CanCancelAction);
        DetailsCommand = new RelayCommand(_ => ShowSelectedDetails(), _ => SelectedRow is not null);
        GetPackageCommand = new AsyncRelayCommand(GetPackageAsync, CanGetPackage);
        DiagnosticsCommand = new RelayCommand(_ => ShowDiagnostics(), _ => plan is not null && !IsBusy);
        ExportPlanCommand = new RelayCommand(_ => ExportPlan(), _ => plan is not null && !IsBusy && applicationMenuWorkflow is not null);
        OpenLogsCommand = new RelayCommand(_ => OpenLogs(), _ => !IsBusy && applicationMenuWorkflow is not null);
        SafetySecurityCommand = new RelayCommand(_ => SafetySecurityRequested?.Invoke());
        AboutCommand = new RelayCommand(_ => AboutRequested?.Invoke());
    }

    public ReadOnlyObservableCollection<PackageRowViewModel> Packages => new(packages);
    public ReadOnlyObservableCollection<PackageRowViewModel> VisiblePackages => new(visiblePackages);
    public ReadOnlyObservableCollection<CompatibilitySearchResultViewModel> CompatibilityMatches => new(compatibilityMatches);
    public IReadOnlyList<FilterOption<PackagePriority?>> PriorityOptions { get; }
    public IReadOnlyList<FilterOption<CatalogPreset>> CatalogPresetOptions { get; }
    public ObservableCollection<FilterOption<string>> ManufacturerOptions { get; }
    public IReadOnlyList<FilterOption<CatalogDiscipline>> DisciplineOptions { get; }
    public IReadOnlyList<FilterOption<PackageRole?>> RoleOptions { get; }

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand QuickViewCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand UpdateCommand { get; }
    public AsyncRelayCommand CancelActionCommand { get; }
    public RelayCommand DetailsCommand { get; }
    public AsyncRelayCommand GetPackageCommand { get; }
    public RelayCommand DiagnosticsCommand { get; }
    public RelayCommand ExportPlanCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand SafetySecurityCommand { get; }
    public RelayCommand AboutCommand { get; }
    public ICommand ExitCommand { get; } = new RelayCommand(_ => System.Windows.Application.Current?.Shutdown());
    public event Action<CatalogDetailViewModel>? DetailRequested;
    public event Action<CompatibilityDetailViewModel>? CompatibilityDetailRequested;
    public event Action<DiagnosticsViewModel>? DiagnosticsRequested;
    public event Action? SafetySecurityRequested;
    public event Action? AboutRequested;

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!SetProperty(ref isBusy, value)) return;
            foreach (var item in packages) item.SetBusy(value || actionActive);
            RaiseCommandStates();
            OnPropertyChanged(nameof(IsNotBusy));
            OnPropertyChanged(nameof(ActionProgressVisible));
        }
    }
    public bool IsNotBusy => !IsBusy;
    public bool ActionProgressVisible => IsBusy || actionActive;
    public bool MigrationActionMode => actionCoordinator is not null;
    public bool LiveRehearsalMode { get; }
    public bool CanCancelAction => ActionSnapshot.State == CompiledActionState.Running;
    public bool RiskAcknowledged
    {
        get => riskAcknowledged;
        set
        {
            if (!SetProperty(ref riskAcknowledged, value)) return;
            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(CanUpdate));
            InstallCommand.RaiseCanExecuteChanged();
            UpdateCommand.RaiseCanExecuteChanged();
        }
    }
    public CompiledActionSnapshot ActionSnapshot { get => actionSnapshot; private set => SetProperty(ref actionSnapshot, value); }

    public string SearchText
    {
        get => searchText;
        set
        {
            if (!SetProperty(ref searchText, value ?? string.Empty)) return;
            RebuildVisible();
            RebuildCompatibilityMatches();
        }
    }
    public bool CompatibilityMatchesVisible => compatibilityMatches.Count > 0;
    public bool CompatibilitySearchOutcomeVisible => SearchText.Trim().Length >= 2 && compatibilityMatches.Count == 0;
    public string CompatibilitySearchOutcomeText => compatibilitySearchOutcome switch
    {
        CompatibilitySearchOutcome.NoVerifiedRelationshipInCurrentCatalog =>
            "No verified device/software relationship is recorded for this model in the current catalog.",
        CompatibilitySearchOutcome.NoDeviceOrCatalogMatch =>
            "No software or device catalog match was found. This does not mean the device has no required software.",
        _ => string.Empty
    };
    public string CompatibilityMatchSummary => compatibilityMatches.Count == 1
        ? "1 compatibility match"
        : $"{compatibilityMatches.Count} compatibility matches";
    public bool StandardProfile { get => standardProfile; set { if (SetProperty(ref standardProfile, value)) RebuildVisible(); } }
    public bool FieldProfile { get => fieldProfile; set { if (SetProperty(ref fieldProfile, value)) RebuildVisible(); } }
    public bool DeveloperProfile { get => developerProfile; set { if (SetProperty(ref developerProfile, value)) RebuildVisible(); } }
    public bool OptionalProfile { get => optionalProfile; set { if (SetProperty(ref optionalProfile, value)) RebuildVisible(); } }
    public FilterOption<PackagePriority?> SelectedPriority { get => selectedPriority; set { if (SetProperty(ref selectedPriority, value)) RebuildVisible(); } }
    public FilterOption<CatalogPreset> SelectedCatalogPreset { get => selectedCatalogPreset; set { if (SetProperty(ref selectedCatalogPreset, value)) RebuildVisible(); } }
    public FilterOption<string> SelectedManufacturer { get => selectedManufacturer; set { if (SetProperty(ref selectedManufacturer, value)) RebuildVisible(); } }
    public FilterOption<CatalogDiscipline> SelectedDiscipline { get => selectedDiscipline; set { if (SetProperty(ref selectedDiscipline, value)) RebuildVisible(); } }
    public FilterOption<PackageRole?> SelectedRole { get => selectedRole; set { if (SetProperty(ref selectedRole, value)) RebuildVisible(); } }

    public QuickView QuickView
    {
        get => quickView;
        private set
        {
            if (!SetProperty(ref quickView, value)) return;
            OnPropertyChanged(nameof(QuickViewText));
            OnPropertyChanged(nameof(IsAllQuickView));
            OnPropertyChanged(nameof(IsMissingQuickView));
            OnPropertyChanged(nameof(IsUpdatesQuickView));
        }
    }
    public string QuickViewText => QuickView switch { QuickView.Missing => "Showing: Missing apps", QuickView.Updates => "Showing: Available updates", _ => "Showing: All apps" };
    public bool IsAllQuickView => QuickView == QuickView.All;
    public bool IsMissingQuickView => QuickView == QuickView.Missing;
    public bool IsUpdatesQuickView => QuickView == QuickView.Updates;

    public PackageRowViewModel? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (!SetProperty(ref selectedRow, value)) return;
            if (selectedDetail is not null && !selectedDetail.Detail.PackageId.Equals(value?.Id, StringComparison.OrdinalIgnoreCase))
                SelectedDetail = null;
            DetailsCommand.RaiseCanExecuteChanged();
            GetPackageCommand.RaiseCanExecuteChanged();
        }
    }

    public DiagnosticsViewModel? Diagnostics
    {
        get => diagnostics;
        private set
        {
            if (!SetProperty(ref diagnostics, value)) return;
            DiagnosticsCommand.RaiseCanExecuteChanged();
        }
    }

    public CatalogDetailViewModel? SelectedDetail
    {
        get => selectedDetail;
        private set => SetProperty(ref selectedDetail, value);
    }

    public CompatibilityDetailViewModel? SelectedCompatibilityDetail
    {
        get => selectedCompatibilityDetail;
        private set => SetProperty(ref selectedCompatibilityDetail, value);
    }

    public string ActivityText { get => activityText; private set => SetProperty(ref activityText, value); }
    public string ActivityState { get => activityState; private set => SetProperty(ref activityState, value); }
    public string StatusText { get => statusText; private set => SetProperty(ref statusText, value); }
    public string CurrentCount => (plan?.Summary.Current ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string ActionCount => (plan is null ? 0 : plan.Summary.ManagedActions + plan.Summary.Manual + plan.Summary.ManualUpdates).ToString(System.Globalization.CultureInfo.InvariantCulture);
    public int InstallCount => packages.Count(item => item.Selected && item.CanSelect && item.Action == PackageAction.Install);
    public int UpdateCount => packages.Count(item => item.Selected && item.CanSelect && item.Action == PackageAction.Update);
    public int SelectedCount => InstallCount + UpdateCount;
    public string InstallButtonText => InstallCount == 0 ? "Install selected" : $"Install selected ({InstallCount})";
    public string UpdateButtonText => UpdateCount == 0 ? "Update selected" : $"Update selected ({UpdateCount})";
    public string SelectionSummary => SelectedCount == 0 ? "Nothing selected" : $"{SelectedCount} selected | {InstallCount} install | {UpdateCount} update";
    public bool CanInstall => !IsBusy && !actionActive && InstallCount > 0 && !SelectedRiskBlocked(PackageAction.Install) && !SelectedRiskNeedsAcknowledgement(PackageAction.Install);
    public bool CanUpdate => !IsBusy && !actionActive && UpdateCount > 0 && !SelectedRiskBlocked(PackageAction.Update) && !SelectedRiskNeedsAcknowledgement(PackageAction.Update);
    public bool RiskAcknowledgementRequired => packages.Any(item => item.Selected && item.Risk != PackageRisk.None);
    public bool RiskAcknowledgementVisible => MigrationActionMode && RiskAcknowledgementRequired;
    public bool WarningVisible => plan is not null && (plan.Reboot.Pending || plan.Providers.Warnings.Count > 0);
    public string WarningText => plan is null ? string.Empty : plan.Reboot.Pending
        ? "Restart recommended. Windows is waiting for a restart to finish an update. You can still select most apps, but system-level changes remain paused until you restart."
        : $"Workstation inventory completed with warnings. {plan.Providers.Warnings.Count} subsystem warning(s) may make some states incomplete.";
    public int MutationRefusalCount => mutationRefusalCount;
    internal int RefreshInvocationCount => refreshInvocationCount;
    public string SortMemberPath => sortMemberPath;
    public ListSortDirection? SortDirection => sortDirection;

    public async Task RefreshAsync()
    {
        refreshInvocationCount++;
        var generation = Interlocked.Increment(ref refreshGeneration);
        var previous = Interlocked.Exchange(ref refreshCancellation, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();
        var cancellation = refreshCancellation;
        var selectedIds = packages.Where(item => item.Selected).Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedRowId = SelectedRow?.Id;
        IsBusy = true;
        AppendActivity("Refreshing allowlisted application state.");
        var progress = new Progress<PlanningRefreshStage>(stage =>
        {
            if (generation != Volatile.Read(ref refreshGeneration)) return;
            ActivityState = StageText(stage);
            AppendActivity(ActivityState);
        });
        try
        {
            var result = await coordinator.RefreshAsync(progress, cancellation.Token).ConfigureAwait(true);
            var refreshedDiagnostics = await diagnosticsService.ComposeAsync(result, cancellation.Token).ConfigureAwait(true);
            if (generation != Volatile.Read(ref refreshGeneration)) return;
            ApplyPlan(result, selectedIds, selectedRowId);
            Diagnostics = new DiagnosticsViewModel(refreshedDiagnostics, ActionDiagnosticText(), diagnosticsExportService);
            AppendActivity(result.Providers.Warnings.Count == 0 ? "Plan ready." : "Plan ready with inventory warnings.");
        }
        catch (OperationCanceledException) when (generation != Volatile.Read(ref refreshGeneration) || cancellation.IsCancellationRequested)
        {
            // A newer refresh owns the presentation state.
        }
        catch (Exception exception)
        {
            if (generation != Volatile.Read(ref refreshGeneration)) return;
            StatusText = "Refresh failed. Existing plan data was retained.";
            ActivityState = "Refresh failed";
            AppendActivity($"ERROR {Sanitize(exception.Message)}");
        }
        finally
        {
            if (generation == Volatile.Read(ref refreshGeneration))
            {
                IsBusy = false;
                ActivityState = "Ready";
            }
        }
    }

    public void SetSort(string memberPath, ListSortDirection? requestedDirection = null)
    {
        if (!AllowedSortMembers.Contains(memberPath, StringComparer.Ordinal)) return;
        sortDirection = requestedDirection ?? (sortMemberPath == memberPath && sortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending : ListSortDirection.Ascending);
        sortMemberPath = memberPath;
        OnPropertyChanged(nameof(SortMemberPath));
        OnPropertyChanged(nameof(SortDirection));
        RebuildVisible();
    }

    public void ClearSelection()
    {
        foreach (var item in packages) item.Selected = false;
        UpdateSelectionState();
    }

    public void Dispose()
    {
        if (actionCoordinator is not null) actionCoordinator.StateChanged -= ActionCoordinator_StateChanged;
        refreshCancellation?.Cancel();
        refreshCancellation?.Dispose();
    }

    private void ApplyPlan(WorkstationPlan result, IReadOnlySet<string> selectedIds, string? selectedRowId)
    {
        RiskAcknowledged = false;
        var retainedManufacturer = SelectedManufacturer.Value;
        plan = result;
        planCatalog = new PackageCatalog(result.Packages.Select(item => item.Package));
        packages.Clear();
        var order = 0;
        foreach (var state in result.Packages)
        {
            var row = new PackageRowViewModel(state, order++, UpdateSelectionState);
            row.SetBusy(IsBusy);
            packages.Add(row);
            if (selectedIds.Contains(row.Id) && row.CanSelect) row.RestoreSelection();
        }
        ManufacturerOptions.Clear();
        ManufacturerOptions.Add(new("All manufacturers", "All"));
        foreach (var vendor in CatalogQueryService.Manufacturers(result.Packages.Select(item => item.Package)))
            ManufacturerOptions.Add(new(vendor, vendor));
        SelectedManufacturer = ManufacturerOptions.FirstOrDefault(item => item.Value.Equals(retainedManufacturer, StringComparison.OrdinalIgnoreCase))
            ?? ManufacturerOptions[0];
        RebuildVisible();
        SelectedRow = selectedRowId is null
            ? null
            : visiblePackages.FirstOrDefault(item => item.Id.Equals(selectedRowId, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(CurrentCount));
        OnPropertyChanged(nameof(ActionCount));
        OnPropertyChanged(nameof(WarningVisible));
        OnPropertyChanged(nameof(WarningText));
        DiagnosticsCommand.RaiseCanExecuteChanged();
    }

    private void RebuildVisible()
    {
        if (plan is null) return;
        var profiles = new HashSet<PackageProfile>();
        if (StandardProfile) profiles.Add(PackageProfile.Standard);
        if (FieldProfile) profiles.Add(PackageProfile.Field);
        if (DeveloperProfile) profiles.Add(PackageProfile.Developer);
        if (OptionalProfile) profiles.Add(PackageProfile.Optional);
        IEnumerable<PackageRowViewModel> rows = profiles.Count == 0 ? [] : queryService.Apply(
            packages.Select(item => new CatalogQueryItem(item.Package, item.Status, item.State.Installed, item.State.AvailableVersion)),
            new CatalogQuery(
                profiles,
                SelectedPriority.Value is null ? new HashSet<PackagePriority>() : new HashSet<PackagePriority> { SelectedPriority.Value.Value },
                SelectedManufacturer.Value,
                SelectedDiscipline.Value,
                SelectedRole.Value is null ? new HashSet<PackageRole>() : new HashSet<PackageRole> { SelectedRole.Value.Value },
                SearchText,
                QuickView,
                SelectedCatalogPreset.Value))
            .Select(item => packages.First(row => ReferenceEquals(row.Package, item.Package)));
        rows = ApplySort(rows);
        visiblePackages.Clear();
        foreach (var row in rows) visiblePackages.Add(row);
        if (SelectedRow is not null && !visiblePackages.Contains(SelectedRow)) SelectedRow = null;
        UpdateStatusText();
    }

    private void RebuildCompatibilityMatches()
    {
        compatibilityMatches.Clear();
        var query = SearchText.Trim();
        if (compatibilityService is null || query.Length < 2)
        {
            compatibilitySearchOutcome = CompatibilitySearchOutcome.NoDeviceOrCatalogMatch;
            NotifyCompatibilityMatchesChanged();
            return;
        }

        var devices = compatibilityService.SearchDevices(query);
        compatibilitySearchOutcome = compatibilityService.GetSearchOutcome(query);
        foreach (var device in devices)
        {
            var displayName = CompatibilityDetailViewModel.DeviceDisplayName(device, query);
            var softwareCount = compatibilityService.GetSoftwareForDevice(device).Sum(group => group.Software.Count);
            compatibilityMatches.Add(new CompatibilitySearchResultViewModel(
                CompatibilitySearchResultKind.Device,
                displayName,
                $"{DeviceMatchLabel(device.MatchKind)} | {device.DeviceFamilyId} | {softwareCount} reviewed software relationship(s)",
                "View software grouped by field-service purpose. This read-only result cannot be selected for install or update.",
                () => OpenCompatibilityDeviceAsync(device, displayName)));
        }

        foreach (var product in compatibilityService.SearchProducts(query))
        {
            compatibilityMatches.Add(new CompatibilitySearchResultViewModel(
                CompatibilitySearchResultKind.Software,
                product.Name,
                $"{product.Vendor} | {CompatibilityLabel(product.Lifecycle)}",
                "View release families, installed evidence, and applicable devices.",
                () => OpenCompatibilityProductAsync(product.Id)));
        }
        NotifyCompatibilityMatchesChanged();
    }

    internal async Task OpenCompatibilityProductAsync(SoftwareProductId productId)
    {
        if (compatibilityService is null) return;
        var detail = await CompatibilityDetailViewModel.CreateProductAsync(compatibilityService, productId, handoffService).ConfigureAwait(true);
        SelectedCompatibilityDetail = detail;
        CompatibilityDetailRequested?.Invoke(detail);
    }

    private Task OpenCompatibilityDeviceAsync(CompatibilityDeviceSearchResult device, string displayName)
    {
        if (compatibilityService is null) return Task.CompletedTask;
        var detail = CompatibilityDetailViewModel.CreateDevice(compatibilityService, device, displayName, handoffService);
        SelectedCompatibilityDetail = detail;
        CompatibilityDetailRequested?.Invoke(detail);
        return Task.CompletedTask;
    }

    private void NotifyCompatibilityMatchesChanged()
    {
        OnPropertyChanged(nameof(CompatibilityMatchesVisible));
        OnPropertyChanged(nameof(CompatibilityMatchSummary));
        OnPropertyChanged(nameof(CompatibilitySearchOutcomeVisible));
        OnPropertyChanged(nameof(CompatibilitySearchOutcomeText));
    }

    private static string DeviceMatchLabel(CompatibilitySearchMatchKind kind) => kind switch
    {
        CompatibilitySearchMatchKind.ExactModelOrAlias => "Exact verified model or alias",
        CompatibilitySearchMatchKind.ExactDeviceFamily => "Exact verified device family",
        CompatibilitySearchMatchKind.NormalizedExact => "Verified normalized model or alias",
        CompatibilitySearchMatchKind.PrefixOrToken => "Verified family or alias match",
        _ => "Verified related match"
    };

    private static string CompatibilityLabel(Enum value)
    {
        var text = value.ToString();
        return string.Concat(text.Select((character, index) => index > 0 && char.IsUpper(character) && char.IsLower(text[index - 1])
            ? $" {character}" : character.ToString()));
    }

    private IEnumerable<PackageRowViewModel> ApplySort(IEnumerable<PackageRowViewModel> rows)
    {
        if (sortDirection is null || sortMemberPath.Length == 0) return rows.OrderBy(item => item.Order);
        Func<PackageRowViewModel, object> key = sortMemberPath switch
        {
            "ApplicationSortKey" => item => item.ApplicationSortKey,
            "VendorSortKey" => item => item.VendorSortKey,
            "PrioritySortKey" => item => item.PrioritySortKey,
            "StatusSortKey" => item => item.StatusSortKey,
            "VersionSortKey" => item => item.VersionSortKey,
            "RiskSortKey" => item => item.RiskSortKey,
            _ => item => item.Order
        };
        var ordered = sortDirection == ListSortDirection.Ascending
            ? rows.OrderBy(key, ObjectComparer.Instance)
            : rows.OrderByDescending(key, ObjectComparer.Instance);
        return ordered.ThenBy(item => item.StableSortKey, StringComparer.Ordinal);
    }

    private void SetQuickView(QuickView view)
    {
        QuickView = view;
        RebuildVisible();
        if (view == QuickView.All) return;
        foreach (var item in packages) item.Selected = false;
        var action = view == QuickView.Missing ? PackageAction.Install : PackageAction.Update;
        foreach (var item in visiblePackages.Where(item => item.CanSelect && item.Action == action)) item.Selected = true;
        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        RiskAcknowledged = false;
        OnPropertyChanged(nameof(InstallCount));
        OnPropertyChanged(nameof(UpdateCount));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(InstallButtonText));
        OnPropertyChanged(nameof(UpdateButtonText));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(RiskAcknowledgementRequired));
        OnPropertyChanged(nameof(RiskAcknowledgementVisible));
        RaiseCommandStates();
    }

    private bool SelectedRiskBlocked(PackageAction action) => plan?.Reboot.Pending == true &&
        packages.Any(item => item.Selected && item.Action == action && item.Risk != PackageRisk.None);

    private bool SelectedRiskNeedsAcknowledgement(PackageAction action) => !RiskAcknowledged &&
        packages.Any(item => item.Selected && item.Action == action && item.Risk != PackageRisk.None);

    private void RefuseMutation(PackageAction action)
    {
        mutationRefusalCount++;
        AppendActivity($"READ-ONLY This source preview did not run {action.ToString().ToLowerInvariant()}. Packaged production actions require the independently validating compiled worker.");
        ActivityState = "Read-only preview";
    }

    private async Task RunActionAsync(ManagedRequestAction action)
    {
        var riskAcknowledgedForThisRun = RiskAcknowledged;
        RiskAcknowledged = false;
        if (actionCoordinator is null)
        {
            RefuseMutation(action == ManagedRequestAction.Install ? PackageAction.Install : PackageAction.Update);
            return;
        }
        if (plan is null) return;
        var expected = action == ManagedRequestAction.Install ? PackageAction.Install : PackageAction.Update;
        var selected = packages.Where(item => item.Selected && item.Action == expected).Select(item => item.State).ToArray();
        try
        {
            var completed = await actionCoordinator.StartAsync(action, selected, plan, riskAcknowledgedForThisRun, dryRun: false).ConfigureAwait(true);
            ApplyPlan(completed.RefreshedPlan, new HashSet<string>(StringComparer.OrdinalIgnoreCase), SelectedRow?.Id);
            Diagnostics = new DiagnosticsViewModel(await diagnosticsService.ComposeAsync(completed.RefreshedPlan).ConfigureAwait(true), ActionDiagnosticText(), diagnosticsExportService);
            AppendActivity($"ACTION {completed.Result.Status}: {completed.Result.Message}");
        }
        catch (Exception exception)
        {
            AppendActivity($"ACTION ERROR {DiagnosticsRedactor.Sanitize(exception.Message)}");
        }
    }

    private async Task CancelActionAsync()
    {
        if (actionCoordinator is null) return;
        if (await actionCoordinator.RequestCancellationAsync().ConfigureAwait(true))
            AppendActivity("Cancellation intent recorded. The independent worker will stop between packages.");
    }

    private bool CanGetPackage() => !IsBusy && !actionActive && plan is not null && SelectedRow is not null &&
        packageDeliveryWorkflow?.CanHandle(SelectedRow.State, plan) == true;

    private async Task GetPackageAsync()
    {
        if (!CanGetPackage() || packageDeliveryWorkflow is null || SelectedRow is null || plan is null) return;
        IsBusy = true;
        ActivityState = "Getting package";
        try
        {
            var outcome = await packageDeliveryWorkflow.DeliverAsync(SelectedRow.State, plan).ConfigureAwait(true);
            AppendActivity($"PACKAGE {(outcome.Completed ? "READY" : "NOT READY")} {outcome.Detail}");
        }
        catch (Exception exception)
        {
            AppendActivity($"PACKAGE ERROR {DiagnosticsRedactor.Sanitize(exception.Message)}");
        }
        finally
        {
            IsBusy = false;
            ActivityState = "Ready";
        }
    }

    private void ActionCoordinator_StateChanged(object? sender, CompiledActionSnapshot snapshot)
    {
        void Apply()
        {
            ActionSnapshot = snapshot;
            actionActive = snapshot.State is CompiledActionState.Preparing or CompiledActionState.Running or CompiledActionState.CancellationRequested;
            ActivityState = snapshot.State.ToString();
            AppendActivity($"ACTION {snapshot.State}: {snapshot.Status}");
            foreach (var item in packages) item.SetBusy(IsBusy || actionActive);
            OnPropertyChanged(nameof(ActionProgressVisible));
            OnPropertyChanged(nameof(CanCancelAction));
            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(CanUpdate));
            RaiseCommandStates();
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.Invoke(Apply);
        else Apply();
    }

    private string ActionDiagnosticText()
    {
        var snapshot = ActionSnapshot;
        var lines = new List<string>
        {
            "[Compiled managed action]",
            $"State: {snapshot.State}",
            $"Request: {snapshot.RequestId}",
            $"Status: {snapshot.Status}",
            $"Progress records: {snapshot.Progress.Count}"
        };
        if (snapshot.Result is not null)
        {
            lines.Add($"Result: {snapshot.Result.Status}");
            lines.Add($"Packages: {snapshot.Result.Packages.Count}");
        }
        return DiagnosticsRedactor.Sanitize(string.Join(Environment.NewLine, lines));
    }

    private void ShowSelectedDetails()
    {
        if (SelectedRow is null || planCatalog is null) return;
        ExternalReleaseEvidence? release = null;
        plan?.ExternalReleases?.TryGetValue(SelectedRow.Id, out release);
        SelectedDetail = new CatalogDetailViewModel(detailService.Create(SelectedRow.State, planCatalog, release), handoffService);
        AppendActivity($"DETAILS {SelectedRow.Name} | {SelectedRow.Vendor} | {SelectedRow.StatusLabel} | {SelectedRow.StatusDetail}");
        DetailRequested?.Invoke(SelectedDetail);
    }

    private void ShowDiagnostics()
    {
        if (plan is null || Diagnostics is null) return;
        AppendActivity($"DIAGNOSTICS packages={plan.Summary.Total}; warnings={Diagnostics.WarningCount}; errors={Diagnostics.ErrorCount}; rebootPending={plan.Reboot.Pending}; compiledActions={(actionCoordinator is null ? "unavailable" : "available")}");
        DiagnosticsRequested?.Invoke(Diagnostics);
    }

    private void ExportPlan()
    {
        if (plan is null || applicationMenuWorkflow is null) return;
        try
        {
            var result = applicationMenuWorkflow.ExportPlan(plan);
            AppendActivity(result.Detail);
        }
        catch (Exception exception)
        {
            AppendActivity($"PLAN EXPORT ERROR {DiagnosticsRedactor.Sanitize(exception.Message)}");
        }
    }

    private void OpenLogs()
    {
        if (applicationMenuWorkflow is null) return;
        try
        {
            AppendActivity($"Opened logs: {applicationMenuWorkflow.OpenLogs()}");
        }
        catch (Exception exception)
        {
            AppendActivity($"OPEN LOGS ERROR {DiagnosticsRedactor.Sanitize(exception.Message)}");
        }
    }

    private void UpdateStatusText()
    {
        if (plan is null) return;
        StatusText = $"{visiblePackages.Count} shown of {plan.Summary.Total} | {plan.Summary.Current} current | {plan.Summary.ManagedActions} managed actions | {plan.Summary.ManualActions} manual | {plan.Summary.Inventory + plan.Summary.NotDetected} inventory | {plan.Summary.InventoryWarnings} warnings | {plan.Summary.Awareness} awareness";
    }

    private void AppendActivity(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {Sanitize(message)}";
        ActivityText = ActivityText.Length == 0 ? line : $"{ActivityText}{Environment.NewLine}{line}";
        if (ActivityText.Length > 32000) ActivityText = ActivityText[^32000..];
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        QuickViewCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
        InstallCommand.RaiseCanExecuteChanged();
        UpdateCommand.RaiseCanExecuteChanged();
        DiagnosticsCommand.RaiseCanExecuteChanged();
        ExportPlanCommand.RaiseCanExecuteChanged();
        OpenLogsCommand.RaiseCanExecuteChanged();
        CancelActionCommand.RaiseCanExecuteChanged();
        GetPackageCommand.RaiseCanExecuteChanged();
    }

    private static QuickView ParseQuickView(object? value) => Enum.TryParse<QuickView>(value?.ToString(), out var parsed) ? parsed : QuickView.All;
    private static string StageText(PlanningRefreshStage stage) => stage switch
    {
        PlanningRefreshStage.ReadingWinGetInventory => "Reading WinGet inventory...",
        PlanningRefreshStage.ReadingWinGetUpdates => "Reading WinGet updates...",
        PlanningRefreshStage.ReadingExternalInventory => "Reading installed AV software...",
        PlanningRefreshStage.ReadingExternalReleases => "Checking official vendor releases...",
        PlanningRefreshStage.CheckingRebootState => "Checking reboot state...",
        PlanningRefreshStage.BuildingPlan => "Building workstation plan...",
        _ => "Ready"
    };
    private static string Sanitize(string value) => new(DiagnosticsRedactor.Sanitize(value).Take(4000).ToArray());
    private static string SplitWords(string value) => System.Text.RegularExpressions.Regex.Replace(value, "(?<!^)([A-Z])", " $1", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static string DisciplineLabel(CatalogDiscipline value) => value switch
    {
        CatalogDiscipline.All => "All disciplines",
        CatalogDiscipline.DSP => "DSP / audio",
        CatalogDiscipline.AudioNetworking => "Audio networking",
        CatalogDiscipline.AVoIP => "AV over IP",
        CatalogDiscipline.RF => "RF / wireless",
        CatalogDiscipline.Conferencing => "Conferencing / PTZ",
        CatalogDiscipline.Displays => "Displays / signage",
        CatalogDiscipline.DvLED => "dvLED",
        CatalogDiscipline.Control => "Control systems",
        CatalogDiscipline.Broadcast => "Broadcast / video",
        CatalogDiscipline.MediaShow => "Media / show control",
        CatalogDiscipline.Utilities => "Field / network utilities",
        CatalogDiscipline.Measurement => "Measurement / analysis",
        CatalogDiscipline.FirmwareCommissioning => "Firmware / commissioning",
        _ => SplitWords(value.ToString())
    };
    private static readonly string[] AllowedSortMembers = ["ApplicationSortKey", "VendorSortKey", "PrioritySortKey", "StatusSortKey", "VersionSortKey", "RiskSortKey"];

    private static IReadOnlyDiagnosticsService CreateUnavailableDiagnosticsService() => new ReadOnlyDiagnosticsService(
        new UnavailableRuntimeDiagnosticsProvider(),
        new("Unknown", "Compiled migration test", "Unknown", "Unknown"));

    private sealed class UnavailableRuntimeDiagnosticsProvider : IRuntimeDiagnosticsProvider
    {
        public Task<RuntimeDiagnosticFacts> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var unknown = new DiagnosticValue(DiagnosticEvidenceState.Unknown, "Unknown");
            return Task.FromResult(new RuntimeDiagnosticFacts(unknown,
                new(DiagnosticEvidenceState.Unknown, "Not loaded by compiled app"), unknown, unknown, unknown, unknown, unknown));
        }
    }

    private sealed class ObjectComparer : IComparer<object>
    {
        public static ObjectComparer Instance { get; } = new();
        public int Compare(object? left, object? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            if (left is int leftInt && right is int rightInt) return leftInt.CompareTo(rightInt);
            return string.Compare(left.ToString(), right.ToString(), StringComparison.Ordinal);
        }
    }
}
