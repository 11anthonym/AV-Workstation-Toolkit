using AVWorkstationToolkit.App.Commands;
using AVWorkstationToolkit.Application.Catalog;

namespace AVWorkstationToolkit.App.ViewModels;

public sealed class ManagedCatalogUpdateViewModel : ObservableObject
{
    private readonly IManagedCatalogUpdateService service;
    private ManagedCatalogUpdateStatus status;
    private bool isBusy;

    public ManagedCatalogUpdateViewModel(IManagedCatalogUpdateService service)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        status = service.Status;
        CheckNowCommand = new AsyncRelayCommand(CheckAsync, () => !IsBusy && !Status.RestartRequired);
        InstallCommand = new AsyncRelayCommand(InstallAsync,
            () => !IsBusy && !Status.RestartRequired && Status.State == ManagedCatalogUpdateState.UpdateAvailable && Status.Verified);
    }

    public AsyncRelayCommand CheckNowCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }

    public ManagedCatalogUpdateStatus Status
    {
        get => status;
        private set
        {
            if (!SetProperty(ref status, value)) return;
            OnPropertyChanged(nameof(StateLabel));
            OnPropertyChanged(nameof(CurrentLabel));
            OnPropertyChanged(nameof(AvailableLabel));
            OnPropertyChanged(nameof(SourceLabel));
            OnPropertyChanged(nameof(VerificationLabel));
            OnPropertyChanged(nameof(RestartLabel));
            CheckNowCommand.RaiseCanExecuteChanged();
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
        }
    }

    public string StateLabel => Status.State switch
    {
        ManagedCatalogUpdateState.UpdateAvailable => "Managed app catalog update available",
        ManagedCatalogUpdateState.Completed => "Managed app catalog saved",
        ManagedCatalogUpdateState.Rejected => "Managed app catalog wasn't accepted",
        ManagedCatalogUpdateState.Offline => "Couldn't check for catalog updates",
        ManagedCatalogUpdateState.RequiresNewerApp => "AV Workstation Toolkit update required",
        ManagedCatalogUpdateState.NotConfigured => "Managed catalog updates aren't configured",
        ManagedCatalogUpdateState.Checking or ManagedCatalogUpdateState.Validating => "Checking managed app catalog…",
        _ => "Managed app catalog is current"
    };
    public string CurrentLabel => Status.CurrentRevision > 0
        ? $"Revision {Status.CurrentRevision} · {Status.CurrentVersion}"
        : Status.CurrentVersion;
    public string AvailableLabel => Status.AvailableRevision > 0
        ? $"Revision {Status.AvailableRevision} · {Status.AvailableVersion}"
        : "No update ready";
    public string SourceLabel => Status.Source;
    public string VerificationLabel => Status.Verified ? "Signature and catalog content verified" : "No newly verified update";
    public string RestartLabel => Status.RestartRequired ? "Restart required before the new revision can be used." : string.Empty;

    public Task CheckAsync() => RunAsync(token => service.CheckAsync(token));
    public Task InstallAsync() => RunAsync(token => service.InstallAvailableAsync(token));

    private async Task RunAsync(Func<CancellationToken, Task<ManagedCatalogUpdateStatus>> operation)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { Status = await operation(CancellationToken.None).ConfigureAwait(true); }
        finally { IsBusy = false; }
    }
}
