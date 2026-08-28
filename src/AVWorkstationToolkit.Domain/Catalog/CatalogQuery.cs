namespace AVWorkstationToolkit.Domain.Catalog;

public sealed record CatalogQuery(
    IReadOnlySet<PackageProfile> Profiles,
    IReadOnlySet<PackagePriority> Priorities,
    string Vendor,
    CatalogDiscipline Discipline,
    IReadOnlySet<PackageRole> Roles,
    string Search,
    QuickView QuickView)
{
    public static CatalogQuery All { get; } = new(
        new HashSet<PackageProfile>(Enum.GetValues<PackageProfile>()),
        new HashSet<PackagePriority>(),
        "All",
        CatalogDiscipline.All,
        new HashSet<PackageRole>(),
        string.Empty,
        QuickView.All);
}

public sealed record CatalogQueryItem(PackageDefinition Package, PackageStatus Status, bool Installed, string AvailableVersion);

public sealed class CatalogQueryService
{
    public IReadOnlyList<CatalogQueryItem> Apply(IEnumerable<CatalogQueryItem> source, CatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        return source.Where(item => Matches(item, query)).ToArray();
    }

    public bool Matches(CatalogQueryItem item, CatalogQuery query)
    {
        var package = item.Package;
        if (query.Profiles.Count > 0 && !query.Profiles.Contains(package.Profile)) return false;
        if (query.Priorities.Count > 0 && !query.Priorities.Contains(package.Priority)) return false;
        if (!string.IsNullOrWhiteSpace(query.Vendor) && !query.Vendor.Equals("All", StringComparison.OrdinalIgnoreCase) &&
            !package.Vendor.Trim().Equals(query.Vendor.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (query.Roles.Count > 0 && !package.Roles.Any(query.Roles.Contains)) return false;
        if (!MatchesDiscipline(package, query.Discipline)) return false;
        if (!MatchesSearch(package, query.Search)) return false;
        return MatchesQuickView(item, query.QuickView);
    }

    public static IReadOnlyList<string> Manufacturers(IEnumerable<PackageDefinition> catalog) =>
        catalog.Select(item => item.Vendor.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool MatchesQuickView(CatalogQueryItem item, QuickView quickView) => quickView switch
    {
        QuickView.Missing => item.Status == PackageStatus.Missing ||
            (!item.Installed && item.Status is PackageStatus.Manual or PackageStatus.NotDetected or PackageStatus.Held),
        QuickView.Updates => item.Status is PackageStatus.UpdateAvailable or PackageStatus.ManualUpdate ||
            (item.Status == PackageStatus.Held && item.Installed && !string.IsNullOrWhiteSpace(item.AvailableVersion)),
        _ => true
    };

    private static bool MatchesSearch(PackageDefinition package, string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var text = string.Join(' ', new[]
        {
            package.Name, package.Id, package.Vendor, package.ProductFamily, package.Note, package.CatalogNotes,
            package.DistributionPolicy.ToToken()
        }.Concat(package.ApplicationTypes.Select(value => value.ToToken()))
         .Concat(package.Roles.Select(value => value.ToToken()))
         .Concat(package.SupportedOperatingSystems.Select(value => value.ToToken()))
         .Concat(package.CatalogTags)
         .Concat(package.WorkflowCategories)
         .Concat(package.InstallationForms.Select(value => value.ToToken())));
        return text.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesDiscipline(PackageDefinition package, CatalogDiscipline discipline)
    {
        if (discipline == CatalogDiscipline.All) return true;
        var accepted = discipline switch
        {
            CatalogDiscipline.DSP => [ApplicationType.DSPAudio, ApplicationType.AmplifierManagement],
            CatalogDiscipline.AudioNetworking => [ApplicationType.AudioNetworking],
            CatalogDiscipline.AVoIP => [ApplicationType.AVoIP],
            CatalogDiscipline.RF => [ApplicationType.WirelessRF],
            CatalogDiscipline.Conferencing => [ApplicationType.Conferencing, ApplicationType.CameraPTZ],
            CatalogDiscipline.Displays => [ApplicationType.DisplayProjector, ApplicationType.DigitalSignage],
            CatalogDiscipline.DvLED => [ApplicationType.DvLEDVideoWall],
            CatalogDiscipline.Control => [ApplicationType.ControlSystem],
            CatalogDiscipline.Broadcast => [ApplicationType.BroadcastVideo],
            CatalogDiscipline.MediaShow => [ApplicationType.MediaServerShowControl],
            CatalogDiscipline.Lighting => [ApplicationType.LightingControl],
            CatalogDiscipline.Intercom => [ApplicationType.Intercom],
            CatalogDiscipline.Utilities => [ApplicationType.FieldUtility, ApplicationType.NetworkUtility, ApplicationType.SerialUtility, ApplicationType.UsbDiagnostic, ApplicationType.EDIDHDCP],
            CatalogDiscipline.Measurement => [ApplicationType.AudioMeasurement, ApplicationType.LoudspeakerPrediction],
            CatalogDiscipline.FirmwareCommissioning => [ApplicationType.FirmwareUtility],
            CatalogDiscipline.Development => [ApplicationType.Development],
            _ => Array.Empty<ApplicationType>()
        };
        return package.ApplicationTypes.Any(accepted.Contains) ||
            discipline == CatalogDiscipline.FirmwareCommissioning && package.FirmwareUtility == true;
    }
}
