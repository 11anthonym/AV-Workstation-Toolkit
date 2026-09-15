using AVWorkstationToolkit.App.Commands;
using AVWorkstationToolkit.Application.Details;

namespace AVWorkstationToolkit.App.ViewModels;

public sealed class CatalogDetailViewModel : ObservableObject, IReadOnlyDetailViewModel
{
    private string intentStatus = "Links open official vendor pages in your browser.";

    public CatalogDetailViewModel(CatalogDetail detail, IValidatedUserHandoffService? handoffs = null)
    {
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        ProductIntentCommand = new RelayCommand(_ => HandleIntent(detail.ProductIntent, handoffs), _ => detail.ProductIntent is not null);
        DownloadIntentCommand = new RelayCommand(_ => HandleIntent(detail.DownloadIntent, handoffs), _ => detail.DownloadIntent is not null);
        Links = new[]
        {
            detail.ProductIntent is null ? null : new DetailLinkViewModel("Open product page", "Official vendor product page.", ProductIntentCommand),
            detail.DownloadIntent is null ? null : new DetailLinkViewModel("Open download page", "Official vendor download page. AVWT won't run an installer.", DownloadIntentCommand)
        }.Where(item => item is not null).Cast<DetailLinkViewModel>().ToArray();
    }

    public CatalogDetail Detail { get; }
    public string ContextId => Detail.PackageId;
    public string DetailType => "Software";
    public string Name => Detail.Name;
    public string Subtitle => Detail.Subtitle;
    public IReadOnlyList<CatalogDetailGroup> Groups => Detail.Groups;
    public IReadOnlyList<DetailLinkViewModel> Links { get; }
    public IReadOnlyList<RelatedSoftwareViewModel> RelatedSoftware { get; } = [];
    public bool HasRelatedSoftware => false;
    public bool ProductIntentAvailable => Detail.ProductIntent is not null;
    public bool DownloadIntentAvailable => Detail.DownloadIntent is not null;
    public RelayCommand ProductIntentCommand { get; }
    public RelayCommand DownloadIntentCommand { get; }
    public string IntentStatus { get => intentStatus; private set => SetProperty(ref intentStatus, value); }

    private void HandleIntent(OpenOfficialUriIntent? intent, IValidatedUserHandoffService? handoffs)
    {
        if (intent is null) return;
        if (handoffs is null)
        {
            IntentStatus = $"Preview only: checked the official link for {Name}. No browser or download was started.";
            return;
        }
        try
        {
            handoffs.OpenOfficialUri(intent);
            IntentStatus = $"Opened the official {intent.Kind.ToString().ToLowerInvariant()} page for {Name}.";
        }
        catch (Exception exception)
        {
            IntentStatus = $"Couldn't open the official page. {AVWorkstationToolkit.Application.Diagnostics.DiagnosticsRedactor.Sanitize(exception.Message)}";
        }
    }
}
