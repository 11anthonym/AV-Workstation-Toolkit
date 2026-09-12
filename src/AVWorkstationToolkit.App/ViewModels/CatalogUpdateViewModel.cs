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
        ImportCommand = new RelayCommand(_ => ImportRequested?.Invoke(), _ => !IsBusy);
    }

    public AsyncRelayCommand CheckNowCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public RelayCommand ImportCommand { get; }
    public event Action? ImportRequested;

    public ReferenceCatalogUpdateStatus Status
    {
        get => status;
        private set
        {
            if (!SetProperty(ref status, value)) return;
            OnPropertyChanged(nameof(StateLabel));
            OnPropertyChanged(nameof(CurrentLabel));
            OnPropertyChanged(nameof(AvailableLabel));
            OnPropertyChanged(nameof(ChangeSummary));
            InstallCommand.RaiseCanExecuteChanged();
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
            ImportCommand.RaiseCanExecuteChanged();
        }
    }

    public string StateLabel => Status.State switch
    {
        ReferenceCatalogUpdateState.NotConfigured => "Online channel not configured",
        ReferenceCatalogUpdateState.RequiresNewerApp => "Application update required",
        ReferenceCatalogUpdateState.UpdateAvailable => "Signed catalog update available",
        ReferenceCatalogUpdateState.Completed => "Catalog updated",
        ReferenceCatalogUpdateState.Rejected => "Catalog rejected",
        ReferenceCatalogUpdateState.Offline => "Catalog service offline",
        _ => Status.State.ToString()
    };
    public string CurrentLabel => Status.CurrentRevision > 0 ? $"Revision {Status.CurrentRevision} · {Status.CurrentVersion}" : "Embedded catalog";
    public string AvailableLabel => Status.AvailableRevision > 0 ? $"Revision {Status.AvailableRevision} · {Status.AvailableVersion}" : "No verified update pending";
    public string ChangeSummary => Status.Changes?.Summary ?? string.Empty;

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

    private async Task RunAsync(Func<CancellationToken, Task<ReferenceCatalogUpdateStatus>> operation)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { Status = await operation(CancellationToken.None).ConfigureAwait(true); }
        finally { IsBusy = false; }
    }
}
