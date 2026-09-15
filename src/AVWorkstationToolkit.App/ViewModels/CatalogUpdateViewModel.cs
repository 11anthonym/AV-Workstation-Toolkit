using AVWorkstationToolkit.App.Commands;
using AVWorkstationToolkit.Application.Compatibility;

namespace AVWorkstationToolkit.App.ViewModels;

public sealed class CatalogUpdateViewModel : ObservableObject
{
    private readonly IReferenceCatalogUpdateService service;
    private ReferenceCatalogUpdateStatus status;
    private bool isBusy;

    public CatalogUpdateViewModel(IReferenceCatalogUpdateService service)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        status = service.Status;
        CheckNowCommand = new AsyncRelayCommand(CheckAsync, () => !IsBusy);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => !IsBusy && Status.State == ReferenceCatalogUpdateState.UpdateAvailable);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => !IsBusy && Status.RestorableRevision > 0);
        ImportCommand = new RelayCommand(_ => ImportRequested?.Invoke(), _ => !IsBusy);
    }

    public AsyncRelayCommand CheckNowCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public RelayCommand ImportCommand { get; }
    public event Action? ImportRequested;

    public ReferenceCatalogUpdateStatus Status
    {
        get => status;
        private set
        {
            if (!SetProperty(ref status, value)) return;
            OnPropertyChanged(nameof(StateLabel));
            OnPropertyChanged(nameof(DetailLabel));
            OnPropertyChanged(nameof(TechnicalDetail));
            OnPropertyChanged(nameof(CurrentLabel));
            OnPropertyChanged(nameof(AvailableLabel));
            OnPropertyChanged(nameof(ChangeSummary));
            OnPropertyChanged(nameof(CanRestorePrevious));
            InstallCommand.RaiseCanExecuteChanged();
            RestoreCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!SetProperty(ref isBusy, value)) return;
            CheckNowCommand.RaiseCanExecuteChanged();
            InstallCommand.RaiseCanExecuteChanged();
            RestoreCommand.RaiseCanExecuteChanged();
            ImportCommand.RaiseCanExecuteChanged();
        }
    }

    public string StateLabel => Status.State switch
    {
        ReferenceCatalogUpdateState.Idle => "Device catalog ready",
        ReferenceCatalogUpdateState.Checking => "Checking for updates…",
        ReferenceCatalogUpdateState.Current => "Device catalog is up to date",
        ReferenceCatalogUpdateState.UpdateAvailable => "Device catalog update available",
        ReferenceCatalogUpdateState.Downloading => "Downloading device catalog…",
        ReferenceCatalogUpdateState.Validating => "Checking device catalog…",
        ReferenceCatalogUpdateState.Completed => "Device catalog saved",
        ReferenceCatalogUpdateState.Rejected => "Device catalog wasn't accepted",
        ReferenceCatalogUpdateState.Offline => "Couldn't check for catalog updates",
        ReferenceCatalogUpdateState.RequiresNewerApp => "AV Workstation Toolkit update required",
        ReferenceCatalogUpdateState.NotConfigured => "Online catalog updates aren't configured",
        _ => "Device catalog status"
    };
    public string DetailLabel => Status.State switch
    {
        ReferenceCatalogUpdateState.Completed => "Saved. Restart AV Workstation Toolkit to use this device catalog.",
        ReferenceCatalogUpdateState.Rejected => "Your current device catalog is unchanged.",
        ReferenceCatalogUpdateState.Offline => "Your saved device catalog is still available.",
        ReferenceCatalogUpdateState.RequiresNewerApp => "This catalog needs a newer version of AV Workstation Toolkit. Your current catalog is unchanged.",
        ReferenceCatalogUpdateState.UpdateAvailable => "A newer signed device catalog is ready to download.",
        ReferenceCatalogUpdateState.Checking => "Your saved device catalog remains available while this check runs.",
        ReferenceCatalogUpdateState.Validating => "The catalog is being checked before it is saved.",
        ReferenceCatalogUpdateState.NotConfigured => "Your saved device catalog is still available.",
        _ => "Device Lookup works with the catalog already saved on this PC."
    };
    public string TechnicalDetail => string.IsNullOrWhiteSpace(Status.Detail) ? string.Empty : $"Details: {Status.Detail}";
    public string CurrentLabel => Status.CurrentRevision > 0 ? $"Revision {Status.CurrentRevision} · {Status.CurrentVersion}" : "Built-in device catalog";
    public string AvailableLabel => Status.AvailableRevision > 0 ? $"Revision {Status.AvailableRevision} · {Status.AvailableVersion}" : "No update ready";
    public string ChangeSummary => Status.Changes?.Summary ?? string.Empty;
    public bool CanRestorePrevious => Status.RestorableRevision > 0;

    public async Task CheckAsync()
    {
        await RunAsync(token => service.CheckAsync(token)).ConfigureAwait(true);
    }

    public async Task InstallAsync()
    {
        await RunAsync(token => service.InstallAvailableAsync(token)).ConfigureAwait(true);
    }

    public async Task ImportAsync(string path)
    {
        await RunAsync(token => service.ImportAsync(path, token)).ConfigureAwait(true);
    }

    public async Task RestoreAsync()
    {
        await RunAsync(token => service.RestorePreviousAsync(token)).ConfigureAwait(true);
    }

    private async Task RunAsync(Func<CancellationToken, Task<ReferenceCatalogUpdateStatus>> operation)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { Status = await operation(CancellationToken.None).ConfigureAwait(true); }
        finally { IsBusy = false; }
    }
}
