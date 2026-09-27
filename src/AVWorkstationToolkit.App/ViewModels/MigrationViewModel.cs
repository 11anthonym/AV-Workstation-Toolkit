using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using AVWorkstationToolkit.App.Commands;
using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Application.Workstation;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Workstation;

namespace AVWorkstationToolkit.App.ViewModels;

/// <summary>
/// The workstation migration and provisioning window. It presents one checklist whether it came from an imported
/// inventory or a workstation template; the migration service owns every change, and automatic installation goes through
/// the same managed action coordinator and worker as the main window.
/// </summary>
public sealed class MigrationViewModel : ObservableObject, IDisposable
{
    private readonly WorkstationMigrationService service;
    private readonly IMigrationFileService files;
    private readonly string generator;
    private readonly Dispatcher? dispatcher;
    private readonly Dictionary<string, MigrationItemViewModel> rows = new(StringComparer.Ordinal);
    private readonly BatchObservableCollection<MigrationItemViewModel> visible = [];
    private readonly BatchObservableCollection<MigrationTaskViewModel> tasks = [];
    private readonly BatchObservableCollection<InventoryReviewRowViewModel> reviewRows = [];
    private readonly Dictionary<string, bool> exportChoices = new(StringComparer.Ordinal);
    private List<InventoryReviewRowViewModel> allReviewRows = [];
    private WorkstationInventory? reviewedInventory;
    private bool showSupportingComponents;
    private string reviewSearchText = string.Empty;
    private MigrationFilter filter = MigrationFilter.Remaining;
    private string searchText = string.Empty;
    private MigrationItemViewModel? selectedItem;
    private bool isBusy;
    private bool riskAcknowledged;
    private string status = string.Empty;
    private string savedSessionProblem = string.Empty;
    private bool disposed;
    private bool refreshScheduled;

    public MigrationViewModel(WorkstationMigrationService service, IMigrationFileService files, string productVersion, Dispatcher? dispatcher = null)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.files = files ?? throw new ArgumentNullException(nameof(files));
        generator = $"AV Workstation Toolkit {productVersion}";
        this.dispatcher = dispatcher ?? System.Windows.Application.Current?.Dispatcher;
        VisibleItems = new ReadOnlyObservableCollection<MigrationItemViewModel>(visible);
        Tasks = new ReadOnlyObservableCollection<MigrationTaskViewModel>(tasks);
        ReviewItems = new ReadOnlyObservableCollection<InventoryReviewRowViewModel>(reviewRows);
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy);
        ExportInventoryCommand = new AsyncRelayCommand(ExportInventoryAsync, () => !IsBusy);
        ImportInventoryCommand = new AsyncRelayCommand(ImportInventoryAsync, () => !IsBusy);
        ApplyProfileCommand = new AsyncRelayCommand(ApplyProfileFromFileAsync, () => !IsBusy);
        NewProfileCommand = new AsyncRelayCommand(() => EditProfileAsync(null, null), () => !IsBusy);
        EditProfileCommand = new AsyncRelayCommand(EditProfileFromFileAsync, () => !IsBusy);
        SaveAsProfileCommand = new AsyncRelayCommand(() => EditProfileAsync(null, ChecklistAsProfileApplications()), () => !IsBusy && HasSession);
        InstallSelectedCommand = new AsyncRelayCommand(() => InstallAsync(SelectedItem is null ? [] : [SelectedItem]), () => CanInstallSelected);
        InstallAllCommand = new AsyncRelayCommand(() => InstallAsync(InstallCandidates()), () => CanInstallAll);
        InstallItemCommand = new RelayCommand(value => _ = InstallAsync(value is MigrationItemViewModel item ? [item] : []),
            value => !IsBusy && AutomaticInstallAvailable && value is MigrationItemViewModel { CanInstall: true });
        IncludeCommand = new RelayCommand(_ => SetIncluded(SelectedItem, true), _ => !IsBusy && SelectedItem is { IsExcluded: true });
        ExcludeCommand = new RelayCommand(_ => SetIncluded(SelectedItem, false), _ => !IsBusy && SelectedItem is { IsExcluded: false });
        RemoveCommand = new RelayCommand(_ => RemoveSelected(), _ => !IsBusy && SelectedItem is not null);
        ConfirmCommand = new RelayCommand(_ => Confirm(true), _ => !IsBusy && SelectedItem is { CanConfirm: true });
        UndoConfirmCommand = new RelayCommand(_ => Confirm(false), _ => !IsBusy && SelectedItem is { IsConfirmed: true });
        FinishCommand = new RelayCommand(_ => Finish(), _ => !IsBusy && HasSession);
        DiscardSavedCommand = new RelayCommand(_ => DiscardSaved(), _ => !IsBusy && SavedSessionProblem.Length > 0);
        FilterCommand = new RelayCommand(value => Filter = Enum.TryParse<MigrationFilter>(value?.ToString(), out var parsed) ? parsed : MigrationFilter.All);
        service.ChecklistChanged += Service_ChecklistChanged;
    }

    /// <summary>Raised to show the workstation template editor as a dialog; the window returns when the editor closes.</summary>
    public event Action<ProfileEditorViewModel>? ProfileEditorRequested;

    public IReadOnlyList<MigrationItemViewModel> VisibleItems { get; }
    public IReadOnlyList<MigrationTaskViewModel> Tasks { get; }
    public IReadOnlyList<InventoryReviewRowViewModel> ReviewItems { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand ExportInventoryCommand { get; }
    public AsyncRelayCommand ImportInventoryCommand { get; }
    public AsyncRelayCommand ApplyProfileCommand { get; }
    public AsyncRelayCommand NewProfileCommand { get; }
    public AsyncRelayCommand EditProfileCommand { get; }
    public AsyncRelayCommand SaveAsProfileCommand { get; }
    public AsyncRelayCommand InstallSelectedCommand { get; }
    public AsyncRelayCommand InstallAllCommand { get; }
    public RelayCommand InstallItemCommand { get; }
    public RelayCommand IncludeCommand { get; }
    public RelayCommand ExcludeCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ConfirmCommand { get; }
    public RelayCommand UndoConfirmCommand { get; }
    public RelayCommand FinishCommand { get; }
    public RelayCommand DiscardSavedCommand { get; }
    public RelayCommand FilterCommand { get; }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!SetProperty(ref isBusy, value)) return;
            OnPropertyChanged(nameof(IsNotBusy));
            RaiseCommandStates();
        }
    }

    public bool IsNotBusy => !IsBusy;

    public string Status { get => status; private set => SetProperty(ref status, value); }
    public string SavedSessionProblem { get => savedSessionProblem; private set { if (SetProperty(ref savedSessionProblem, value)) OnPropertyChanged(nameof(SavedSessionProblemVisible)); } }
    public bool SavedSessionProblemVisible => SavedSessionProblem.Length > 0;
    public bool AutomaticInstallAvailable => service.AutomaticInstallAvailable;
    public string AutomaticInstallNote => service.AutomaticInstallAvailable
        ? "Approved apps install through WinGet with the same checks as the main window. Everything else stays on the list for you to install, then rescan."
        : "Preview build: automatic installation is available only in the packaged app. The checklist, scans, and exports work normally.";

    public bool HasInventory => service.TargetInventory is not null;
    public string ThisPcTitle => service.TargetInventory is { } inventory && inventory.Machine.ComputerName.Length > 0
        ? inventory.Machine.ComputerName : "This PC";
    public string ThisPcSummary
    {
        get
        {
            if (service.TargetInventory is not { } inventory) return "Not scanned yet.";
            var recognized = inventory.Applications.Count(item => item.Relevance == MigrationRelevance.Application && item.CatalogId.Length > 0);
            return $"{inventory.ApplicationCount} installed applications · {recognized} recognized by the AVWT catalog";
        }
    }
    public string ThisPcDetail => service.TargetInventory is not { } inventory ? string.Empty
        : $"Scanned {inventory.CapturedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}. {(inventory.Sources.Complete ? string.Empty : inventory.Sources.Detail)}".Trim();

    public bool HasSession => service.Session is not null;
    public bool NoSessionVisible => !HasSession;

    /// <summary>Before a checklist exists, this PC's applications are listed for review before export.</summary>
    public bool ReviewVisible => !HasSession && HasInventory;
    public bool ShowSupportingComponents
    {
        get => showSupportingComponents;
        set { if (SetProperty(ref showSupportingComponents, value)) RebuildReview(); }
    }
    public string ReviewSearchText
    {
        get => reviewSearchText;
        set { if (SetProperty(ref reviewSearchText, value ?? string.Empty)) RebuildReview(); }
    }
    public string ReviewSummary
    {
        get
        {
            var applications = allReviewRows.Where(row => row.IsApplication).ToArray();
            var leftOut = applications.Count(row => !row.Migrate);
            var selected = allReviewRows.Count(row => row.Migrate);
            return leftOut == 0
                ? $"{selected} of {applications.Length} applications will be migrated"
                : $"{selected} of {applications.Length} applications will be migrated · {leftOut} left out";
        }
    }
    public string SourceTitle => service.Session?.Source is not { } source ? "No checklist yet"
        : source.Kind == MigrationSourceKind.Profile
            ? $"Workstation template: {source.Label} · revision {source.ProfileVersion}"
            : $"Migrating from {source.Label}";
    public string SourceDetail
    {
        get
        {
            if (service.Session is not { } session) return string.Empty;
            var parts = new List<string>();
            if (session.Source.CapturedAtUtc is { } captured) parts.Add($"Inventory captured {captured.ToLocalTime():yyyy-MM-dd HH:mm}");
            parts.Add($"Started {session.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
            if (session.RemovedCount > 0) parts.Add($"{session.RemovedCount} removed");
            if (Summary.Supporting > 0) parts.Add($"{Summary.Supporting} supporting components left out");
            if (session.SkippedComponentCount > 0) parts.Add($"{session.SkippedComponentCount} system components and updates kept only in the inventory file");
            return string.Join(" · ", parts);
        }
    }

    public ChecklistSummary Summary => service.Checklist?.Summary ?? ChecklistSummary.Empty;

    /// <summary>The number that matters while rebuilding a workstation: selected applications not yet on this PC.</summary>
    public string RemainingHeadline => !HasSession ? string.Empty
        : service.Checklist is null ? "Scan this PC to compare"
        : Summary.Remaining == 0 ? "Nothing remaining"
        : Summary.Remaining == 1 ? "1 remaining" : $"{Summary.Remaining} remaining";
    public string ProgressText => service.Checklist is null ? string.Empty : string.Join(" · ", new[]
    {
        $"{Summary.Imported} imported",
        $"{Summary.Satisfied} installed",
        Count(Summary.Excluded, "excluded"),
        Count(Summary.NeedsReview, "needs review")
    }.Where(value => value.Length > 0));
    public double ProgressPercent => Summary.Included == 0 ? 0 : 100.0 * Summary.Satisfied / Summary.Included;
    public string SummaryText => service.Checklist is null ? string.Empty : string.Join(" · ", new[]
    {
        Count(Summary.Ready, "ready to install"),
        Count(Summary.Manual, "to install manually"),
        Count(Summary.Unknown, "not in the catalog"),
        Count(Summary.Failed, "failed"),
        Count(Summary.Unverified, "installed but not verified"),
        Count(Summary.Installing, "installing")
    }.Where(value => value.Length > 0));
    public bool CompleteVisible => HasSession && Summary.Included > 0 && Summary.Remaining == 0 && service.Checklist is not null;

    public MigrationFilter Filter
    {
        get => filter;
        set
        {
            if (!SetProperty(ref filter, value)) return;
            RebuildVisible();
        }
    }

    public string SearchText
    {
        get => searchText;
        set
        {
            if (!SetProperty(ref searchText, value ?? string.Empty)) return;
            RebuildVisible();
        }
    }

    public string RemainingLabel => $"Remaining ({CountRows(MigrationFilter.Remaining)})";
    public string ReadyLabel => $"Install available ({CountRows(MigrationFilter.ReadyToInstall)})";
    public string ManualLabel => $"Manual ({CountRows(MigrationFilter.Manual)})";
    public string AttentionLabel => $"Needs attention ({CountRows(MigrationFilter.NeedsAttention)})";
    public string CompletedLabel => $"Completed ({CountRows(MigrationFilter.Completed)})";
    public string ExcludedLabel => $"Excluded ({CountRows(MigrationFilter.Excluded)})";
    public string SupportingLabel => $"Supporting ({CountRows(MigrationFilter.Supporting)})";
    public string AllLabel => $"All ({rows.Count})";

    public MigrationItemViewModel? SelectedItem
    {
        get => selectedItem;
        set
        {
            if (!SetProperty(ref selectedItem, value)) return;
            OnPropertyChanged(nameof(DetailVisible));
            RaiseCommandStates();
        }
    }

    public bool DetailVisible => SelectedItem is not null;
    public bool TasksVisible => tasks.Count > 0;
    public string TasksSummary => $"Manual checks · {tasks.Count(task => task.Done)} of {tasks.Count} done";

    public bool RiskAcknowledged
    {
        get => riskAcknowledged;
        set
        {
            if (!SetProperty(ref riskAcknowledged, value)) return;
            RaiseCommandStates();
        }
    }

    public bool RiskAcknowledgementVisible => AutomaticInstallAvailable && InstallCandidates().Any(item => item.Risk != PackageRisk.None);
    public string RiskAcknowledgementText
    {
        get
        {
            var risky = InstallCandidates().Where(item => item.Risk != PackageRisk.None).ToArray();
            if (risky.Length == 0) return string.Empty;
            var names = string.Join(", ", risky.Take(3).Select(item => item.Name)) + (risky.Length > 3 ? $", and {risky.Length - 3} more" : string.Empty);
            var effects = string.Join(", ", risky.Select(item => item.Risk switch
            {
                PackageRisk.Driver => "install a driver",
                PackageRisk.Service => "add a background service",
                _ => "accept network connections"
            }).Distinct(StringComparer.Ordinal));
            return $"I understand that {names} may {effects}. Continue with this operation.";
        }
    }

    public string InstallAllText => $"Install all available ({InstallCandidates().Count})";
    public bool CanInstallAll => !IsBusy && AutomaticInstallAvailable && InstallCandidates().Count > 0 && RiskSatisfied(InstallCandidates());
    public bool CanInstallSelected => !IsBusy && AutomaticInstallAvailable && SelectedItem is { CanInstall: true } item && RiskSatisfied([item]);

    /// <summary>Loads any saved checklist and compares it with this PC, reusing the main window's plan when it has one.</summary>
    public async Task InitializeAsync(WorkstationPlan? currentPlan)
    {
        try
        {
            service.LoadSaved();
            if (HasSession) Status = "Loaded the saved migration checklist.";
        }
        catch (Exception exception) when (exception is WorkstationDocumentException or IOException or UnauthorizedAccessException)
        {
            SavedSessionProblem = $"The saved migration checklist couldn't be opened: {Sanitize(exception.Message)} You can discard it and start again.";
        }
        if (currentPlan is not null && service.Accept(currentPlan)) Refresh();
        else await ScanAsync().ConfigureAwait(true);
    }

    /// <summary>Adopts a plan the main window refreshed so the checklist stays current without another scan.</summary>
    public void AdoptPlan(WorkstationPlan plan)
    {
        if (disposed || IsBusy) return;
        if (service.Accept(plan)) Refresh();
    }

    public void Dispose()
    {
        disposed = true;
        service.ChecklistChanged -= Service_ChecklistChanged;
    }

    internal async Task ScanAsync()
    {
        IsBusy = true;
        Status = "Scanning this PC: Windows installed apps, WinGet, and the AVWT catalog…";
        try
        {
            var inventory = await service.ScanAsync().ConfigureAwait(true);
            Status = inventory.Sources.Complete
                ? $"Scan complete: {inventory.ApplicationCount} installed applications found."
                : $"Scan finished with gaps: {inventory.Sources.Detail}";
        }
        catch (Exception exception)
        {
            Status = $"Couldn't scan this PC. {Sanitize(exception.Message)}";
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    internal async Task ExportInventoryAsync()
    {
        if (service.TargetInventory is null) await ScanAsync().ConfigureAwait(true);
        if (service.TargetInventory is not { } inventory) return;
        try
        {
            var stem = ProfileKeys.Slug(inventory.Machine.ComputerName, "workstation");
            var path = files.PickInventoryToSave($"{stem}-inventory-{inventory.CapturedAtUtc.ToLocalTime():yyyyMMdd}.json");
            if (path is null)
            {
                Status = "Export cancelled.";
                return;
            }
            files.Write(path, WorkstationInventoryDocumentCodec.Serialize(inventory, generator, ExportChoice));
            var selected = inventory.Applications.Count(ExportChoice);
            Status = $"Exported {inventory.Applications.Count} observed applications to {path}: {selected} selected for migration. Import this file on the replacement PC.";
        }
        catch (Exception exception)
        {
            Status = $"Couldn't export the inventory. {Sanitize(exception.Message)}";
        }
    }

    internal async Task ImportInventoryAsync()
    {
        try
        {
            var path = files.PickInventoryToOpen();
            if (path is null) return;
            var document = WorkstationInventoryDocumentCodec.Parse(files.Read(path, WorkstationInventoryDocumentCodec.MaximumBytes));
            var source = document.Machine.ComputerName.Length > 0 ? document.Machine.ComputerName : "the selected file";
            if (!ConfirmReplace($"Start a new checklist from {source}?")) return;
            var session = service.StartFromInventory(document);
            SavedSessionProblem = string.Empty;
            Filter = MigrationFilter.Remaining;
            await ScanAsync().ConfigureAwait(true);
            var included = session.Items.Count(item => item.Included);
            var imported = $"Imported {session.Items.Count} applications from {source}: {included} selected, {session.Items.Count - included} support components excluded.";
            Status = service.Checklist is null
                ? $"{imported} {Status} Scan this PC again to compare it with the checklist."
                : $"{imported} This PC was scanned and compared: {Summary.Satisfied} already installed, {Summary.Remaining} to do.";
        }
        catch (Exception exception)
        {
            Status = $"Couldn't import the inventory. {Sanitize(exception.Message)}";
        }
    }

    internal async Task ApplyProfileFromFileAsync()
    {
        try
        {
            var path = files.PickProfileToOpen();
            if (path is null) return;
            await ApplyProfileAsync(DeploymentProfileCodec.Parse(files.Read(path, DeploymentProfileCodec.MaximumBytes))).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Status = $"Couldn't apply the workstation template. {Sanitize(exception.Message)}";
        }
    }

    internal async Task ApplyProfileAsync(DeploymentProfile profile)
    {
        var current = service.Session;
        // This also runs straight from the profile editor, outside any caller's error handling, so a checklist that
        // can't be saved is reported here and the current checklist stays as it was.
        try
        {
            if (current?.Source is { Kind: MigrationSourceKind.Profile } source && source.ProfileId == profile.ProfileId && source.ProfileVersion != profile.ProfileVersion)
            {
                var diff = current.CompareToProfile(profile);
                if (!files.Confirm("Update workstation template",
                        $"This PC's checklist uses the {profile.Name} workstation template, revision {source.ProfileVersion}.{Environment.NewLine}{Environment.NewLine}{diff.Describe()}{Environment.NewLine}{Environment.NewLine}Update the checklist to revision {profile.ProfileVersion}? Progress on applications that remain is kept."))
                    return;
                service.AdoptProfileRevision(profile);
                Status = $"Updated the checklist to the {profile.Name} workstation template, revision {profile.ProfileVersion}.";
            }
            else
            {
                if (!ConfirmReplace($"Apply the {profile.Name} workstation template (revision {profile.ProfileVersion})?")) return;
                service.StartFromProfile(profile);
                Status = $"Applied the {profile.Name} workstation template, revision {profile.ProfileVersion}.";
            }
        }
        catch (Exception exception)
        {
            Status = $"Couldn't apply the workstation template. {Sanitize(exception.Message)}";
            RequestRefresh();
            return;
        }
        SavedSessionProblem = string.Empty;
        Filter = MigrationFilter.Remaining;
        var applied = Status;
        await ScanAsync().ConfigureAwait(true);
        Status = service.Checklist is null
            ? $"{applied} {Status} Scan this PC again to compare it with the checklist."
            : $"{applied} {Summary.Satisfied} of {Summary.Included} selected applications are already installed.";
    }

    internal async Task EditProfileFromFileAsync()
    {
        try
        {
            var path = files.PickProfileToOpen();
            if (path is null) return;
            await EditProfileAsync(DeploymentProfileCodec.Parse(files.Read(path, DeploymentProfileCodec.MaximumBytes)), null).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Status = $"Couldn't open the workstation template. {Sanitize(exception.Message)}";
        }
    }

    private async Task EditProfileAsync(DeploymentProfile? existing, IReadOnlyList<ProfileApplication>? seed)
    {
        var editor = new ProfileEditorViewModel(service.Identities, files, existing, seed);
        ProfileEditorRequested?.Invoke(editor);
        if (editor.SavedProfile is not { } saved) return;
        Status = $"Saved the {saved.Name} workstation template, revision {saved.ProfileVersion}, with {saved.Applications.Count} applications and {saved.Checks.Count} manual checks.";
        if (editor.ApplyAfterSave) await ApplyProfileAsync(saved).ConfigureAwait(true);
    }

    private IReadOnlyList<ProfileApplication> ChecklistAsProfileApplications() =>
        rows.Values.Where(row => row.Included).Select(row => row.Item.Desired).Select(desired => desired.Identity.IsConfident
            ? new ProfileApplication(desired.Identity.Application!.Id, desired.Identity.Application.Name, Required: desired.Spec.Required)
            : new ProfileApplication(string.Empty, desired.Spec.DisplayName, desired.Spec.Publisher, desired.Spec.WinGetId, desired.Spec.Required))
        .GroupBy(item => item.Key, StringComparer.Ordinal).Select(group => group.First()).ToArray();

    internal async Task InstallAsync(IReadOnlyList<MigrationItemViewModel> targets)
    {
        if (!service.AutomaticInstallAvailable)
        {
            Status = "Automatic installation is available only in the packaged app. Nothing was installed.";
            return;
        }
        var eligible = targets.Where(item => item.CanInstall).ToArray();
        if (eligible.Length == 0) return;
        if (!RiskSatisfied(eligible))
        {
            Status = "Confirm the system-impact changes before installing.";
            return;
        }
        var acknowledged = RiskAcknowledged;
        RiskAcknowledged = false;
        IsBusy = true;
        Status = eligible.Length == 1 ? $"Installing {eligible[0].Name}…" : $"Installing {eligible.Length} apps one at a time…";
        try
        {
            var outcome = await service.InstallAsync(eligible.Select(item => item.ItemId).ToArray(), acknowledged).ConfigureAwait(true);
            // The installer's result and this PC's check afterward are separate facts. Only a scan completes an item, so a
            // missing or partial check says so rather than turning a finished install into a failure.
            var verification = outcome.ScanProblem.Length > 0
                ? $"Install finished; verification failed: this PC couldn't be checked afterward ({outcome.ScanProblem}), so nothing is marked complete until a scan detects it. Scan this PC again."
                : service.TargetInventory is { Sources.Complete: false } inventory
                    ? $"The check afterward was incomplete ({inventory.Sources.Detail}): {outcome.DetectedAfterward} of {outcome.Requested} confirmed installed. Scan this PC again to check the rest."
                    : $"This PC was checked again: {outcome.DetectedAfterward} of {outcome.Requested} now detected as installed.";
            var saved = outcome.SaveProblem.Length > 0 ? $" The checklist couldn't be saved: {outcome.SaveProblem}" : string.Empty;
            Status = $"Installer result: {outcome.Message} {verification}{saved}";
        }
        catch (Exception exception)
        {
            Status = $"Couldn't install. {Sanitize(exception.Message)}";
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private void SetIncluded(MigrationItemViewModel? item, bool included)
    {
        if (item is null) return;
        Mutate(() => service.SetIncluded(item.ItemId, included), included ? $"Included {item.Name}." : $"Excluded {item.Name}. You can include it again from the Excluded view.");
    }

    private void RemoveSelected()
    {
        var item = SelectedItem;
        if (item is null) return;
        if (!files.Confirm("Remove from checklist", $"Remove {item.Name} from this migration permanently? To keep the option of adding it back, exclude it instead."))
            return;
        Mutate(() => service.Remove(item.ItemId), $"Removed {item.Name} from this migration.");
        SelectedItem = null;
    }

    private void Confirm(bool confirmed)
    {
        var item = SelectedItem;
        if (item is null) return;
        Mutate(() => service.Confirm(item.ItemId, confirmed), confirmed
            ? $"Recorded that {item.Name} is installed. AV Workstation Toolkit can't detect this app, so the checklist keeps your confirmation."
            : $"Cleared the confirmation for {item.Name}.");
    }

    private void Finish()
    {
        var message = Summary.Remaining > 0
            ? $"{Summary.Remaining} selected applications aren't done yet. Finish this migration anyway and delete its checklist?"
            : "Finish this migration and delete its saved checklist?";
        if (!files.Confirm("Finish migration", message)) return;
        Mutate(service.Finish, "Migration finished. Its checklist was deleted from this PC.");
    }

    private void DiscardSaved()
    {
        if (!files.Confirm("Discard saved migration", "Delete the saved migration checklist that couldn't be opened?")) return;
        Mutate(service.Finish, "Discarded the saved migration checklist.");
        SavedSessionProblem = string.Empty;
    }

    private void Mutate(Action change, string success)
    {
        try
        {
            change();
            Status = success;
        }
        catch (Exception exception)
        {
            Status = $"Couldn't update the checklist. {Sanitize(exception.Message)}";
        }
        RequestRefresh();
    }

    private bool ConfirmReplace(string question) => service.Session is null ||
        files.Confirm("Replace checklist", $"{question}{Environment.NewLine}{Environment.NewLine}This replaces the current checklist ({SourceTitle}) and its progress.");

    private IReadOnlyList<MigrationItemViewModel> InstallCandidates() => rows.Values.Where(item => item.CanInstall).ToArray();

    private bool RiskSatisfied(IEnumerable<MigrationItemViewModel> items) => RiskAcknowledged || items.All(item => item.Risk == PackageRisk.None);

    private void Service_ChecklistChanged(object? sender, EventArgs e)
    {
        if (disposed) return;
        RequestRefresh();
    }

    // Checklist edits usually arrive from a check box inside the grid, so the grid is rebuilt after that binding
    // update finishes rather than during it. Without a dispatcher (tests) the refresh runs at once.
    private void RequestRefresh()
    {
        if (dispatcher is null) { Refresh(); return; }
        if (refreshScheduled) return;
        refreshScheduled = true;
        _ = dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            refreshScheduled = false;
            Refresh();
        });
    }

    internal void Refresh()
    {
        if (disposed) return;
        var checklist = service.Checklist;
        var current = checklist?.Items ?? [];
        var ids = current.Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in rows.Keys.Where(id => !ids.Contains(id)).ToArray()) rows.Remove(stale);
        foreach (var item in current)
        {
            if (rows.TryGetValue(item.ItemId, out var row)) row.Update(item);
            else rows.Add(item.ItemId, new MigrationItemViewModel(item, (changed, included) => SetIncluded(changed, included)));
        }
        var sessionTasks = service.Session?.Tasks ?? [];
        tasks.ReplaceAll(sessionTasks.Select(task =>
        {
            var existing = tasks.FirstOrDefault(item => item.Id == task.Id);
            if (existing is null) return new MigrationTaskViewModel(task, (changed, done) =>
                Mutate(() => service.SetTaskDone(changed.Id, done), done ? $"Checked off: {changed.Text}" : $"Unchecked: {changed.Text}"));
            existing.Update(task);
            return existing;
        }).ToArray());
        if (SelectedItem is not null && !rows.ContainsKey(SelectedItem.ItemId)) SelectedItem = null;
        RebuildVisible();
        RebuildReview();
        OnPropertyChanged(string.Empty);
        RaiseCommandStates();
    }

    // User-facing applications default to migrating; supporting and system components default to staying out.
    private bool ExportChoice(ObservedApplication application) =>
        exportChoices.TryGetValue(application.ObservationKey, out var migrate) ? migrate : application.Relevance == MigrationRelevance.Application;

    private void RebuildReview()
    {
        if (!ReferenceEquals(reviewedInventory, service.TargetInventory))
        {
            reviewedInventory = service.TargetInventory;
            allReviewRows = (reviewedInventory?.Applications ?? [])
                .Select(application => new InventoryReviewRowViewModel(application, ExportChoice(application), (row, migrate) =>
                {
                    exportChoices[row.Key] = migrate;
                    OnPropertyChanged(nameof(ReviewSummary));
                }))
                .ToList();
        }
        var query = ReviewSearchText.Trim();
        reviewRows.ReplaceAll(allReviewRows
            .Where(row => ShowSupportingComponents || row.IsApplication)
            .Where(row => query.Length == 0 || $"{row.Name} {row.Publisher} {row.IdentityLabel}".Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray());
        OnPropertyChanged(nameof(ReviewSummary));
    }

    private void RebuildVisible()
    {
        var query = SearchText.Trim();
        visible.ReplaceAll(rows.Values
            .Where(item => item.Matches(Filter))
            .Where(item => query.Length == 0 || item.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.StatusOrder)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray());
        OnPropertyChanged(nameof(Filter));
    }

    private int CountRows(MigrationFilter value) => rows.Values.Count(item => item.Matches(value));

    private void RaiseCommandStates()
    {
        foreach (var command in new[] { ScanCommand, ExportInventoryCommand, ImportInventoryCommand, ApplyProfileCommand, NewProfileCommand,
                     EditProfileCommand, SaveAsProfileCommand, InstallSelectedCommand, InstallAllCommand })
            command.RaiseCanExecuteChanged();
        foreach (var command in new[] { IncludeCommand, ExcludeCommand, RemoveCommand, ConfirmCommand, UndoConfirmCommand, FinishCommand, DiscardSavedCommand, InstallItemCommand })
            command.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanInstallAll));
        OnPropertyChanged(nameof(CanInstallSelected));
    }

    private static string Count(int value, string label) => value == 0 ? string.Empty : $"{value.ToString(CultureInfo.CurrentCulture)} {label}";
    private static string Sanitize(string value) => new(DiagnosticsRedactor.Sanitize(value).Take(1000).ToArray());
}
