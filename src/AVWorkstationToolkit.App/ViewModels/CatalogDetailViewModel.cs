using AVWorkstationToolkit.App.Commands;
using AVWorkstationToolkit.Application.Details;

namespace AVWorkstationToolkit.App.ViewModels;

public sealed class CatalogDetailViewModel : ObservableObject
{
    private string intentStatus = "Official links are validated read-only intents; this migration phase does not open a browser.";

    public CatalogDetailViewModel(CatalogDetail detail)
    {
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        ProductIntentCommand = new RelayCommand(_ => RecordIntent(detail.ProductIntent), _ => detail.ProductIntent is not null);
        DownloadIntentCommand = new RelayCommand(_ => RecordIntent(detail.DownloadIntent), _ => detail.DownloadIntent is not null);
    }

    public CatalogDetail Detail { get; }
    public string Name => Detail.Name;
    public string Subtitle => Detail.Subtitle;
    public IReadOnlyList<CatalogDetailGroup> Groups => Detail.Groups;
    public bool ProductIntentAvailable => Detail.ProductIntent is not null;
    public bool DownloadIntentAvailable => Detail.DownloadIntent is not null;
    public RelayCommand ProductIntentCommand { get; }
    public RelayCommand DownloadIntentCommand { get; }
    public string IntentStatus { get => intentStatus; private set => SetProperty(ref intentStatus, value); }

    private void RecordIntent(OpenOfficialUriIntent? intent)
    {
        if (intent is null) return;
        IntentStatus = $"READ-ONLY Validated {intent.Kind.ToString().ToLowerInvariant()} URI intent for {intent.PackageId}; no browser or download was started.";
    }
}
