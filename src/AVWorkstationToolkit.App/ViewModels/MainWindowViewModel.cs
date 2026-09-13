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
using System.Windows.Threading;

namespace AVWorkstationToolkit.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private const int CompatibilityResultLimit = 24;
    private static readonly TimeSpan DefaultSearchDebounce = TimeSpan.FromMilliseconds(175);
    private readonly IWorkstationPlanningCoordinator coordinator;
    private readonly IReadOnlyDiagnosticsService diagnosticsService;
    private readonly CatalogDetailService detailService;
    private readonly IDiagnosticsExportService? diagnosticsExportService;
    private readonly IValidatedUserHandoffService? handoffService;
    private readonly IPackageDeliveryWorkflow? packageDeliveryWorkflow;
    private readonly IApplicationMenuWorkflow? applicationMenuWorkflow;
    private readonly CompatibilityCatalogQueryService? compatibilityService;
    private readonly IReferenceCatalogUpdateService? referenceCatalogUpdates;
    private readonly CatalogQueryService queryService = new();
    private readonly CompiledActionCoordinator? actionCoordinator;
    private readonly ObservableCollection<PackageRowViewModel> packages = [];
    private readonly TimeSpan searchDebounce;
    private readonly Dispatcher? uiDispatcher;
    private IReadOnlyList<PackageRowViewModel> visiblePackages = [];
    private IReadOnlyList<CompatibilitySearchResultViewModel> compatibilityMatches = [];
    private IReadOnlyList<PackageRowViewModel> packageSearchSnapshot = [];
    private CancellationTokenSource? refreshCancellation;
    private CancellationTokenSource? searchCancellation;
    private long refreshGeneration;
    private long searchGeneration;
    private WorkstationPlan? plan;
    private PackageCatalog? planCatalog;
    private DiagnosticsViewModel? diagnostics;
    private CatalogDetailViewModel? selectedDetail;
    private CompatibilityDetailViewModel? selectedCompatibilityDetail;
    private CompatibilitySearchOutcome compatibilitySearchOutcome = CompatibilitySearchOutcome.NoDeviceOrCatalogMatch;
    private bool isBusy;
    private string searchText = string.Empty;
    private bool searchInProgress;
    private int compatibilityTotalMatchCount;
    private Task searchCompletion = Task.CompletedTask;
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
        CompatibilityCatalogQueryService? compatibilityService = null,
        IReferenceCatalogUpdateService? referenceCatalogUpdates = null,
        TimeSpan? searchDebounce = null)
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
        this.referenceCatalogUpdates = referenceCatalogUpdates;
        this.searchDebounce = searchDebounce ?? DefaultSearchDebounce;
        if (this.searchDebounce < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(searchDebounce));
        uiDispatcher = System.Windows.Application.Current?.Dispatcher;
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
        CatalogUpdatesCommand = new RelayCommand(_ => CatalogUpdatesRequested?.Invoke(referenceCatalogUpdates!), _ => referenceCatalogUpdates is not null);
        AboutCommand = new RelayCommand(_ => AboutRequested?.Invoke());
    }

    public ReadOnlyObservableCollection<PackageRowViewModel> Packages => new(packages);
    public IReadOnlyList<PackageRowViewModel> VisiblePackages => visiblePackages;
    public IReadOnlyList<CompatibilitySearchResultViewModel> CompatibilityMatches => compatibilityMatches;
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
    public RelayCommand CatalogUpdatesCommand { get; }
    public RelayCommand AboutCommand { get; }
    public ICommand ExitCommand { get; } = new RelayCommand(_ => System.Windows.Application.Current?.Shutdown());
    public event Action<CatalogDetailViewModel>? DetailRequested;
    public event Action<CompatibilityDetailViewModel>? CompatibilityDetailRequested;
    public event Action<DiagnosticsViewModel>? DiagnosticsRequested;
    public event Action? SafetySecurityRequested;
    public event Action<IReferenceCatalogUpdateService>? CatalogUpdatesRequested;
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
            ScheduleSearch();
        }
    }
    public bool SearchInProgress { get => searchInProgress; private set { if (SetProperty(ref searchInProgress, value)) NotifySearchPresentationChanged(); } }
    public bool SearchStatusVisible => SearchInProgress || SearchText.Trim().Length > 0;
    public string SearchStatusText => SearchInProgress
        ? "Searching..."
        : SearchText.Trim().Length < 2
            ? SearchText.Trim().Length == 0 ? string.Empty : "Type at least 2 characters to search devices."
            : compatibilityTotalMatchCount > 0
                ? compatibilityTotalMatchCount == 1 ? "1 compatibility match" : $"{compatibilityTotalMatchCount} compatibility matches"
                : visiblePackages.Count == 1 ? "1 application match"
                : visiblePackages.Count > 1 ? $"{visiblePackages.Count} application matches"
                : "No matches";
    public bool CompatibilityMatchesVisible => !SearchInProgress && compatibilityMatches.Count > 0;
    public bool CompatibilitySearchOutcomeVisible => !SearchInProgress && SearchText.Trim().Length >= 2 && compatibilityMatches.Count == 0;
    public string CompatibilitySearchOutcomeText => compatibilitySearchOutcome switch
    {
        CompatibilitySearchOutcome.NoVerifiedRelationshipInCurrentCatalog =>
            "No verified device/software relationship is recorded for this model in the current catalog.",
        CompatibilitySearchOutcome.NoDeviceOrCatalogMatch =>
            "No software or device catalog match was found. This does not mean the device has no required software.",
        _ => string.Empty
    };
    public string CompatibilityMatchSummary => compatibilityTotalMatchCount > compatibilityMatches.Count
        ? $"{compatibilityMatches.Count} of {compatibilityTotalMatchCount} matches"
        : compatibilityMatches.Count == 1 ? "1 compatibility match" : $"{compatibilityMatches.Count} compatibility matches";
    internal Task SearchCompletion => searchCompletion;
    internal static int LiveCompatibilityResultLimit => CompatibilityResultLimit;
    public bool StandardProfile { get => standardProfile; set { if (SetProperty(ref standardProfile, value)) SearchStateChanged(); } }
    public bool FieldProfile { get => fieldProfile; set { if (SetProperty(ref fieldProfile, value)) SearchStateChanged(); } }
    public bool DeveloperProfile { get => developerProfile; set { if (SetProperty(ref developerProfile, value)) SearchStateChanged(); } }
    public bool OptionalProfile { get => optionalProfile; set { if (SetProperty(ref optionalProfile, value)) SearchStateChanged(); } }
    public FilterOption<PackagePriority?> SelectedPriority { get => selectedPriority; set { if (SetProperty(ref selectedPriority, value)) SearchStateChanged(); } }
    public FilterOption<CatalogPreset> SelectedCatalogPreset { get => selectedCatalogPreset; set { if (SetProperty(ref selectedCatalogPreset, value)) SearchStateChanged(); } }
    public FilterOption<string> SelectedManufacturer { get => selectedManufacturer; set { if (SetProperty(ref selectedManufacturer, value)) SearchStateChanged(); } }
    public FilterOption<CatalogDiscipline> SelectedDiscipline { get => selectedDiscipline; set { if (SetProperty(ref selectedDiscipline, value)) SearchStateChanged(); } }
    public FilterOption<PackageRole?> SelectedRole { get => selectedRole; set { if (SetProperty(ref selectedRole, value)) SearchStateChanged(); } }

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
        SearchStateChanged();
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
        searchCancellation?.Cancel();
        searchCancellation?.Dispose();
    }

    private void ApplyPlan(WorkstationPlan result, IReadOnlySet<string> selectedIds, string? selectedRowId)
    {
        InvalidatePendingSearch();
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
        packageSearchSnapshot = packages.ToArray();
        ManufacturerOptions.Clear();
        ManufacturerOptions.Add(new("All manufacturers", "All"));
        foreach (var vendor in CatalogQueryService.Manufacturers(result.Packages.Select(item => item.Package)))
            ManufacturerOptions.Add(new(vendor, vendor));
        SetProperty(ref selectedManufacturer,
            ManufacturerOptions.FirstOrDefault(item => item.Value.Equals(retainedManufacturer, StringComparison.OrdinalIgnoreCase))
                ?? ManufacturerOptions[0],
            nameof(SelectedManufacturer));
        RebuildVisible();
        SelectedRow = selectedRowId is null
            ? null
            : visiblePackages.FirstOrDefault(item => item.Id.Equals(selectedRowId, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(CurrentCount));
        OnPropertyChanged(nameof(ActionCount));
        OnPropertyChanged(nameof(WarningVisible));
        OnPropertyChanged(nameof(WarningText));
        DiagnosticsCommand.RaiseCanExecuteChanged();
        ScheduleSearch(useTextDebounce: false);
    }

    private void RebuildVisible()
    {
        if (plan is null) return;
        ApplyVisibleRows(ComputeVisibleRows(CreateSearchRequest(SearchText)));
    }

    private IReadOnlyList<PackageRowViewModel> ComputeVisibleRows(
        SearchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.HasPlan) return [];
        cancellationToken.ThrowIfCancellationRequested();
        var profiles = new HashSet<PackageProfile>();
        if (request.StandardProfile) profiles.Add(PackageProfile.Standard);
        if (request.FieldProfile) profiles.Add(PackageProfile.Field);
        if (request.DeveloperProfile) profiles.Add(PackageProfile.Developer);
        if (request.OptionalProfile) profiles.Add(PackageProfile.Optional);
        var rowsByPackage = request.PackageRows.ToDictionary(row => row.Package.Id, StringComparer.Ordinal);
        IEnumerable<PackageRowViewModel> rows = profiles.Count == 0 ? [] : queryService.Apply(
            request.PackageRows.Select(item => new CatalogQueryItem(item.Package, item.Status, item.State.Installed, item.State.AvailableVersion)),
            new CatalogQuery(
                profiles,
                request.Priority is null ? new HashSet<PackagePriority>() : new HashSet<PackagePriority> { request.Priority.Value },
                request.Manufacturer,
                request.Discipline,
                request.Role is null ? new HashSet<PackageRole>() : new HashSet<PackageRole> { request.Role.Value },
                request.Query,
                request.QuickView,
                request.CatalogPreset))
            .Select(item => rowsByPackage[item.Package.Id]);
        cancellationToken.ThrowIfCancellationRequested();
        return ApplySort(rows, request.SortMemberPath, request.SortDirection).ToArray();
    }

    private void ApplyVisibleRows(IReadOnlyList<PackageRowViewModel> rows)
    {
        if (visiblePackages.SequenceEqual(rows)) return;
        visiblePackages = rows;
        OnPropertyChanged(nameof(VisiblePackages));
        if (SelectedRow is not null && !visiblePackages.Contains(SelectedRow)) SelectedRow = null;
        UpdateStatusText();
    }

    private void SearchStateChanged()
    {
        ScheduleSearch(useTextDebounce: false, rebuildVisibleImmediately: true);
    }

    private void InvalidatePendingSearch()
    {
        Interlocked.Increment(ref searchGeneration);
        var previous = Interlocked.Exchange(ref searchCancellation, null);
        previous?.Cancel();
        previous?.Dispose();
    }

    private void ScheduleSearch(bool useTextDebounce = true, bool rebuildVisibleImmediately = false)
    {
        var query = SearchText.Trim();
        var generation = Interlocked.Increment(ref searchGeneration);
        var current = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref searchCancellation, current);
        previous?.Cancel();
        previous?.Dispose();
        if (rebuildVisibleImmediately) RebuildVisible();
        var progressChanged = SearchInProgress != (useTextDebounce || query.Length > 0);
        SearchInProgress = useTextDebounce || query.Length > 0;
        if (!progressChanged) NotifySearchPresentationChanged();
        searchCompletion = RunSearchAsync(CreateSearchRequest(query), generation, useTextDebounce, current.Token);
    }

    private async Task RunSearchAsync(
        SearchRequest request,
        long generation,
        bool useTextDebounce,
        CancellationToken cancellationToken)
    {
        try
        {
            if (useTextDebounce && searchDebounce > TimeSpan.Zero)
                await Task.Delay(searchDebounce, cancellationToken).ConfigureAwait(false);
            var result = await Task.Run(() => ComputeSearch(request, cancellationToken), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await RunOnUiContextAsync(() =>
            {
                if (generation != Volatile.Read(ref searchGeneration) || cancellationToken.IsCancellationRequested) return;
                ApplySearchResult(result);
                SearchInProgress = false;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private SearchResult ComputeSearch(SearchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = ComputeVisibleRows(request, cancellationToken);
        if (compatibilityService is null || request.Query.Length < 2)
            return new SearchResult(rows, [], 0, CompatibilitySearchOutcome.NoDeviceOrCatalogMatch);

        var search = compatibilityService.Search(request.Query, cancellationToken);
        var matches = new List<CompatibilitySearchResultViewModel>(Math.Min(
            CompatibilityResultLimit, search.Devices.Count + search.Products.Count));
        foreach (var device in search.Devices)
        {
            if (matches.Count == CompatibilityResultLimit) break;
            cancellationToken.ThrowIfCancellationRequested();
            var displayName = CompatibilityDetailViewModel.DeviceDisplayName(device, request.Query);
            matches.Add(new CompatibilitySearchResultViewModel(
                CompatibilitySearchResultKind.Device,
                displayName,
                $"{HardwareLookupLabel(device)} | {DeviceMatchLabel(device.MatchKind)} | {device.MatchedRelationIds.Count} reviewed software relationship(s)",
                device.LookupState is HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet or HardwareLookupState.KnownFamilyWithUnresolvedCoverage
                    ? "Known hardware identity; software coverage is not yet verified. This does not mean no software is required."
                    : "View software grouped by field-service purpose. This read-only result cannot be selected for install or update.",
                () => OpenCompatibilityDeviceAsync(device, displayName)));
        }

        foreach (var product in search.Products)
        {
            if (matches.Count == CompatibilityResultLimit) break;
            cancellationToken.ThrowIfCancellationRequested();
            matches.Add(new CompatibilitySearchResultViewModel(
                CompatibilitySearchResultKind.Software,
                product.Name,
                $"{product.Vendor} | {CompatibilityLabel(product.Lifecycle)}",
                "View release families, installed evidence, and applicable devices.",
                () => OpenCompatibilityProductAsync(product.Id)));
        }
        return new SearchResult(rows, matches, search.Devices.Count + search.Products.Count, search.Outcome);
    }

    private void ApplySearchResult(SearchResult result)
    {
        ApplyVisibleRows(result.VisibleRows);
        compatibilityMatches = result.CompatibilityMatches;
        compatibilityTotalMatchCount = result.TotalCompatibilityMatches;
        compatibilitySearchOutcome = result.Outcome;
        OnPropertyChanged(nameof(CompatibilityMatches));
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
        OnPropertyChanged(nameof(SearchStatusText));
        OnPropertyChanged(nameof(SearchStatusVisible));
    }

    private void NotifySearchPresentationChanged()
    {
        OnPropertyChanged(nameof(CompatibilityMatchesVisible));
        OnPropertyChanged(nameof(CompatibilitySearchOutcomeVisible));
        OnPropertyChanged(nameof(SearchStatusText));
        OnPropertyChanged(nameof(SearchStatusVisible));
    }

    private SearchRequest CreateSearchRequest(string query) => new(
        query,
        plan is not null,
        packageSearchSnapshot,
        StandardProfile,
        FieldProfile,
        DeveloperProfile,
        OptionalProfile,
        SelectedPriority.Value,
        SelectedManufacturer.Value,
        SelectedDiscipline.Value,
        SelectedRole.Value,
        QuickView,
        SelectedCatalogPreset.Value,
        sortMemberPath,
        sortDirection);

    private Task RunOnUiContextAsync(Action action)
    {
        if (uiDispatcher is null || uiDispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return uiDispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;
    }

    private static string DeviceMatchLabel(CompatibilitySearchMatchKind kind) => kind switch
    {
        CompatibilitySearchMatchKind.ExactModelOrAlias => "Exact verified model or alias",
        CompatibilitySearchMatchKind.ExactDeviceFamily => "Exact verified device family",
        CompatibilitySearchMatchKind.NormalizedExact => "Verified normalized model or alias",
        CompatibilitySearchMatchKind.PrefixOrToken => "Verified family or alias match",
        _ => "Verified related match"
    };

    private static string HardwareLookupLabel(CompatibilityDeviceSearchResult device) => device.LookupState switch
    {
        HardwareLookupState.KnownExactModelWithVerifiedRelationships => $"Exact model | {device.Hardware!.Manufacturer} | {CompatibilityLabel(device.Hardware.Category)}",
        HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet => $"Known model | {device.Hardware!.Manufacturer} | software coverage not yet verified",
        HardwareLookupState.KnownFamilyWithVerifiedRelationships => $"Known family | {device.Hardware!.Manufacturer} | {CompatibilityLabel(device.Hardware.Category)}",
        HardwareLookupState.KnownFamilyWithUnresolvedCoverage => $"Known family | {device.Hardware!.Manufacturer} | software coverage not yet verified",
        _ => device.DeviceFamilyId
    };

    private static string CompatibilityLabel(Enum value)
    {
        if (value is HardwareDeviceCategory.AudioDsp) return "Audio DSP";
        if (value is HardwareDeviceCategory.AvOverIp) return "AV-over-IP";
        if (value is HardwareDeviceCategory.AvInterface) return "AV interface";
        if (value is HardwareDeviceCategory.DigitalSignage) return "Digital signage";
        if (value is HardwareDeviceCategory.NetworkInfrastructure) return "Network infrastructure";
        var text = value.ToString();
        return string.Concat(text.Select((character, index) => index > 0 && char.IsUpper(character) && char.IsLower(text[index - 1])
            ? $" {character}" : character.ToString()));
    }

    private IEnumerable<PackageRowViewModel> ApplySort(IEnumerable<PackageRowViewModel> rows) =>
        ApplySort(rows, sortMemberPath, sortDirection);

    private static IEnumerable<PackageRowViewModel> ApplySort(
        IEnumerable<PackageRowViewModel> rows,
        string requestedSortMemberPath,
        ListSortDirection? requestedSortDirection)
    {
        if (requestedSortDirection is null || requestedSortMemberPath.Length == 0) return rows.OrderBy(item => item.Order);
        Func<PackageRowViewModel, object> key = requestedSortMemberPath switch
        {
            "ApplicationSortKey" => item => item.ApplicationSortKey,
            "VendorSortKey" => item => item.VendorSortKey,
            "PrioritySortKey" => item => item.PrioritySortKey,
            "StatusSortKey" => item => item.StatusSortKey,
            "VersionSortKey" => item => item.VersionSortKey,
            "RiskSortKey" => item => item.RiskSortKey,
            _ => item => item.Order
        };
        var ordered = requestedSortDirection == ListSortDirection.Ascending
            ? rows.OrderBy(key, ObjectComparer.Instance)
            : rows.OrderByDescending(key, ObjectComparer.Instance);
        return ordered.ThenBy(item => item.StableSortKey, StringComparer.Ordinal);
    }

    private sealed record SearchRequest(
        string Query,
        bool HasPlan,
        IReadOnlyList<PackageRowViewModel> PackageRows,
        bool StandardProfile,
        bool FieldProfile,
        bool DeveloperProfile,
        bool OptionalProfile,
        PackagePriority? Priority,
        string Manufacturer,
        CatalogDiscipline Discipline,
        PackageRole? Role,
        QuickView QuickView,
        CatalogPreset CatalogPreset,
        string SortMemberPath,
        ListSortDirection? SortDirection);

    private sealed record SearchResult(
        IReadOnlyList<PackageRowViewModel> VisibleRows,
        IReadOnlyList<CompatibilitySearchResultViewModel> CompatibilityMatches,
        int TotalCompatibilityMatches,
        CompatibilitySearchOutcome Outcome);

    private void SetQuickView(QuickView view)
    {
        InvalidatePendingSearch();
        QuickView = view;
        RebuildVisible();
        if (view != QuickView.All)
        {
            foreach (var item in packages) item.Selected = false;
            var action = view == QuickView.Missing ? PackageAction.Install : PackageAction.Update;
            foreach (var item in visiblePackages.Where(item => item.CanSelect && item.Action == action)) item.Selected = true;
            UpdateSelectionState();
        }
        ScheduleSearch(useTextDebounce: false);
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
