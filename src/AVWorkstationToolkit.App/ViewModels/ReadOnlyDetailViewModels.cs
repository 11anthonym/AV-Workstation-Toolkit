using System.Windows.Input;
using AVWorkstationToolkit.App.Commands;
using AVWorkstationToolkit.Application.Compatibility;
using AVWorkstationToolkit.Application.Details;
using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.App.ViewModels;

public interface IReadOnlyDetailViewModel
{
    string ContextId { get; }
    string Name { get; }
    string Subtitle { get; }
    IReadOnlyList<CatalogDetailGroup> Groups { get; }
    IReadOnlyList<DetailLinkViewModel> Links { get; }
    IReadOnlyList<RelatedSoftwareViewModel> RelatedSoftware { get; }
    bool HasRelatedSoftware { get; }
    string IntentStatus { get; }
}

public sealed record DetailLinkViewModel(string Label, string Description, ICommand Command);

public sealed record RelatedSoftwareViewModel(
    string ProductName,
    string Purpose,
    string Summary,
    ICommand OpenCommand);

public enum CompatibilitySearchResultKind { Software, Device }

public sealed class CompatibilitySearchResultViewModel(
    CompatibilitySearchResultKind kind,
    string title,
    string subtitle,
    string matchDetail,
    Func<Task> openAsync)
{
    public CompatibilitySearchResultKind Kind { get; } = kind;
    public string KindLabel => Kind == CompatibilitySearchResultKind.Device ? "Device" : "Software";
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string MatchDetail { get; } = matchDetail;
    public bool CanSelect => false;
    public AsyncRelayCommand OpenCommand { get; } = new(openAsync ?? throw new ArgumentNullException(nameof(openAsync)));
}

public sealed class CompatibilityDetailViewModel : ObservableObject, IReadOnlyDetailViewModel
{
    private readonly CompatibilityCatalogQueryService queries;
    private readonly IValidatedUserHandoffService? handoffs;
    private string intentStatus = "Compatibility information is descriptive. Links open reviewed HTTPS evidence only.";

    private CompatibilityDetailViewModel(
        CompatibilityCatalogQueryService queries,
        IValidatedUserHandoffService? handoffs,
        string contextId,
        string name,
        string subtitle,
        IReadOnlyList<CatalogDetailGroup> groups,
        IReadOnlyList<DetailLinkViewModel> links,
        IReadOnlyList<RelatedSoftwareViewModel> relatedSoftware)
    {
        this.queries = queries;
        this.handoffs = handoffs;
        ContextId = contextId;
        Name = name;
        Subtitle = subtitle;
        Groups = groups;
        Links = links;
        RelatedSoftware = relatedSoftware;
    }

    public string ContextId { get; }
    public string Name { get; }
    public string Subtitle { get; }
    public IReadOnlyList<CatalogDetailGroup> Groups { get; }
    public IReadOnlyList<DetailLinkViewModel> Links { get; private set; }
    public IReadOnlyList<RelatedSoftwareViewModel> RelatedSoftware { get; private set; }
    public bool HasRelatedSoftware => RelatedSoftware.Count > 0;
    public string IntentStatus { get => intentStatus; private set => SetProperty(ref intentStatus, value); }
    public event Action<CompatibilityDetailViewModel>? NavigationRequested;

    public static async Task<CompatibilityDetailViewModel> CreateProductAsync(
        CompatibilityCatalogQueryService queries,
        SoftwareProductId productId,
        IValidatedUserHandoffService? handoffs = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queries);
        var product = queries.GetProduct(productId);
        var families = queries.GetReleaseFamilies(productId);
        var installed = await queries.GetInstalledVersionsAsync(productId, cancellationToken).ConfigureAwait(false);
        var devices = queries.GetDevicesForProduct(productId);
        var groups = new List<CatalogDetailGroup>
        {
            Group("Product", ("Vendor", product.Vendor), ("Lifecycle", Label(product.Lifecycle)),
                ("Aliases", Values(product.Aliases))),
            Group("Installed-version evidence", installed.Select(item =>
                (InstalledLabel(item), InstalledValue(item))).ToArray()),
            Group("Release families", families.Count == 0
                ? [("Status", "No verified release-family records are available.")]
                : families.Select(item => ($"{Label(item.Kind)} — {item.Branch}",
                    $"{Label(item.Lifecycle)}. {TextOrUnknown(item.Constraints)}")).ToArray())
        };
        foreach (var device in devices)
        {
            groups.Add(Group(DeviceName(device), device.Purposes.Select(purpose =>
                (Label(purpose.Purpose), RelationSummary(purpose.Applicability, purpose.Confidence,
                    purpose.ReleaseFamilyId, purpose.Constraints))).ToArray()));
        }

        var viewModel = new CompatibilityDetailViewModel(queries, handoffs, product.Id.Value, product.Name,
            $"{product.Vendor} | {Label(product.Lifecycle)} | Read-only compatibility record", groups, [], []);
        viewModel.LinksInternal.Add(viewModel.CreateLink("Official product page", product.OfficialSourceUri,
            OpenOfficialUriIntent.FromCompatibilityProduct(product)));
        foreach (var family in families)
            viewModel.AddUniqueLink($"{Label(family.Kind)} release evidence", family.EvidenceUri,
                OpenOfficialUriIntent.FromCompatibilityRelease(product.Id, family));
        foreach (var device in devices)
            foreach (var purpose in device.Purposes)
                viewModel.AddUniqueLink($"{DeviceName(device)} — {Label(purpose.Purpose)} evidence", purpose.EvidenceUri,
                    OpenOfficialUriIntent.FromCompatibilityRelation(product.Id, purpose));
        return viewModel.FreezeLinks();
    }

    public static CompatibilityDetailViewModel CreateDevice(
        CompatibilityCatalogQueryService queries,
        CompatibilityDeviceSearchResult device,
        string displayName,
        IValidatedUserHandoffService? handoffs = null)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(device);
        // The result carries the exact authoritative relation scope selected by search. Do not use a display
        // label (which may be a model or alias) to repeat a lookup and accidentally broaden or switch scope.
        var softwareGroups = queries.GetSoftwareForDevice(device);
        var groups = new List<CatalogDetailGroup>
        {
            Group("Device identity", ("Device family", device.DeviceFamilyId),
                ("Models", Values(device.ExactModelIds)), ("Aliases", Values(device.Aliases)))
        };
        foreach (var purposeGroup in softwareGroups)
            groups.Add(Group(Label(purposeGroup.Purpose), purposeGroup.Software.Select(item =>
                (item.ProductName, RelationSummary(item.Applicability, item.Confidence,
                    item.ReleaseFamilyId, item.Constraints))).ToArray()));

        var viewModel = new CompatibilityDetailViewModel(queries, handoffs, device.DeviceFamilyId, displayName,
            $"Device compatibility | {softwareGroups.Sum(group => group.Software.Count)} reviewed software relationship(s)",
            groups, [], []);
        var relatedSoftware = softwareGroups.SelectMany(group => group.Software).ToArray();
        foreach (var softwareGroup in relatedSoftware.GroupBy(item => item.ProductId))
        {
            var software = softwareGroup.First();
            viewModel.RelatedSoftwareInternal.Add(new RelatedSoftwareViewModel(
                software.ProductName,
                string.Join(", ", softwareGroup.Select(item => Label(item.Purpose)).Distinct(StringComparer.OrdinalIgnoreCase)),
                "Open this product's release families, installed evidence, and applicable devices.",
                new AsyncRelayCommand(() => viewModel.NavigateToProductAsync(software.ProductId))));
        }
        foreach (var group in softwareGroups)
        {
            foreach (var software in group.Software)
            {
                viewModel.AddUniqueLink($"{software.ProductName} — {Label(software.Purpose)} evidence", software.EvidenceUri,
                    OpenOfficialUriIntent.FromCompatibilityRelation(software));
            }
        }
        return viewModel.FreezeLinksAndSoftware();
    }

    private List<DetailLinkViewModel> LinksInternal { get; } = [];
    private List<RelatedSoftwareViewModel> RelatedSoftwareInternal { get; } = [];

    private CompatibilityDetailViewModel FreezeLinks()
    {
        Links = LinksInternal.ToArray();
        return this;
    }

    private CompatibilityDetailViewModel FreezeLinksAndSoftware()
    {
        Links = LinksInternal.ToArray();
        RelatedSoftware = RelatedSoftwareInternal.ToArray();
        return this;
    }

    private async Task NavigateToProductAsync(SoftwareProductId productId)
    {
        var target = await CreateProductAsync(queries, productId, handoffs).ConfigureAwait(true);
        NavigationRequested?.Invoke(target);
    }

    private DetailLinkViewModel CreateLink(string label, Uri uri, OpenOfficialUriIntent intent) =>
        new(label, uri.AbsoluteUri, new RelayCommand(_ => OpenIntent(intent)));

    private void AddUniqueLink(string label, Uri uri, OpenOfficialUriIntent intent)
    {
        if (LinksInternal.Any(item => item.Description.Equals(uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))) return;
        LinksInternal.Add(CreateLink(label, uri, intent));
    }

    private void OpenIntent(OpenOfficialUriIntent intent)
    {
        if (handoffs is null)
        {
            IntentStatus = $"READ-ONLY Validated {intent.Kind.ToString().ToLowerInvariant()} HTTPS intent for {intent.PackageId}; no browser or download was started.";
            return;
        }
        try
        {
            handoffs.OpenOfficialUri(intent);
            IntentStatus = $"Opened reviewed {intent.Kind.ToString().ToLowerInvariant()} evidence for {intent.PackageId}.";
        }
        catch (Exception exception)
        {
            IntentStatus = $"Evidence handoff failed: {DiagnosticsRedactor.Sanitize(exception.Message)}";
        }
    }

    private static CatalogDetailGroup Group(string name, params (string Label, string Value)[] fields) =>
        new(name, fields.Select(field => new CatalogDetailField(field.Label, field.Value)).ToArray());

    private static string InstalledLabel(InstalledVersion evidence) => evidence.State == InstalledVersionEvidenceState.Observed
        ? TextOrUnknown(evidence.RawDisplayName)
        : Label(evidence.State);

    private static string InstalledValue(InstalledVersion evidence) => evidence.State switch
    {
        InstalledVersionEvidenceState.Observed => $"{evidence.RawVersion} ({Values(evidence.Sources)})",
        InstalledVersionEvidenceState.Unknown => $"Unknown / Not yet verified. {TextOrUnknown(evidence.Detail)}",
        InstalledVersionEvidenceState.Incomplete => $"Incomplete / Not yet verified. {TextOrUnknown(evidence.Detail)}",
        InstalledVersionEvidenceState.Unavailable => $"Unavailable / Not yet verified. {TextOrUnknown(evidence.Detail)}",
        _ => TextOrUnknown(evidence.Detail)
    };

    private static string RelationSummary(RelationApplicability applicability, CompatibilityEvidenceConfidence confidence,
        ReleaseFamilyId? releaseFamilyId, string constraints)
    {
        var family = releaseFamilyId is null ? "Any reviewed family" : $"Family: {releaseFamilyId.Value.Value}";
        return $"{Label(applicability)} | {Label(confidence)} | {family}. {TextOrUnknown(constraints)}";
    }

    private static string DeviceName(ApplicableDeviceSummary device) => device.ExactModelIds.FirstOrDefault()
        ?? device.Aliases.FirstOrDefault()
        ?? device.DeviceFamilyId;

    internal static string DeviceDisplayName(CompatibilityDeviceSearchResult device, string query)
    {
        var exact = device.ExactModelIds.Concat(device.Aliases)
            .FirstOrDefault(value => value.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase));
        return exact ?? device.ExactModelIds.FirstOrDefault() ?? device.Aliases.FirstOrDefault() ?? device.DeviceFamilyId;
    }

    private static string Values(IEnumerable<string> values)
    {
        var items = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return items.Length == 0 ? "Not specified" : string.Join(", ", items);
    }

    private static string TextOrUnknown(string? value) => string.IsNullOrWhiteSpace(value) ? "Not specified" : value.Trim();

    private static string Label(Enum value)
    {
        if (value is ReleaseFamilyKind.Lts) return "LTS";
        if (value is DeviceSoftwarePurpose.LegacyService) return "Legacy service";
        var text = value.ToString();
        return string.Concat(text.Select((character, index) => index > 0 && char.IsUpper(character) && char.IsLower(text[index - 1])
            ? $" {character}" : character.ToString()));
    }
}
