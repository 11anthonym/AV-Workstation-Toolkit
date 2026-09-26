using System.Globalization;
using AVWorkstationToolkit.App.Commands;
using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Workstation;

namespace AVWorkstationToolkit.App.ViewModels;

public sealed class ProfileApplicationRowViewModel(
    string catalogId,
    string name,
    string vendor,
    string kind,
    string winGetId,
    bool isCustom) : ObservableObject
{
    private bool selected;
    private bool required = true;

    public string CatalogId { get; } = catalogId;
    public string Name { get; } = name;
    public string Vendor { get; } = vendor;
    public string Kind { get; } = kind;
    public string WinGetId { get; } = winGetId;
    public bool IsCustom { get; } = isCustom;
    public bool Selected { get => selected; set => SetProperty(ref selected, value); }
    public bool Required { get => required; set => SetProperty(ref required, value); }
    public string Key => ProfileKeys.For(CatalogId, Name);
}

/// <summary>
/// Creates or revises a deployment profile: catalog applications, any uncatalogued applications described by name, and
/// descriptive manual checks. A profile describes desired state only; it can't add installation authority.
/// </summary>
public sealed class ProfileEditorViewModel : ObservableObject
{
    private readonly IMigrationFileService files;
    private readonly TimeProvider timeProvider;
    private readonly List<ProfileApplicationRowViewModel> applications;
    private readonly BatchObservableCollection<ProfileApplicationRowViewModel> visible = [];
    private readonly string? existingProfileId;
    private string name;
    private string description;
    private string versionText;
    private string search = string.Empty;
    private bool showSelectedOnly;
    private string checksText;
    private string customName = string.Empty;
    private string customPublisher = string.Empty;
    private string status = string.Empty;

    public ProfileEditorViewModel(
        ApplicationIdentityCatalog identities,
        IMigrationFileService files,
        DeploymentProfile? existing = null,
        IEnumerable<ProfileApplication>? seed = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(identities);
        this.files = files ?? throw new ArgumentNullException(nameof(files));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        existingProfileId = existing?.ProfileId;
        name = existing?.Name ?? string.Empty;
        description = existing?.Description ?? string.Empty;
        versionText = ((existing?.ProfileVersion ?? 0) + 1).ToString(CultureInfo.InvariantCulture);
        checksText = string.Join(Environment.NewLine, existing?.Checks.Select(check => check.Text) ?? []);
        applications = identities.Applications
            .Where(application => application.Package.SupportedOperatingSystems.Contains(SupportedOperatingSystem.Windows) &&
                application.Package.DeploymentClass is not (DeploymentClass.WebOnly or DeploymentClass.ServerOnly or DeploymentClass.Embedded))
            .OrderBy(application => application.Name, StringComparer.OrdinalIgnoreCase)
            .Select(application => new ProfileApplicationRowViewModel(application.Id, application.Name, application.Vendor,
                application.Management switch
                {
                    ApplicationManagement.ManagedWinGet => "Automatic (managed WinGet)",
                    ApplicationManagement.VendorHandoff => "Manual (vendor)",
                    ApplicationManagement.InventoryOnly => "Manual (inventory only)",
                    _ => "Manual (catalog knowledge)"
                }, string.Empty, false))
            .ToList();
        foreach (var row in applications) Track(row);
        foreach (var wanted in (existing?.Applications ?? []).Concat(seed ?? []))
            Select(wanted);
        visibleView = new System.Collections.ObjectModel.ReadOnlyObservableCollection<ProfileApplicationRowViewModel>(visible);
        AddCustomCommand = new RelayCommand(_ => AddCustom(), _ => ApplicationNames.CompactKey(CustomName).Length > 0);
        SaveCommand = new RelayCommand(_ => Save(applyAfterSave: false));
        SaveAndApplyCommand = new RelayCommand(_ => Save(applyAfterSave: true));
        CancelCommand = new RelayCommand(_ => CloseRequested?.Invoke(this, EventArgs.Empty));
        Rebuild();
    }

    private readonly System.Collections.ObjectModel.ReadOnlyObservableCollection<ProfileApplicationRowViewModel> visibleView;

    public event EventHandler? CloseRequested;

    public IReadOnlyList<ProfileApplicationRowViewModel> VisibleApplications => visibleView;
    public RelayCommand AddCustomCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAndApplyCommand { get; }
    public RelayCommand CancelCommand { get; }
    public DeploymentProfile? SavedProfile { get; private set; }
    public bool ApplyAfterSave { get; private set; }
    public string Title => existingProfileId is null ? "New deployment profile" : "Revise deployment profile";

    public string Name { get => name; set { if (SetProperty(ref name, value ?? string.Empty)) OnPropertyChanged(nameof(ProfileIdText)); } }
    public string Description { get => description; set => SetProperty(ref description, value ?? string.Empty); }
    public string VersionText { get => versionText; set => SetProperty(ref versionText, value ?? string.Empty); }
    public string ChecksText { get => checksText; set => SetProperty(ref checksText, value ?? string.Empty); }
    public string ProfileIdText => $"Profile ID: {ProfileId}";
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public string SelectionSummary => $"{applications.Count(item => item.Selected)} applications selected";

    public string Search { get => search; set { if (SetProperty(ref search, value ?? string.Empty)) Rebuild(); } }
    public bool ShowSelectedOnly { get => showSelectedOnly; set { if (SetProperty(ref showSelectedOnly, value)) Rebuild(); } }

    public string CustomName
    {
        get => customName;
        set { if (SetProperty(ref customName, value ?? string.Empty)) AddCustomCommand.RaiseCanExecuteChanged(); }
    }

    public string CustomPublisher { get => customPublisher; set => SetProperty(ref customPublisher, value ?? string.Empty); }

    private string ProfileId => existingProfileId ?? ProfileKeys.Slug(Name);

    /// <summary>Builds and validates the profile from the current choices; throws when a value is invalid.</summary>
    public DeploymentProfile BuildProfile()
    {
        if (!int.TryParse(VersionText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            throw new WorkstationDocumentException("The profile version must be a whole number.");
        var checks = new List<ProfileCheck>();
        foreach (var line in ChecksText.Split('\n').Select(value => ApplicationNames.Clean(value)).Where(value => value.Length > 0))
        {
            var id = ProfileKeys.Slug(line, "check");
            var candidate = id;
            for (var suffix = 2; checks.Any(check => check.Id == candidate); suffix++)
                candidate = $"{(id.Length > 58 ? id[..58] : id)}-{suffix}";
            checks.Add(new ProfileCheck(candidate, line));
        }
        var selected = applications.Where(item => item.Selected)
            .OrderBy(item => item.IsCustom)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new ProfileApplication(item.CatalogId, item.Name, item.IsCustom ? item.Vendor : string.Empty, item.WinGetId, item.Required))
            .ToArray();
        return DeploymentProfileCodec.Validate(new DeploymentProfile(ProfileId, Name, version, Description, timeProvider.GetUtcNow(), selected, checks));
    }

    private void Save(bool applyAfterSave)
    {
        try
        {
            var profile = BuildProfile();
            var path = files.PickProfileToSave($"{profile.ProfileId}-v{profile.ProfileVersion}.json");
            if (path is null)
            {
                Status = "Save cancelled.";
                return;
            }
            files.Write(path, DeploymentProfileCodec.Serialize(profile));
            SavedProfile = profile;
            ApplyAfterSave = applyAfterSave;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            Status = $"Couldn't save the profile. {DiagnosticsRedactor.Sanitize(exception.Message)}";
        }
    }

    private void Select(ProfileApplication wanted)
    {
        var row = applications.FirstOrDefault(item => item.Key == wanted.Key);
        if (row is null)
        {
            row = new ProfileApplicationRowViewModel(wanted.CatalogId, wanted.DisplayName.Length > 0 ? wanted.DisplayName : wanted.CatalogId,
                wanted.Publisher, "Manual (not in catalog)", wanted.WinGetId, true);
            applications.Add(row);
            Track(row);
        }
        row.Selected = true;
        row.Required = wanted.Required;
    }

    private void AddCustom()
    {
        var display = ApplicationNames.Clean(CustomName);
        Select(new ProfileApplication(string.Empty, display, ApplicationNames.Clean(CustomPublisher)));
        CustomName = string.Empty;
        CustomPublisher = string.Empty;
        Search = string.Empty;
        Rebuild();
    }

    private void Rebuild()
    {
        var query = Search.Trim();
        visible.ReplaceAll(applications
            .Where(item => !ShowSelectedOnly || item.Selected)
            .Where(item => query.Length == 0 || $"{item.Name} {item.Vendor} {item.CatalogId}".Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Selected && ShowSelectedOnly)
            .ThenBy(item => item.IsCustom ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray());
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private void Track(ProfileApplicationRowViewModel row) =>
        row.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SelectionSummary));
}
