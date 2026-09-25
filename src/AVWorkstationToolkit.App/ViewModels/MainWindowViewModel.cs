using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using AVWorkstationToolkit.App.Commands;
using AVWorkstationToolkit.Application.Details;
using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Application.Providers;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.Application.Compatibility;
using System.Windows.Threading;
using AVWorkstationToolkit.Application.Catalog;

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
    private readonly IManagedCatalogUpdateService? managedCatalogUpdates;
    private readonly CatalogQueryService queryService = new();
    private readonly CompiledActionCoordinator? actionCoordinator;
    private readonly BatchObservableCollection<PackageRowViewModel> packages = [];
    private readonly ReadOnlyObservableCollection<PackageRowViewModel> packageView;
    private readonly BatchObservableCollection<PackageRowViewModel> visiblePackages = [];
    private readonly ReadOnlyObservableCollection<PackageRowViewModel> visiblePackageView;
    private readonly BatchObservableCollection<CompatibilitySearchResultViewModel> compatibilityMatches = [];
    private readonly ReadOnlyObservableCollection<CompatibilitySearchResultViewModel> compatibilityMatchView;
    private readonly BatchObservableCollection<ISoftwareTableRow> softwareRows = [];
    private readonly ReadOnlyObservableCollection<ISoftwareTableRow> softwareRowView;
    private RelatedSoftware relatedSoftware = RelatedSoftware.None;
    private readonly TimeSpan searchDebounce;
    private readonly TimeProvider timeProvider;
    private readonly Dispatcher? uiDispatcher;
    private IReadOnlyList<PackageSearchEntry> packageSearchSnapshot = [];
    private readonly object activityGate = new();
    private readonly List<string> pendingActivityLines = [];
    private readonly object actionSnapshotGate = new();
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
    private ISoftwareTableRow? selectedTableRow;
    private string sortMemberPath = string.Empty;
    private ListSortDirection? sortDirection;
    private string activityText = string.Empty;
    private string activityState = "Ready";
    private string statusText = "Loading software and device information...";
    private PlanWarningPresentation warningPresentation = PlanWarningPresentation.None;
    private int mutationRefusalCount;
    private bool actionActive;
    private bool riskAcknowledged;
    private CompiledActionSnapshot actionSnapshot = new(CompiledActionState.Idle, string.Empty, "No installation or update is running.", [], null);
    private CompiledActionSnapshot? pendingActionSnapshot;
    private int refreshInvocationCount;
    private int selectionBatchDepth;
    private bool selectionStatePending;
    private bool activityFlushScheduled;
    private bool actionSnapshotApplyScheduled;
    private bool disposed;
    private long packageSearchIndexBuildCount;
    private long packageSearchEntriesIndexed;
    private long packageSearchRowsEvaluated;
    private long selectionStateUpdateCount;
    private long activityTextUpdateCount;
    private long actionPresentationUpdateCount;

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
        IManagedCatalogUpdateService? managedCatalogUpdates = null,
        TimeSpan? searchDebounce = null,
        Dispatcher? presentationDispatcher = null,
        TimeProvider? timeProvider = null)
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
        this.managedCatalogUpdates = managedCatalogUpdates;
        this.searchDebounce = searchDebounce ?? DefaultSearchDebounce;
        if (this.searchDebounce < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(searchDebounce));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        uiDispatcher = presentationDispatcher ?? System.Windows.Application.Current?.Dispatcher;
        packageView = new ReadOnlyObservableCollection<PackageRowViewModel>(packages);
        visiblePackageView = new ReadOnlyObservableCollection<PackageRowViewModel>(visiblePackages);
        compatibilityMatchView = new ReadOnlyObservableCollection<CompatibilitySearchResultViewModel>(compatibilityMatches);
        softwareRowView = new ReadOnlyObservableCollection<ISoftwareTableRow>(softwareRows);
        LiveRehearsalMode = liveRehearsalMode;
        if (actionCoordinator is not null) actionCoordinator.StateChanged += ActionCoordinator_StateChanged;
        PriorityOptions =
        [
            new("All priorities", null), new("P1", PackagePriority.P1), new("P2", PackagePriority.P2),
            new("Utility", PackagePriority.Utility), new("Developer", PackagePriority.Dev)
        ];
        CatalogPresetOptions =
        [
            new("All apps", CatalogPreset.All), new("Priority 1 apps", CatalogPreset.P1),
            new("Obtain before onsite", CatalogPreset.Onsite), new("Free tools", CatalogPreset.Free),
            new("Free public downloads", CatalogPreset.FreePublic), new("Dealer login required", CatalogPreset.Dealer),
            new("Licensed software", CatalogPreset.Licensed), new("Drivers", CatalogPreset.Drivers),
            new("Background services / network listeners", CatalogPreset.Services), new("Firmware tools", CatalogPreset.Firmware),
            new("Current software", CatalogPreset.Current), new("Legacy / transition software", CatalogPreset.Legacy),
            new("Not installed through AVWT", CatalogPreset.Unmanaged), new("Installed — limited version information", CatalogPreset.InstalledSourceLimited)
        ];
        DisciplineOptions = Enum.GetValues<CatalogDiscipline>()
            .Select(value => new FilterOption<CatalogDiscipline>(DisciplineLabel(value), value)).ToArray();
        RoleOptions = new[] { new FilterOption<PackageRole?>("All roles", null) }
            .Concat(Enum.GetValues<PackageRole>().Select(value => new FilterOption<PackageRole?>(SplitWords(value.ToString()), value))).ToArray();
        ManufacturerOptions = new BatchObservableCollection<FilterOption<string>> { new("All manufacturers", "All") };
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
        DetailsCommand = new RelayCommand(_ => ShowSelectedDetails(), _ => SelectedRow is not null || SelectedTableRow is ReferenceSoftwareRowViewModel);
        GetPackageCommand = new AsyncRelayCommand(GetPackageAsync, CanGetPackage);
        DiagnosticsCommand = new RelayCommand(_ => ShowDiagnostics(), _ => plan is not null && !IsBusy);
        ExportPlanCommand = new RelayCommand(_ => ExportPlan(), _ => plan is not null && !IsBusy && applicationMenuWorkflow is not null);
        OpenLogsCommand = new RelayCommand(_ => OpenLogs(), _ => !IsBusy && applicationMenuWorkflow is not null);
        SafetySecurityCommand = new RelayCommand(_ => SafetySecurityRequested?.Invoke());
        CatalogUpdatesCommand = new RelayCommand(_ => CatalogUpdatesRequested?.Invoke(referenceCatalogUpdates!), _ => referenceCatalogUpdates is not null);
        ManagedCatalogUpdatesCommand = new RelayCommand(_ => ManagedCatalogUpdatesRequested?.Invoke(managedCatalogUpdates!), _ => managedCatalogUpdates is not null);
        AboutCommand = new RelayCommand(_ => AboutRequested?.Invoke());
    }

    public ReadOnlyObservableCollection<PackageRowViewModel> Packages => packageView;
    public IReadOnlyList<PackageRowViewModel> VisiblePackages => visiblePackageView;

    // The Software table: the visible catalog rows followed by any reference-only software documented for the search results.
    public IReadOnlyList<ISoftwareTableRow> SoftwareRows => softwareRowView;
    public IReadOnlyList<CompatibilitySearchResultViewModel> CompatibilityMatches => compatibilityMatchView;
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
    public RelayCommand ManagedCatalogUpdatesCommand { get; }
    public RelayCommand AboutCommand { get; }
    public ICommand ExitCommand { get; } = new RelayCommand(_ => System.Windows.Application.Current?.Shutdown());
    public event Action<CatalogDetailViewModel>? DetailRequested;
    public event Action<CompatibilityDetailViewModel>? CompatibilityDetailRequested;
    public event Action<DiagnosticsViewModel>? DiagnosticsRequested;
    public event Action? SafetySecurityRequested;
    public event Action<IReferenceCatalogUpdateService>? CatalogUpdatesRequested;
    public event Action<IManagedCatalogUpdateService>? ManagedCatalogUpdatesRequested;
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
        ? "Searching…"
        : SearchText.Trim().Length < 2
            ? SearchText.Trim().Length == 0 ? string.Empty : "Type at least 2 characters to search devices and software."
            : compatibilityTotalMatchCount > 0
                ? compatibilityTotalMatchCount == 1 ? "1 device or software result" : $"{compatibilityTotalMatchCount} device or software results"
                : visiblePackages.Count == 1 ? "1 app match"
                : visiblePackages.Count > 1 ? $"{visiblePackages.Count} app matches"
                : "No matches";
    public bool CompatibilityMatchesVisible => !SearchInProgress && compatibilityMatches.Count > 0;
    public bool CompatibilitySearchOutcomeVisible => !SearchInProgress && SearchText.Trim().Length >= 2 &&
        compatibilityMatches.Count == 0 &&
        (compatibilitySearchOutcome == CompatibilitySearchOutcome.NoVerifiedRelationshipInCurrentCatalog || visiblePackages.Count == 0);
    public string CompatibilitySearchOutcomeText => compatibilitySearchOutcome switch
    {
        CompatibilitySearchOutcome.NoVerifiedRelationshipInCurrentCatalog =>
            "This device is listed, but we haven't verified which software applies. This doesn't mean no software is needed.",
        CompatibilitySearchOutcome.NoDeviceOrCatalogMatch =>
            "No match was found in this catalog. Try the full model number or manufacturer; this doesn't mean no software exists.",
        _ => string.Empty
    };
    public string CompatibilityMatchSummary => compatibilityTotalMatchCount > compatibilityMatches.Count
        ? $"Showing {compatibilityMatches.Count} of {compatibilityTotalMatchCount} results"
        : compatibilityMatches.Count == 1 ? "1 result" : $"{compatibilityMatches.Count} results";
    internal Task SearchCompletion => searchCompletion;
    internal static int LiveCompatibilityResultLimit => CompatibilityResultLimit;
    internal PresentationResponsivenessMetrics ResponsivenessMetrics => new(
        Volatile.Read(ref packageSearchIndexBuildCount),
        Volatile.Read(ref packageSearchEntriesIndexed),
        Volatile.Read(ref packageSearchRowsEvaluated),
        visiblePackages.ResetCount,
        compatibilityMatches.ResetCount,
        Volatile.Read(ref selectionStateUpdateCount),
        Volatile.Read(ref activityTextUpdateCount),
        Volatile.Read(ref actionPresentationUpdateCount));
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
    public string QuickViewText => QuickView switch { QuickView.Missing => "Showing: Not installed", QuickView.Updates => "Showing: Updates available", _ => "Showing: All apps" };
    public bool IsAllQuickView => QuickView == QuickView.All;
    public bool IsMissingQuickView => QuickView == QuickView.Missing;
    public bool IsUpdatesQuickView => QuickView == QuickView.Updates;

    public PackageRowViewModel? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (!SetProperty(ref selectedRow, value)) return;
            // Keep the table's selected item in step when code selects or clears a catalog row.
            if (value is not null || selectedTableRow is PackageRowViewModel) SelectedTableRow = value;
            if (selectedDetail is not null && !selectedDetail.Detail.PackageId.Equals(value?.Id, StringComparison.OrdinalIgnoreCase))
                SelectedDetail = null;
            DetailsCommand.RaiseCanExecuteChanged();
            GetPackageCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(GetPackageButtonText));
        }
    }

    // The table's selected item. Only a catalog row becomes SelectedRow, so package commands never see reference-only software.
    public ISoftwareTableRow? SelectedTableRow
    {
        get => selectedTableRow;
        set
        {
            if (!SetProperty(ref selectedTableRow, value)) return;
            SelectedRow = value as PackageRowViewModel;
            DetailsCommand.RaiseCanExecuteChanged();
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
    public string SelectionSummary => SelectedCount == 0 ? "Nothing selected" : $"{SelectedCount} selected · {InstallCount} to install · {UpdateCount} to update";
    public string GetPackageButtonText => SelectedRow?.Package.DeliveryMode switch
    {
        DeliveryMode.VendorPage or DeliveryMode.Awareness or DeliveryMode.Bundled => "Open vendor page",
        DeliveryMode.DirectDownload or DeliveryMode.AuthenticatedSftp or DeliveryMode.ParentProvider => "Download package",
        _ => "Get package"
    };
    public string RiskAcknowledgementText
    {
        get
        {
            var selected = packages.Where(item => item.Selected && item.Risk != PackageRisk.None).ToArray();
            if (selected.Length == 0) return "I understand these changes and want to continue.";
            var names = string.Join(", ", selected.Take(3).Select(item => item.Name));
            if (selected.Length > 3) names += $", and {selected.Length - 3} more";
            var effects = string.Join(", ", selected.Select(item => item.Risk switch
            {
                PackageRisk.Driver => "install a driver",
                PackageRisk.Service => "add a background service",
                PackageRisk.Listener => "accept network connections",
                _ => string.Empty
            }).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
            return $"I understand that {names} may {effects}. Continue with this operation.";
        }
    }
    public bool CanInstall => !IsBusy && !actionActive && InstallCount > 0 && !SelectedRiskBlocked(PackageAction.Install) && !SelectedRiskNeedsAcknowledgement(PackageAction.Install);
    public bool CanUpdate => !IsBusy && !actionActive && UpdateCount > 0 && !SelectedRiskBlocked(PackageAction.Update) && !SelectedRiskNeedsAcknowledgement(PackageAction.Update);
    public bool RiskAcknowledgementRequired => packages.Any(item => item.Selected && item.Risk != PackageRisk.None);
    public bool RiskAcknowledgementVisible => MigrationActionMode && RiskAcknowledgementRequired;
    public bool WarningVisible => warningPresentation.Visible;
    public string WarningSeverityText => warningPresentation.SeverityText;
    public string WarningTitle => warningPresentation.Title;
    public string WarningText => warningPresentation.Message;
    public string WarningDetailText => warningPresentation.Detail;
    public string WarningBackground => warningPresentation.Background;
    public string WarningBorder => warningPresentation.Border;
    public string WarningAccent => warningPresentation.Accent;
    public string WarningAutomationText => warningPresentation.AutomationText;
    public int MutationRefusalCount => mutationRefusalCount;
    internal int RefreshInvocationCount => refreshInvocationCount;

    // The plan behind the current presentation, so an automated startup mode can assert against the
    // provider results that refresh already produced instead of refreshing a second time. Null until
    // a refresh applies a plan; RefreshAsync reports a failed refresh to the activity log and leaves
    // the previous presentation in place, so a caller must treat null as "no plan was produced".
    internal WorkstationPlan? LatestPlan => plan;
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
        AppendActivity("Refreshing app status.");
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
            AppendActivity(WarningVisible
                ? $"{WarningSeverityText} {WarningTitle}. {WarningDetailText}"
                : "App status ready.");
        }
        catch (OperationCanceledException) when (generation != Volatile.Read(ref refreshGeneration) || cancellation.IsCancellationRequested)
        {
            // A newer refresh owns the presentation state.
        }
        catch (Exception exception)
        {
            if (generation != Volatile.Read(ref refreshGeneration)) return;
            StatusText = "Refresh failed. Previously loaded results are still shown.";
            ActivityState = "Refresh failed";
            AppendActivity($"Couldn't refresh app status. {Sanitize(exception.Message)}");
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
        RunSelectionBatch(() =>
        {
            foreach (var item in packages) item.Selected = false;
        });
    }

    public void Dispose()
    {
        disposed = true;
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
        warningPresentation = CreateWarningPresentation(result);
        planCatalog = new PackageCatalog(result.Packages.Select(item => item.Package));
        var newRows = new PackageRowViewModel[result.Packages.Count];
        var newSearchEntries = new PackageSearchEntry[result.Packages.Count];
        for (var order = 0; order < result.Packages.Count; order++)
        {
            var state = result.Packages[order];
            var row = new PackageRowViewModel(state, order, PackageSelectionChanged);
            row.SetBusy(IsBusy);
            newRows[order] = row;
            newSearchEntries[order] = new PackageSearchEntry(
                row,
                new CatalogQueryItem(
                    row.Package,
                    row.Status,
                    row.State.Installed,
                    row.State.AvailableVersion,
                    CatalogQueryService.CreateSearchText(row.Package)));
        }

        packages.ReplaceAll(newRows);
        packageSearchSnapshot = newSearchEntries;
        Interlocked.Increment(ref packageSearchIndexBuildCount);
        Interlocked.Add(ref packageSearchEntriesIndexed, newSearchEntries.Length);
        RunSelectionBatch(() =>
        {
            foreach (var row in newRows)
                if (selectedIds.Contains(row.Id) && row.CanSelect) row.RestoreSelection();
        }, forceUpdate: true);

        var manufacturerOptions = new[] { new FilterOption<string>("All manufacturers", "All") }
            .Concat(CatalogQueryService.Manufacturers(result.Packages.Select(item => item.Package))
                .Select(vendor => new FilterOption<string>(vendor, vendor)))
            .ToArray();
        ReplaceManufacturerOptions(manufacturerOptions);
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
        OnPropertyChanged(nameof(WarningSeverityText));
        OnPropertyChanged(nameof(WarningTitle));
        OnPropertyChanged(nameof(WarningText));
        OnPropertyChanged(nameof(WarningDetailText));
        OnPropertyChanged(nameof(WarningBackground));
        OnPropertyChanged(nameof(WarningBorder));
        OnPropertyChanged(nameof(WarningAccent));
        OnPropertyChanged(nameof(WarningAutomationText));
        DiagnosticsCommand.RaiseCanExecuteChanged();
        ScheduleSearch(useTextDebounce: false, reuseVisibleRows: true);
    }

    private void RebuildVisible()
    {
        if (plan is null) return;
        ApplyVisibleRows(ComputeVisibleRows(CreateSearchRequest(SearchText)));
    }

    private void ReplaceManufacturerOptions(IReadOnlyList<FilterOption<string>> options) =>
        ((BatchObservableCollection<FilterOption<string>>)ManufacturerOptions).ReplaceAll(options);

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
        if (profiles.Count == 0) return [];
        var query = new CatalogQuery(
            profiles,
            request.Priority is null ? new HashSet<PackagePriority>() : new HashSet<PackagePriority> { request.Priority.Value },
            request.Manufacturer,
            request.Discipline,
            request.Role is null ? new HashSet<PackageRole>() : new HashSet<PackageRole> { request.Role.Value },
            request.Query,
            request.QuickView,
            request.CatalogPreset);
        // Catalog apps documented for the listed device and software results keep their own rows and every other filter.
        var relatedQuery = query with { Search = string.Empty };
        var rows = new List<PackageRowViewModel>(request.PackageEntries.Count);
        Interlocked.Add(ref packageSearchRowsEvaluated, request.PackageEntries.Count);
        for (var index = 0; index < request.PackageEntries.Count; index++)
        {
            if ((index & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
            var entry = request.PackageEntries[index];
            if (queryService.Matches(entry.Item, query) ||
                request.RelatedPackageIds.Contains(entry.Row.Id) && queryService.Matches(entry.Item, relatedQuery)) rows.Add(entry.Row);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ApplySort(rows, request.SortMemberPath, request.SortDirection).ToArray();
    }

    private void ApplyVisibleRows(IReadOnlyList<PackageRowViewModel> rows)
    {
        var visibleChanged = visiblePackages.ReplaceAll(rows);
        var tableChanged = softwareRows.ReplaceAll(ComposeSoftwareRows());
        if (!visibleChanged && !tableChanged) return;
        if (SelectedRow is not null && !visiblePackages.Contains(SelectedRow)) SelectedRow = null;
        if (SelectedTableRow is not null && !softwareRows.Contains(SelectedTableRow)) SelectedTableRow = null;
        UpdateStatusText();
    }

    private IReadOnlyList<ISoftwareTableRow> ComposeSoftwareRows()
    {
        if (relatedSoftware.ReferenceRows.Count == 0 || !ShowsReferenceSoftware ||
            !relatedSoftware.Query.Equals(SearchText.Trim(), StringComparison.Ordinal))
            return visiblePackages;
        return [.. visiblePackages, .. relatedSoftware.ReferenceRows];
    }

    // Reference-only software has no profile, priority, manufacturer facet, discipline, role, preset, or install state,
    // so it is listed only while none of those filters or quick views is narrowing the table.
    private bool ShowsReferenceSoftware => QuickView == QuickView.All && SelectedCatalogPreset.Value == CatalogPreset.All &&
        SelectedPriority.Value is null && SelectedManufacturer.Value == "All" && SelectedDiscipline.Value == CatalogDiscipline.All &&
        SelectedRole.Value is null && StandardProfile && FieldProfile && DeveloperProfile && OptionalProfile;

    private void SearchStateChanged()
    {
        InvalidatePendingSearch();
        RebuildVisible();
        ScheduleSearch(useTextDebounce: false, reuseVisibleRows: true);
    }

    private void InvalidatePendingSearch()
    {
        Interlocked.Increment(ref searchGeneration);
        var previous = Interlocked.Exchange(ref searchCancellation, null);
        previous?.Cancel();
        previous?.Dispose();
    }

    private void ScheduleSearch(bool useTextDebounce = true, bool reuseVisibleRows = false)
    {
        var query = SearchText.Trim();
        var generation = Interlocked.Increment(ref searchGeneration);
        var current = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref searchCancellation, current);
        previous?.Cancel();
        previous?.Dispose();
        var progressChanged = !SearchInProgress;
        SearchInProgress = true;
        if (!progressChanged) NotifySearchPresentationChanged();
        var request = CreateSearchRequest(query);
        if (reuseVisibleRows) request = request with { PrecomputedVisibleRows = visiblePackages.ToArray() };
        searchCompletion = RunSearchAsync(request, generation, useTextDebounce, current.Token);
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
                await Task.Delay(searchDebounce, timeProvider, cancellationToken).ConfigureAwait(false);
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
        if (compatibilityService is null || request.Query.Length < 2)
            return new SearchResult(request.PrecomputedVisibleRows ?? ComputeVisibleRows(request, cancellationToken), [], 0,
                CompatibilitySearchOutcome.NoDeviceOrCatalogMatch, RelatedSoftware.None);

        var search = compatibilityService.Search(request.Query, cancellationToken);
        var matches = new List<CompatibilitySearchResultViewModel>(Math.Min(
            CompatibilityResultLimit, search.Devices.Count + search.Products.Count));
        // Software documented for each listed result, in result order, with the purposes and devices that link it.
        var documented = new OrderedDictionary<SoftwareProductId, (List<DeviceSoftwarePurpose> Purposes, List<string> Devices)>();
        void Document(SoftwareProductId productId, DeviceSoftwarePurpose? purpose = null, string? device = null)
        {
            if (!documented.TryGetValue(productId, out var use)) documented.Add(productId, use = ([], []));
            if (purpose is { } value && !use.Purposes.Contains(value)) use.Purposes.Add(value);
            if (device is not null && !use.Devices.Contains(device, StringComparer.OrdinalIgnoreCase)) use.Devices.Add(device);
        }

        foreach (var device in search.Devices)
        {
            if (matches.Count == CompatibilityResultLimit) break;
            cancellationToken.ThrowIfCancellationRequested();
            var displayName = CompatibilityDetailViewModel.DeviceDisplayName(device, request.Query);
            matches.Add(new CompatibilitySearchResultViewModel(
                CompatibilitySearchResultKind.Device,
                displayName,
                $"{HardwareLookupLabel(device)} | {DeviceMatchLabel(device.MatchKind)} | {device.MatchedRelationIds.Count} documented software link(s)",
                device.LookupState is HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet or HardwareLookupState.KnownFamilyWithUnresolvedCoverage
                    ? "This device is listed, but its software support hasn't been verified. This doesn't mean no software is needed. It can't be selected for install or update."
                    : "View software grouped by purpose. Device results can't be selected for install or update.",
                () => OpenCompatibilityDeviceAsync(device, displayName)));
            foreach (var software in compatibilityService.GetSoftwareForDevice(device).SelectMany(group => group.Software))
                Document(software.ProductId, software.Purpose, displayName);
        }

        foreach (var product in search.Products)
        {
            if (matches.Count == CompatibilityResultLimit) break;
            cancellationToken.ThrowIfCancellationRequested();
            matches.Add(new CompatibilitySearchResultViewModel(
                CompatibilitySearchResultKind.Software,
                product.Name,
                $"{product.Vendor} | {CompatibilityLabel(product.Lifecycle)}",
                "View version information and the devices this software supports.",
                () => OpenCompatibilityProductAsync(product.Id)));
            Document(product.Id);
        }

        // A documented product that is also an app catalog record is shown through that record's own row; anything
        // else is listed as reference-only software.
        var packageIds = request.PackageEntries.Select(entry => entry.Row.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relatedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referenceRows = new List<ReferenceSoftwareRowViewModel>();
        foreach (var (productId, use) in documented)
        {
            if (packageIds.Contains(productId.Value))
            {
                relatedPackageIds.Add(productId.Value);
                continue;
            }
            var product = compatibilityService.GetProduct(productId);
            referenceRows.Add(new ReferenceSoftwareRowViewModel(product.Id, product.Name, product.Vendor, use.Purposes, use.Devices));
        }

        var rows = request.PrecomputedVisibleRows is { } precomputed && request.RelatedPackageIds.SetEquals(relatedPackageIds)
            ? precomputed
            : ComputeVisibleRows(request with { RelatedPackageIds = relatedPackageIds }, cancellationToken);
        return new SearchResult(rows, matches, search.Devices.Count + search.Products.Count, search.Outcome,
            new RelatedSoftware(request.Query, relatedPackageIds, referenceRows));
    }

    private void ApplySearchResult(SearchResult result)
    {
        relatedSoftware = result.Related;
        ApplyVisibleRows(result.VisibleRows);
        compatibilityMatches.ReplaceAll(result.CompatibilityMatches);
        compatibilityTotalMatchCount = result.TotalCompatibilityMatches;
        compatibilitySearchOutcome = result.Outcome;
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
        sortDirection,
        relatedSoftware.Query.Equals(query.Trim(), StringComparison.Ordinal) ? relatedSoftware.PackageIds : RelatedSoftware.None.PackageIds);

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
        CompatibilitySearchMatchKind.ExactModelOrAlias => "Exact model or alias",
        CompatibilitySearchMatchKind.ExactDeviceFamily => "Exact device family",
        CompatibilitySearchMatchKind.NormalizedExact => "Model match",
        CompatibilitySearchMatchKind.PrefixOrToken => "Family or alias match",
        _ => "Related match"
    };

    private static string HardwareLookupLabel(CompatibilityDeviceSearchResult device) => device.LookupState switch
    {
        HardwareLookupState.KnownExactModelWithVerifiedRelationships => $"Exact model | {device.Hardware!.Manufacturer} | {CompatibilityLabel(device.Hardware.Category)}",
        HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet => $"Known model | {device.Hardware!.Manufacturer} | software support not yet verified",
        HardwareLookupState.KnownFamilyWithVerifiedRelationships => $"Known family | {device.Hardware!.Manufacturer} | {CompatibilityLabel(device.Hardware.Category)}",
        HardwareLookupState.KnownFamilyWithUnresolvedCoverage => $"Known family | {device.Hardware!.Manufacturer} | software support not yet verified",
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
        IReadOnlyList<PackageSearchEntry> PackageEntries,
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
        ListSortDirection? SortDirection,
        IReadOnlySet<string> RelatedPackageIds,
        IReadOnlyList<PackageRowViewModel>? PrecomputedVisibleRows = null);

    private sealed record PackageSearchEntry(PackageRowViewModel Row, CatalogQueryItem Item);

    private sealed record SearchResult(
        IReadOnlyList<PackageRowViewModel> VisibleRows,
        IReadOnlyList<CompatibilitySearchResultViewModel> CompatibilityMatches,
        int TotalCompatibilityMatches,
        CompatibilitySearchOutcome Outcome,
        RelatedSoftware Related);

    // Software documented for one query's listed results: catalog apps shown through their own rows, and reference-only rows.
    private sealed record RelatedSoftware(
        string Query,
        IReadOnlySet<string> PackageIds,
        IReadOnlyList<ReferenceSoftwareRowViewModel> ReferenceRows)
    {
        public static RelatedSoftware None { get; } = new(string.Empty, new HashSet<string>(), []);
    }

    private void SetQuickView(QuickView view)
    {
        InvalidatePendingSearch();
        QuickView = view;
        RebuildVisible();
        if (view != QuickView.All)
        {
            RunSelectionBatch(() =>
            {
                foreach (var item in packages) item.Selected = false;
                var action = view == QuickView.Missing ? PackageAction.Install : PackageAction.Update;
                foreach (var item in visiblePackages.Where(item => item.CanSelect && item.Action == action)) item.Selected = true;
            }, forceUpdate: true);
        }
        ScheduleSearch(useTextDebounce: false, reuseVisibleRows: true);
    }

    private void PackageSelectionChanged()
    {
        if (selectionBatchDepth > 0)
        {
            selectionStatePending = true;
            return;
        }
        UpdateSelectionState();
    }

    private void RunSelectionBatch(Action action, bool forceUpdate = false)
    {
        selectionBatchDepth++;
        try
        {
            action();
        }
        finally
        {
            selectionBatchDepth--;
            if (selectionBatchDepth == 0 && (selectionStatePending || forceUpdate))
            {
                selectionStatePending = false;
                UpdateSelectionState();
            }
        }
    }

    private void UpdateSelectionState()
    {
        Interlocked.Increment(ref selectionStateUpdateCount);
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
        OnPropertyChanged(nameof(RiskAcknowledgementText));
        RaiseCommandStates();
    }

    private bool SelectedRiskBlocked(PackageAction action) => plan?.Reboot.Pending == true &&
        packages.Any(item => item.Selected && item.Action == action && item.Risk != PackageRisk.None);

    private bool SelectedRiskNeedsAcknowledgement(PackageAction action) => !RiskAcknowledged &&
        packages.Any(item => item.Selected && item.Action == action && item.Risk != PackageRisk.None);

    private void RefuseMutation(PackageAction action)
    {
        mutationRefusalCount++;
        AppendActivity($"Preview only: {action.ToString().ToLowerInvariant()} was not started. Install and update actions are available only in the packaged app.");
        ActivityState = "Preview only";
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
            AppendActivity($"{ActionStateLabel(ActionSnapshot.State)}: {completed.Result.Message}");
        }
        catch (Exception exception)
        {
            var verb = action == ManagedRequestAction.Install ? "install" : "update";
            AppendActivity($"Couldn't {verb} the selected apps. {DiagnosticsRedactor.Sanitize(exception.Message)}");
        }
    }

    private async Task CancelActionAsync()
    {
        if (actionCoordinator is null) return;
        if (await actionCoordinator.RequestCancellationAsync().ConfigureAwait(true))
            AppendActivity("Stop requested. The current app may finish before the remaining apps are skipped.");
    }

    private bool CanGetPackage() => !IsBusy && !actionActive && plan is not null && SelectedRow is not null &&
        packageDeliveryWorkflow?.CanHandle(SelectedRow.State, plan) == true;

    private async Task GetPackageAsync()
    {
        if (!CanGetPackage() || packageDeliveryWorkflow is null || SelectedRow is null || plan is null) return;
        IsBusy = true;
        ActivityState = "Opening package options";
        try
        {
            var outcome = await packageDeliveryWorkflow.DeliverAsync(SelectedRow.State, plan).ConfigureAwait(true);
            AppendActivity(outcome.Detail);
        }
        catch (Exception exception)
        {
            AppendActivity($"Couldn't get the package. {DiagnosticsRedactor.Sanitize(exception.Message)}");
        }
        finally
        {
            IsBusy = false;
            ActivityState = "Ready";
        }
    }

    private void ActionCoordinator_StateChanged(object? sender, CompiledActionSnapshot snapshot)
    {
        AppendActivity($"{ActionStateLabel(snapshot.State)}: {snapshot.Status}");
        var stateTransition = snapshot.State != ActionSnapshot.State;
        bool alreadyScheduled;
        lock (actionSnapshotGate)
        {
            if (disposed) return;
            pendingActionSnapshot = snapshot;
            alreadyScheduled = actionSnapshotApplyScheduled;
            if (!alreadyScheduled) actionSnapshotApplyScheduled = true;
        }

        if (uiDispatcher is null) ApplyPendingActionSnapshot();
        else if (stateTransition)
        {
            if (uiDispatcher.CheckAccess()) ApplyPendingActionSnapshot();
            else _ = uiDispatcher.BeginInvoke(DispatcherPriority.Send, ApplyPendingActionSnapshot);
        }
        else if (!alreadyScheduled)
        {
            _ = uiDispatcher.BeginInvoke(DispatcherPriority.Background, ApplyPendingActionSnapshot);
        }
    }

    private void ApplyPendingActionSnapshot()
    {
        CompiledActionSnapshot? snapshot;
        lock (actionSnapshotGate)
        {
            snapshot = pendingActionSnapshot;
            pendingActionSnapshot = null;
            actionSnapshotApplyScheduled = false;
        }
        if (snapshot is null || disposed) return;

        ActionSnapshot = snapshot;
        actionActive = snapshot.State is CompiledActionState.Preparing or CompiledActionState.Running or CompiledActionState.CancellationRequested;
        ActivityState = ActionStateLabel(snapshot.State);
        foreach (var item in packages) item.SetBusy(IsBusy || actionActive);
        Interlocked.Increment(ref actionPresentationUpdateCount);
        OnPropertyChanged(nameof(ActionProgressVisible));
        OnPropertyChanged(nameof(CanCancelAction));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanUpdate));
        RaiseCommandStates();
    }

    private string ActionDiagnosticText()
    {
        var snapshot = ActionSnapshot;
        var lines = new List<string>
        {
            "[Compiled managed action]",
            $"State: {snapshot.State}",
            $"Request: {(string.IsNullOrWhiteSpace(snapshot.RequestId) ? "(none)" : snapshot.RequestId)}",
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
        if (SelectedTableRow is ReferenceSoftwareRowViewModel reference)
        {
            _ = OpenCompatibilityProductAsync(reference.ProductId);
            return;
        }
        if (SelectedRow is null || planCatalog is null) return;
        ExternalReleaseEvidence? release = null;
        plan?.ExternalReleases?.TryGetValue(SelectedRow.Id, out release);
        SelectedDetail = new CatalogDetailViewModel(detailService.Create(SelectedRow.State, planCatalog, release), handoffService);
        AppendActivity($"Opened details for {SelectedRow.Name}: {SelectedRow.StatusLabel}. {SelectedRow.StatusDetail}");
        DetailRequested?.Invoke(SelectedDetail);
    }

    private void ShowDiagnostics()
    {
        if (plan is null || Diagnostics is null) return;
        AppendActivity($"Opened diagnostics: {Diagnostics.IssueSummary}.");
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
            AppendActivity($"Couldn't export app status. {DiagnosticsRedactor.Sanitize(exception.Message)}");
        }
    }

    private void OpenLogs()
    {
        if (applicationMenuWorkflow is null) return;
        try
        {
            AppendActivity($"Opened the log folder: {applicationMenuWorkflow.OpenLogs()}");
        }
        catch (Exception exception)
        {
            AppendActivity($"Couldn't open the log folder. {DiagnosticsRedactor.Sanitize(exception.Message)}");
        }
    }

    private void UpdateStatusText()
    {
        if (plan is null) return;
        StatusText = $"{visiblePackages.Count} of {plan.Summary.Total} apps · {plan.Summary.Current} up to date · {CountLabel(plan.Summary.ManagedActions, "install or update action")} · {CountLabel(plan.Summary.ManualActions, "vendor step")} · {plan.Summary.Inventory + plan.Summary.NotDetected} inventory only · {CountLabel(plan.Summary.InventoryWarnings, "incomplete check")} · {plan.Summary.Awareness} information only";
    }

    private void AppendActivity(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {Sanitize(message)}";
        lock (activityGate)
        {
            if (disposed) return;
            pendingActivityLines.Add(line);
            if (activityFlushScheduled) return;
            activityFlushScheduled = true;
        }

        if (uiDispatcher is null) FlushPendingActivity();
        else _ = uiDispatcher.BeginInvoke(DispatcherPriority.Background, FlushPendingActivity);
    }

    private void FlushPendingActivity()
    {
        string[] lines;
        lock (activityGate)
        {
            if (disposed)
            {
                pendingActivityLines.Clear();
                activityFlushScheduled = false;
                return;
            }
            lines = pendingActivityLines.ToArray();
            pendingActivityLines.Clear();
            activityFlushScheduled = false;
        }
        if (lines.Length == 0) return;

        var addition = string.Join(Environment.NewLine, lines);
        ActivityText = ActivityText.Length == 0 ? addition : $"{ActivityText}{Environment.NewLine}{addition}";
        if (ActivityText.Length > 32000) ActivityText = ActivityText[^32000..];
        Interlocked.Increment(ref activityTextUpdateCount);
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

    private static PlanWarningPresentation CreateWarningPresentation(WorkstationPlan value)
    {
        var checks = new List<(string Name, ProviderQuality Quality, string Detail)>();
        AddCheck("WinGet installed inventory", value.Providers.WinGetInventoryQuality, value.Providers.WinGetInventoryDetail);
        AddCheck("WinGet update check", value.Providers.WinGetUpdateQuality, value.Providers.WinGetUpdateDetail);
        AddCheck("External application inventory", value.Providers.ExternalInventoryQuality, value.Providers.ExternalInventoryDetail);
        AddCheck("Restart detection", value.Providers.RebootQuality, value.Providers.RebootDetail);

        if (!value.Reboot.Pending && checks.Count == 0) return PlanWarningPresentation.None;

        var malformed = checks.Any(item => item.Quality == ProviderQuality.Malformed);
        var issueCount = checks.Count + (value.Reboot.Pending ? 1 : 0);
        var title = issueCount > 1 ? "Several checks need attention"
            : value.Reboot.Pending ? "Restart recommended"
            : checks[0].Name switch
            {
                "WinGet update check" => "Couldn't check for updates",
                "WinGet installed inventory" => "Couldn't check installed apps",
                "External application inventory" => "Some installed apps couldn't be checked",
                "Restart detection" => "Couldn't check restart status",
                _ => "A check needs attention"
            };
        var message = issueCount > 1
            ? "Some app information is incomplete. Review the details below; uncertain apps can't be installed or updated."
            : value.Reboot.Pending
                ? "Windows is waiting for a restart to finish an update. Most low-risk actions remain available, but system-level changes stay paused until you restart."
                : checks[0].Name switch
                {
                    "WinGet update check" => value.Providers.WinGetInventoryQuality == ProviderQuality.Complete
                        ? "Installed versions are still shown, but update availability is unknown. Try checking again."
                        : "Update availability is unknown. Installation information may also be incomplete. Try checking again.",
                    "WinGet installed inventory" => "AVWT couldn't confirm which managed apps are installed. Install and update actions stay unavailable for uncertain items.",
                    "External application inventory" => "AVWT couldn't read one or more Windows app lists. Affected app statuses remain incomplete.",
                    "Restart detection" => "AVWT couldn't determine whether Windows is waiting for a restart. System-level changes remain restricted.",
                    _ => "A check didn't finish. Uncertain apps can't be installed or updated."
                };
        var details = checks.Select(item => $"{item.Name}: {CleanDetail(item.Detail)}").ToList();
        if (value.Reboot.Pending) details.Insert(0, $"Pending restart: {CleanDetail(value.Reboot.Summary)}");
        var detail = string.Join("  |  ", details);
        var severity = malformed ? "CHECK FAILED" : "ATTENTION";
        return new(true, severity, title, message, detail,
            malformed ? "#321719" : "#3A2B0B",
            malformed ? "#A43F48" : "#8A6513",
            malformed ? "#FF8D96" : "#F8C555");

        void AddCheck(string name, ProviderQuality quality, string detail)
        {
            if (quality != ProviderQuality.Complete) checks.Add((name, quality, detail));
        }

        static string CleanDetail(string detail) => string.IsNullOrWhiteSpace(detail)
            ? "No additional detail was provided."
            : DiagnosticsRedactor.Sanitize(detail).Trim();
    }

    private sealed record PlanWarningPresentation(
        bool Visible,
        string SeverityText,
        string Title,
        string Message,
        string Detail,
        string Background,
        string Border,
        string Accent)
    {
        public static PlanWarningPresentation None { get; } = new(false, string.Empty, string.Empty, string.Empty, string.Empty,
            "#3A2B0B", "#8A6513", "#F8C555");
        public string AutomationText => string.Join(". ", new[] { SeverityText, Title, Message, Detail }.Where(value => value.Length > 0));
    }

    private static string StageText(PlanningRefreshStage stage) => stage switch
    {
        PlanningRefreshStage.ReadingWinGetInventory => "Checking installed managed apps…",
        PlanningRefreshStage.ReadingWinGetUpdates => "Checking for managed app updates…",
        PlanningRefreshStage.ReadingExternalInventory => "Checking installed AV software…",
        PlanningRefreshStage.ReadingExternalReleases => "Checking vendor releases…",
        PlanningRefreshStage.CheckingRebootState => "Checking restart status…",
        PlanningRefreshStage.BuildingPlan => "Preparing app status…",
        _ => "Ready"
    };
    private static string ActionStateLabel(CompiledActionState state) => state switch
    {
        CompiledActionState.Idle => "Ready",
        CompiledActionState.Preparing => "Preparing selected apps",
        CompiledActionState.Running => "Installation in progress",
        CompiledActionState.CancellationRequested => "Stop requested",
        CompiledActionState.Completed => "Completed",
        CompiledActionState.Failed => "Failed",
        CompiledActionState.Cancelled => "Stopped",
        _ => "Action status"
    };
    private static string Sanitize(string value) => new(DiagnosticsRedactor.Sanitize(value).Take(4000).ToArray());
    private static string CountLabel(int count, string singular) => count == 1 ? $"1 {singular}" : $"{count} {singular}s";
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

internal sealed record PresentationResponsivenessMetrics(
    long PackageSearchIndexBuilds,
    long PackageSearchEntriesIndexed,
    long PackageSearchRowsEvaluated,
    int VisibleCollectionResets,
    int CompatibilityCollectionResets,
    long SelectionStateUpdates,
    long ActivityTextUpdates,
    long ActionPresentationUpdates);
