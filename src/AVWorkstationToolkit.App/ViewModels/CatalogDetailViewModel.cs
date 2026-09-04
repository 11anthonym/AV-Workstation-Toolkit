using AVWorkstationToolkit.App.Commands;
using AVWorkstationToolkit.Application.Details;

namespace AVWorkstationToolkit.App.ViewModels;

public sealed class CatalogDetailViewModel : ObservableObject, IReadOnlyDetailViewModel
{
    private string intentStatus = "Official links are validated against the catalog before Windows opens them.";

    public CatalogDetailViewModel(CatalogDetail detail, IValidatedUserHandoffService? handoffs = null)
    {
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        ProductIntentCommand = new RelayCommand(_ => HandleIntent(detail.ProductIntent, handoffs), _ => detail.ProductIntent is not null);
        DownloadIntentCommand = new RelayCommand(_ => HandleIntent(detail.DownloadIntent, handoffs), _ => detail.DownloadIntent is not null);
        Links = new[]
        {
            detail.ProductIntent is null ? null : new DetailLinkViewModel("Open product page", "Catalog-validated official HTTPS product page.", ProductIntentCommand),
            detail.DownloadIntent is null ? null : new DetailLinkViewModel("Open download page", "Catalog-validated official HTTPS download page; no installer is executed.", DownloadIntentCommand)
        }.Where(item => item is not null).Cast<DetailLinkViewModel>().ToArray();
    }

    public CatalogDetail Detail { get; }
    public string ContextId => Detail.PackageId;
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
            IntentStatus = $"READ-ONLY Validated {intent.Kind.ToString().ToLowerInvariant()} URI intent for {intent.PackageId}; no browser or download was started.";
            return;
        }
        try
        {
            handoffs.OpenOfficialUri(intent);
            IntentStatus = $"Opened the validated official {intent.Kind.ToString().ToLowerInvariant()} page for {intent.PackageId}.";
        }
        catch (Exception exception)
        {
            IntentStatus = $"Official page handoff failed: {AVWorkstationToolkit.Application.Diagnostics.DiagnosticsRedactor.Sanitize(exception.Message)}";
        }
    }
}
