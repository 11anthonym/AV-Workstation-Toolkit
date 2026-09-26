using System.Security.Cryptography;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Domain.Workstation;

namespace AVWorkstationToolkit.Application.Workstation;

/// <summary>Persists the one active migration checklist beneath the per-user data root.</summary>
public interface IMigrationSessionStore
{
    /// <summary>Returns null when no migration is saved; throws <see cref="WorkstationDocumentException"/> when the saved file is invalid.</summary>
    MigrationSession? Load();
    void Save(MigrationSession session);
    void Delete();
}

public sealed record MigrationChecklist(
    MigrationSession Session,
    IReadOnlyList<ReconciledApplication> Items,
    ChecklistSummary Summary,
    WorkstationInventory TargetInventory,
    WorkstationPlan Plan);

public sealed record MigrationInstallOutcome(
    ActionResultStatus Status,
    string Message,
    int Requested,
    int DetectedAfterward);

/// <summary>
/// Coordinates a migration or provisioning checklist. Imported inventories and profiles only describe what is wanted;
/// what may be installed comes solely from this workstation's catalog plan, and every automatic installation goes
/// through <see cref="CompiledActionCoordinator"/> to the independently validating worker. An item counts as done
/// only when a scan of this workstation detects it, or when a technician explicitly marks an undetectable item done.
/// </summary>
public sealed class WorkstationMigrationService
{
    private readonly IWorkstationPlanningCoordinator planning;
    private readonly WorkstationInventoryService inventory;
    private readonly IMigrationSessionStore store;
    private readonly CompiledActionCoordinator? actions;
    private readonly TimeProvider timeProvider;
    private readonly ApplicationReconciliationService reconciler = new();
    // Scans and installations finish on worker threads while checklist edits arrive from the UI thread, so every
    // change to the session, plan, inventory, and checklist is made under one lock.
    private readonly object gate = new();
    private HashSet<string> installing = new(StringComparer.Ordinal);

    public WorkstationMigrationService(
        IWorkstationPlanningCoordinator planning,
        WorkstationInventoryService inventory,
        IMigrationSessionStore store,
        CompiledActionCoordinator? actions = null,
        TimeProvider? timeProvider = null)
    {
        this.planning = planning ?? throw new ArgumentNullException(nameof(planning));
        this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.actions = actions;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised when this service refreshed the workstation plan, so other views can adopt it.</summary>
    public event EventHandler<WorkstationPlan>? PlanRefreshed;

    /// <summary>Raised whenever the checklist is reconciled again or cleared; it may be raised off the UI thread.</summary>
    public event EventHandler? ChecklistChanged;

    public MigrationSession? Session { get; private set; }
    public WorkstationPlan? Plan { get; private set; }
    public WorkstationInventory? TargetInventory { get; private set; }
    public MigrationChecklist? Checklist { get; private set; }
    public bool AutomaticInstallAvailable => actions is not null;
    public ApplicationIdentityCatalog Identities => inventory.Identities;

    public MigrationSession? LoadSaved()
    {
        lock (gate)
        {
            Session = store.Load();
            Reconcile();
            return Session;
        }
    }

    /// <summary>Scans this workstation: the catalog plan and the full observed inventory from one set of provider reads.</summary>
    public async Task<WorkstationInventory> ScanAsync(IProgress<PlanningRefreshStage>? progress = null, CancellationToken cancellationToken = default)
    {
        var plan = await planning.RefreshAsync(progress, cancellationToken).ConfigureAwait(false);
        Accept(plan);
        PlanRefreshed?.Invoke(this, plan);
        return TargetInventory!;
    }

    /// <summary>Adopts a plan refreshed elsewhere. Returns false when the plan carries no inventory evidence and a scan is needed.</summary>
    public bool Accept(WorkstationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Evidence is null) return false;
        var observed = inventory.Build(plan.Evidence);
        lock (gate)
        {
            Plan = plan;
            TargetInventory = observed;
            Reconcile();
        }
        return true;
    }

    public MigrationChecklist? Reconcile()
    {
        MigrationChecklist? checklist = null;
        lock (gate)
        {
            if (Session is not null && Plan is not null && TargetInventory is not null)
            {
                var items = reconciler.Reconcile(Session.Resolve(Identities),
                    new ReconciliationTarget(Plan.Packages, Plan.Reboot, TargetInventory), installing);
                checklist = new MigrationChecklist(Session, items, ChecklistSummary.From(items), TargetInventory, Plan);
            }
            Checklist = checklist;
        }
        ChecklistChanged?.Invoke(this, EventArgs.Empty);
        return checklist;
    }

    public MigrationSession StartFromInventory(InventoryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Replace(MigrationSession.FromInventory(document, NewSessionId(), timeProvider.GetUtcNow()));
    }

    public MigrationSession StartFromProfile(DeploymentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Replace(MigrationSession.FromProfile(profile, Identities, NewSessionId(), timeProvider.GetUtcNow()));
    }

    /// <summary>Moves the active checklist to another revision of its profile, keeping progress on applications that remain.</summary>
    public ProfileRevisionDiff AdoptProfileRevision(DeploymentProfile profile)
    {
        var session = Session ?? throw new InvalidOperationException("No migration checklist is active.");
        var diff = session.CompareToProfile(profile);
        Replace(session.AdoptProfileRevision(profile, Identities, timeProvider.GetUtcNow()));
        return diff;
    }

    public void SetIncluded(string itemId, bool included) => Mutate(session => session.SetIncluded(itemId, included, timeProvider.GetUtcNow()));
    public void Remove(string itemId) => Mutate(session => session.Remove(itemId, timeProvider.GetUtcNow()));
    public void Confirm(string itemId, bool confirmed) => Mutate(session => session.Confirm(itemId, confirmed, timeProvider.GetUtcNow()));
    public void SetTaskDone(string taskId, bool done) => Mutate(session => session.SetTaskDone(taskId, done, timeProvider.GetUtcNow()));

    /// <summary>Ends the migration and deletes its saved checklist.</summary>
    public void Finish()
    {
        lock (gate)
        {
            store.Delete();
            Session = null;
            Checklist = null;
        }
        ChecklistChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Installs the selected checklist items that this workstation's plan offers as exact managed installs. The
    /// request carries only those plan states, is authorized against the current plan, and is revalidated by the worker.
    /// </summary>
    public async Task<MigrationInstallOutcome> InstallAsync(
        IReadOnlyCollection<string> itemIds,
        bool riskAcknowledged,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (actions is null)
            throw new InvalidOperationException("Automatic installation is available only in the packaged app. Nothing was installed.");
        var checklist = Checklist ?? throw new InvalidOperationException("Scan this PC before installing.");
        var wanted = itemIds.ToHashSet(StringComparer.Ordinal);
        var selected = checklist.Items.Where(item => wanted.Contains(item.ItemId) && item.CanInstallAutomatically).ToArray();
        if (selected.Length == 0)
            throw new InvalidOperationException("None of the selected items can be installed automatically on this PC.");
        var states = selected.Select(item => item.CatalogState!)
            .GroupBy(state => state.Package.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        lock (gate) installing = selected.Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal);
        Reconcile();
        CompiledActionRunResult run;
        try
        {
            run = await actions.StartAsync(ManagedRequestAction.Install, states, checklist.Plan, riskAcknowledged, dryRun: false,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A refused or interrupted request records no attempt; the next scan shows what actually happened.
            lock (gate) installing = new HashSet<string>(StringComparer.Ordinal);
            Reconcile();
            throw;
        }
        var now = timeProvider.GetUtcNow();
        lock (gate)
        {
            installing = new HashSet<string>(StringComparer.Ordinal);
            // Attempts are recorded on the checklist as it is now, which may have changed while the installer ran.
            var session = Session ?? throw new InvalidOperationException("The migration checklist was closed while installing.");
            foreach (var item in selected)
            {
                var packageId = item.CatalogState!.Package.Id;
                var outcome = run.Result.Packages.FirstOrDefault(package => package.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase));
                var succeeded = outcome?.Status == PackageOutcomeStatus.Succeeded;
                var message = outcome is null
                    ? DiagnosticsRedactor.Sanitize(run.Result.Message)
                    : outcome.Status switch
                    {
                        PackageOutcomeStatus.Succeeded => "Installed and verified by the worker.",
                        PackageOutcomeStatus.Blocked => "Blocked by policy when the worker rechecked it.",
                        PackageOutcomeStatus.Unverified => "The installer finished, but the worker couldn't verify the result.",
                        PackageOutcomeStatus.Planned => "The worker didn't reach this app.",
                        _ => $"The installer failed with exit code {outcome.ExitCode}."
                    };
                if (session.Items.Any(entry => entry.ItemId == item.ItemId))
                    session = session.RecordAttempt(item.ItemId, new InstallAttempt(now, succeeded, message), now);
            }
            Session = session;
            store.Save(session);
        }
        Accept(run.RefreshedPlan);
        PlanRefreshed?.Invoke(this, run.RefreshedPlan);
        var detected = Checklist?.Items.Count(item => selected.Any(entry => entry.ItemId == item.ItemId) && item.Status == ChecklistStatus.Installed) ?? 0;
        return new MigrationInstallOutcome(run.Result.Status, DiagnosticsRedactor.Sanitize(run.Result.Message), selected.Length, detected);
    }

    private MigrationSession Replace(MigrationSession session)
    {
        lock (gate)
        {
            store.Save(session);
            Session = session;
        }
        Reconcile();
        return session;
    }

    private void Mutate(Func<MigrationSession, MigrationSession> change)
    {
        lock (gate)
        {
            var session = Session ?? throw new InvalidOperationException("No migration checklist is active.");
            Replace(change(session));
        }
    }

    private string NewSessionId() =>
        $"migration-{timeProvider.GetUtcNow():yyyyMMdd-HHmmss}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
}
