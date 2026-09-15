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
    string DetailType { get; }
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
    private string intentStatus = "This information is for reference. Links open vendor documentation in your browser.";

    private CompatibilityDetailViewModel(
        CompatibilityCatalogQueryService queries,
        IValidatedUserHandoffService? handoffs,
        string contextId,
        string detailType,
        string name,
        string subtitle,
        IReadOnlyList<CatalogDetailGroup> groups,
        IReadOnlyList<DetailLinkViewModel> links,
        IReadOnlyList<RelatedSoftwareViewModel> relatedSoftware)
    {
        this.queries = queries;
        this.handoffs = handoffs;
        ContextId = contextId;
        DetailType = detailType;
        Name = name;
        Subtitle = subtitle;
        Groups = groups;
        Links = links;
        RelatedSoftware = relatedSoftware;
    }

    public string ContextId { get; }
    public string DetailType { get; }
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
            Group("Versions found on this PC", installed.Select(item =>
                (InstalledLabel(item), InstalledValue(item))).ToArray()),
            Group("Version branches", families.Count == 0
                ? [("Status", "No documented version branches are listed.")]
                : families.Select(item => ($"{Label(item.Kind)} — {item.Branch}",
                    $"{Label(item.Lifecycle)}. {TextOrUnknown(item.Constraints)}")).ToArray())
        };
        foreach (var device in devices)
        {
            groups.Add(Group(DeviceName(device), device.Purposes.Select(purpose =>
                (Label(purpose.Purpose), RelationSummary(purpose.Applicability, purpose.Confidence,
                    ReleaseFamilyText(queries, product.Id, purpose.ReleaseFamilyId), purpose.Constraints))).ToArray()));
        }

        var viewModel = new CompatibilityDetailViewModel(queries, handoffs, product.Id.Value, "Software", product.Name,
            $"{product.Vendor} · {Label(product.Lifecycle)} · Software reference", groups, [], []);
        viewModel.LinksInternal.Add(viewModel.CreateLink("Official product page", product.OfficialSourceUri,
            OpenOfficialUriIntent.FromCompatibilityProduct(product)));
        foreach (var family in families)
            viewModel.AddUniqueLink($"{Label(family.Kind)} versions — {EvidenceLabel(family.EvidenceKind)}", family.EvidenceUri,
                OpenOfficialUriIntent.FromCompatibilityRelease(product.Id, family));
        foreach (var device in devices)
            foreach (var purpose in device.Purposes)
                viewModel.AddUniqueLink($"{DeviceName(device)} — {Label(purpose.Purpose)} documentation", purpose.EvidenceUri,
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
            device.Hardware is null
                ? Group("Device identity", ("Device family", device.DeviceFamilyId),
                    ("Models", Values(device.ExactModelIds)), ("Aliases", Values(device.Aliases)))
                : Group("Hardware identity", ("Manufacturer", device.Hardware.Manufacturer),
                    ("Exact model", TextOrUnknown(device.Hardware.ExactModel)), ("Family", device.Hardware.Family),
                    ("Category", Label(device.Hardware.Category)), ("Coverage", CoverageLabel(device.LookupState)),
                    ("Aliases", Values(device.Hardware.Aliases)))
        };
        if (softwareGroups.Count == 0)
            groups.Add(Group("Software for this device", ("Status", CoverageDetail(device.LookupState))));
        foreach (var purposeGroup in softwareGroups)
            groups.Add(Group(Label(purposeGroup.Purpose), purposeGroup.Software.Select(item =>
                (item.ProductName, RelationSummary(item.Applicability, item.Confidence,
                    ReleaseFamilyText(queries, item.ProductId, item.ReleaseFamilyId), item.Constraints))).ToArray()));

        var viewModel = new CompatibilityDetailViewModel(queries, handoffs, device.DeviceFamilyId, "Device", displayName,
            DocumentedSoftwareSummary(softwareGroups.Sum(group => group.Software.Count)),
            groups, [], []);
        var relatedSoftware = softwareGroups.SelectMany(group => group.Software).ToArray();
        foreach (var softwareGroup in relatedSoftware.GroupBy(item => item.ProductId))
        {
            var software = softwareGroup.First();
            viewModel.RelatedSoftwareInternal.Add(new RelatedSoftwareViewModel(
                software.ProductName,
                string.Join(", ", softwareGroup.Select(item => Label(item.Purpose)).Distinct(StringComparer.OrdinalIgnoreCase)),
                "Open version information and the devices this software supports.",
                new AsyncRelayCommand(() => viewModel.NavigateToProductAsync(software.ProductId))));
        }
        foreach (var group in softwareGroups)
        {
            foreach (var software in group.Software)
            {
                viewModel.AddUniqueLink($"{software.ProductName} — {Label(software.Purpose)} documentation", software.EvidenceUri,
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
            IntentStatus = $"Preview only: checked the {LinkKind(intent.Kind)} for {Name}. No browser or download was started.";
            return;
        }
        try
        {
            handoffs.OpenOfficialUri(intent);
            IntentStatus = $"Opened the {LinkKind(intent.Kind)} for {Name}.";
        }
        catch (Exception exception)
        {
            IntentStatus = $"Couldn't open the link. {DiagnosticsRedactor.Sanitize(exception.Message)}";
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
        InstalledVersionEvidenceState.Unknown => "We haven't verified which versions are installed.",
        InstalledVersionEvidenceState.Incomplete => $"Some installation information is missing. {TextOrUnknown(evidence.Detail)}",
        InstalledVersionEvidenceState.Unavailable => $"We couldn't check installed versions. {TextOrUnknown(evidence.Detail)}",
        _ => TextOrUnknown(evidence.Detail)
    };

    private static string RelationSummary(RelationApplicability applicability, CompatibilityEvidenceConfidence confidence,
        string releaseFamily, string constraints)
    {
        return $"{Label(applicability)}. {ConfidenceLabel(confidence)}. {releaseFamily}. {TextOrUnknown(constraints)}";
    }

    private static string ReleaseFamilyText(
        CompatibilityCatalogQueryService queries,
        SoftwareProductId productId,
        ReleaseFamilyId? releaseFamilyId)
    {
        if (releaseFamilyId is null) return "No version-branch restriction listed";
        var family = queries.GetReleaseFamilies(productId)
            .FirstOrDefault(item => item.Id == releaseFamilyId.Value);
        return family is null ? "A specific version branch applies" : $"{Label(family.Kind)}: {family.Branch}";
    }

    private static string ConfidenceLabel(CompatibilityEvidenceConfidence confidence) => confidence switch
    {
        CompatibilityEvidenceConfidence.VendorDocumented => "Vendor documented",
        CompatibilityEvidenceConfidence.PhysicalInstallVerified => "Verified on hardware",
        _ => "Needs verification"
    };

    private static string EvidenceLabel(CompatibilityEvidenceKind kind) => kind switch
    {
        CompatibilityEvidenceKind.VendorProductPage => "Vendor product page",
        CompatibilityEvidenceKind.VendorReleaseNotes => "Vendor release notes",
        CompatibilityEvidenceKind.VendorCompatibilityMatrix => "Vendor compatibility guide",
        CompatibilityEvidenceKind.VendorSupportArticle => "Vendor support article",
        CompatibilityEvidenceKind.PhysicalInstallRecord => "Installation record",
        _ => "Vendor documentation"
    };

    private static string LinkKind(OfficialUriKind kind) => kind == OfficialUriKind.Product
        ? "vendor product page"
        : "vendor documentation";

    private static string DocumentedSoftwareSummary(int count) => count == 1
        ? "Device reference · 1 documented software link"
        : $"Device reference · {count} documented software links";

    private static string DeviceName(ApplicableDeviceSummary device) => device.ExactModelIds.FirstOrDefault()
        ?? device.Aliases.FirstOrDefault()
        ?? device.DeviceFamilyId;

    internal static string DeviceDisplayName(CompatibilityDeviceSearchResult device, string query)
    {
        if (device.Hardware is { ExactModel.Length: > 0 } hardware) return hardware.ExactModel;
        if (device.Hardware is { } familyHardware) return familyHardware.Family;
        var exact = device.ExactModelIds.Concat(device.Aliases)
            .FirstOrDefault(value => value.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase));
        return exact ?? device.ExactModelIds.FirstOrDefault() ?? device.Aliases.FirstOrDefault() ?? device.DeviceFamilyId;
    }

    private static string CoverageLabel(HardwareLookupState state) => state switch
    {
        HardwareLookupState.KnownExactModelWithVerifiedRelationships => "Documented software available",
        HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet => "Software support not yet verified",
        HardwareLookupState.KnownFamilyWithVerifiedRelationships => "Documented family software available",
        HardwareLookupState.KnownFamilyWithUnresolvedCoverage => "Family software support not yet verified",
        _ => "Software relationship listed"
    };

    private static string CoverageDetail(HardwareLookupState state) => state switch
    {
        HardwareLookupState.KnownExactModelWithNoVerifiedRelationshipsYet =>
            "This device is listed, but we haven't verified which software applies. This doesn't mean no software is needed.",
        HardwareLookupState.KnownFamilyWithUnresolvedCoverage =>
            "This device family is listed, but we haven't verified which software applies. This doesn't mean no software is needed.",
        _ => "No documented software is linked to this result yet."
    };

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
        if (value is HardwareDeviceCategory.AudioDsp) return "Audio DSP";
        if (value is HardwareDeviceCategory.AvOverIp) return "AV-over-IP";
        if (value is HardwareDeviceCategory.AvInterface) return "AV interface";
        if (value is HardwareDeviceCategory.DigitalSignage) return "Digital signage";
        if (value is HardwareDeviceCategory.NetworkInfrastructure) return "Network infrastructure";
        var text = value.ToString();
        return string.Concat(text.Select((character, index) => index > 0 && char.IsUpper(character) && char.IsLower(text[index - 1])
            ? $" {character}" : character.ToString()));
    }
}
