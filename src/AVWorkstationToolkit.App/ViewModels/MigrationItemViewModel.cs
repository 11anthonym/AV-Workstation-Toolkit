using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Workstation;

namespace AVWorkstationToolkit.App.ViewModels;

public enum MigrationFilter { Remaining, ReadyToInstall, Manual, NeedsAttention, Completed, Excluded, Supporting, All }

/// <summary>One checklist row. Presentation only: every change goes back through the migration service.</summary>
public sealed class MigrationItemViewModel : ObservableObject
{
    private readonly Action<MigrationItemViewModel, bool> includeChanged;
    private ReconciledApplication item;

    public MigrationItemViewModel(ReconciledApplication item, Action<MigrationItemViewModel, bool> includeChanged)
    {
        this.item = item ?? throw new ArgumentNullException(nameof(item));
        this.includeChanged = includeChanged ?? throw new ArgumentNullException(nameof(includeChanged));
    }

    public ReconciledApplication Item => item;
    public string ItemId => item.ItemId;
    public string Name => item.DisplayName;
    public string Publisher => item.Desired.Identity.IsConfident && item.Desired.Spec.Publisher.Length == 0
        ? item.Desired.Identity.Application!.Vendor
        : item.Desired.Spec.Publisher;
    public string SourceVersion => item.Desired.Spec.Version.Length > 0 ? item.Desired.Spec.Version : "—";
    public string InstalledVersion => item.Satisfied && item.InstalledVersion.Length > 0 ? item.InstalledVersion
        : item.Status == ChecklistStatus.Installed ? "Installed" : "—";
    public string VersionNote => item.VersionDifference switch
    {
        VersionDifference.Newer => "Newer here",
        VersionDifference.Older => "Older here",
        VersionDifference.Different => "Different version",
        _ => string.Empty
    };
    public string Detail => item.Detail;
    public bool Satisfied => item.Satisfied;
    public bool CanInstall => item.CanInstallAutomatically;
    public bool CanConfirm => item.CanConfirmManually;
    public bool IsConfirmed => item.Status == ChecklistStatus.ConfirmedManually;
    public bool IsExcluded => !item.Desired.Included;
    public bool IsApplication => item.IsApplication;
    public bool Undetectable => ApplicationReconciliationService.IsUndetectable(item.Desired);
    public PackageRisk Risk => item.CatalogState?.Package.Risk ?? PackageRisk.None;
    public string OptionalLabel => item.Desired.Spec.Required ? string.Empty : "Not required";

    public bool Included
    {
        get => item.Desired.Included;
        set
        {
            if (value == item.Desired.Included) return;
            includeChanged(this, value);
        }
    }

    public string StatusLabel => item.Status switch
    {
        ChecklistStatus.Installed => "Installed",
        ChecklistStatus.ConfirmedManually => "Confirmed installed",
        ChecklistStatus.ReadyToInstall => item.CanInstallAutomatically ? "Install available" : "Restart first",
        ChecklistStatus.Installing => "Installing…",
        ChecklistStatus.InstallFailed => "Install failed",
        ChecklistStatus.InstallUnverified => "Installed · not verified",
        ChecklistStatus.ManualInstall => Undetectable ? "Manual · can't be detected" : "Manual · not yet detected",
        ChecklistStatus.UnknownApplication => "Manual · not in catalog",
        ChecklistStatus.NeedsReview => "Review",
        ChecklistStatus.CheckUnavailable => "Can't check",
        _ => item.IsApplication ? "Excluded" : "Supporting component"
    };

    public int StatusOrder => item.Status switch
    {
        ChecklistStatus.InstallFailed or ChecklistStatus.InstallUnverified => 0,
        ChecklistStatus.Installing => 1,
        ChecklistStatus.ReadyToInstall => 2,
        ChecklistStatus.ManualInstall => 3,
        ChecklistStatus.UnknownApplication => 4,
        ChecklistStatus.NeedsReview => 5,
        ChecklistStatus.CheckUnavailable => 6,
        ChecklistStatus.ConfirmedManually => 7,
        ChecklistStatus.Installed => 8,
        _ => 9
    };

    public string StatusBrush => Palette().Background;
    public string StatusBorder => Palette().Border;
    public string StatusForeground => Palette().Foreground;

    public string IdentityText
    {
        get
        {
            var identity = item.Desired.Identity;
            if (identity.IsConfident)
            {
                var known = identity.Application!;
                var management = known.Management switch
                {
                    ApplicationManagement.ManagedWinGet => "approved managed WinGet app",
                    ApplicationManagement.VendorHandoff => "manual vendor download or page",
                    ApplicationManagement.InventoryOnly => "inventory only",
                    _ => "catalog knowledge only"
                };
                return $"AVWT catalog: {known.Name} ({known.Id}) · {management} · identified by {identity.Describe().ToLowerInvariant()}";
            }
            var winGet = item.Desired.Spec.WinGetId;
            var basis = identity.Confidence is IdentityConfidence.Probable or IdentityConfidence.Ambiguous
                ? $"Needs review: {identity.Describe().ToLowerInvariant()}"
                : "Not in the AVWT catalog";
            return winGet.Length > 0 ? $"{basis} · WinGet package {winGet} (not in the managed catalog)" : basis;
        }
    }

    public string EvidenceText => item.MatchEvidence.Length > 0 ? item.MatchEvidence : "Not detected on this PC.";

    public string SearchText => string.Join(' ', Name, Publisher, item.Desired.Spec.DisplayName, item.Desired.Spec.CatalogId, item.Desired.Spec.WinGetId);

    public bool Matches(MigrationFilter filter) => filter switch
    {
        MigrationFilter.Remaining => item.Desired.Included && !item.Satisfied,
        MigrationFilter.ReadyToInstall => item.Desired.Included && item.Status is ChecklistStatus.ReadyToInstall or ChecklistStatus.InstallFailed or ChecklistStatus.InstallUnverified && item.CanInstallAutomatically,
        MigrationFilter.Manual => item.Desired.Included && item.Status is ChecklistStatus.ManualInstall or ChecklistStatus.UnknownApplication,
        MigrationFilter.NeedsAttention => item.Desired.Included && item.Status is ChecklistStatus.NeedsReview or ChecklistStatus.CheckUnavailable or ChecklistStatus.InstallFailed or ChecklistStatus.InstallUnverified,
        MigrationFilter.Completed => item.Desired.Included && item.Satisfied,
        MigrationFilter.Excluded => !item.Desired.Included && item.IsApplication,
        MigrationFilter.Supporting => !item.Desired.Included && !item.IsApplication,
        _ => true
    };

    internal void Update(ReconciledApplication value)
    {
        item = value ?? throw new ArgumentNullException(nameof(value));
        OnPropertyChanged(string.Empty);
    }

    private (string Background, string Border, string Foreground) Palette() => item.Status switch
    {
        ChecklistStatus.Installed or ChecklistStatus.ConfirmedManually => ("#11372D", "#226C57", "#66E1B5"),
        ChecklistStatus.ReadyToInstall => ("#123454", "#245B8D", "#79BEFF"),
        ChecklistStatus.Installing => ("#33215C", "#6745A2", "#C4A4FF"),
        ChecklistStatus.InstallFailed => ("#451A22", "#893044", "#FF9AAA"),
        ChecklistStatus.ManualInstall or ChecklistStatus.UnknownApplication => ("#3B2B13", "#7A5B20", "#FFD27A"),
        ChecklistStatus.NeedsReview or ChecklistStatus.CheckUnavailable or ChecklistStatus.InstallUnverified => ("#2D2B45", "#55517A", "#C8C3FF"),
        _ => ("#252D39", "#485568", "#B8C3D2")
    };
}

/// <summary>A profile manual check shown with a tick box.</summary>
public sealed class MigrationTaskViewModel(ChecklistTask task, Action<MigrationTaskViewModel, bool> doneChanged) : ObservableObject
{
    private ChecklistTask task = task;

    public string Id => task.Id;
    public string Text => task.Text;
    public bool Done
    {
        get => task.DoneAtUtc is not null;
        set
        {
            if (value == Done) return;
            doneChanged(this, value);
        }
    }

    internal void Update(ChecklistTask value)
    {
        task = value;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// One application observed on this PC, listed before export so the technician can leave it out of the migration.
/// Leaving it out only records that choice in the file; the observation itself is always exported as evidence.
/// </summary>
public sealed class InventoryReviewRowViewModel : ObservableObject
{
    private readonly Action<InventoryReviewRowViewModel, bool> changed;
    private bool migrate;

    public InventoryReviewRowViewModel(ObservedApplication application, bool migrate, Action<InventoryReviewRowViewModel, bool> changed)
    {
        Application = application ?? throw new ArgumentNullException(nameof(application));
        this.migrate = migrate;
        this.changed = changed ?? throw new ArgumentNullException(nameof(changed));
    }

    public ObservedApplication Application { get; }
    public string Key => Application.ObservationKey;
    public string Name => Application.DisplayName;
    public string Publisher => Application.Publisher;
    public string Version => Application.DisplayVersion.Length > 0 ? Application.DisplayVersion : "—";
    public string IdentityLabel => Application.CatalogId.Length > 0
        ? $"AVWT catalog: {Application.CatalogIdentity.Application!.Name}"
        : Application.WinGetId.Length > 0 ? $"WinGet: {Application.WinGetId}" : "Not in the catalog";
    public string RelevanceLabel => Application.Relevance switch
    {
        MigrationRelevance.Application => "Application",
        MigrationRelevance.SupportComponent => "Supporting component",
        MigrationRelevance.SystemComponent => "System component",
        _ => "Update"
    };
    public string RelevanceReason => Application.RelevanceReason;
    public bool IsApplication => Application.Relevance == MigrationRelevance.Application;

    public bool Migrate
    {
        get => migrate;
        set
        {
            if (SetProperty(ref migrate, value)) changed(this, value);
        }
    }
}
