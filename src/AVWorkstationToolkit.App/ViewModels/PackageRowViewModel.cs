using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Domain.Versions;
using AVWorkstationToolkit.Application.Details;

namespace AVWorkstationToolkit.App.ViewModels;

public sealed class PackageRowViewModel : ObservableObject
{
    private readonly Action selectionChanged;
    private bool selected;
    private bool busy;

    public PackageRowViewModel(PackageState state, int order, Action selectionChanged)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        Order = order;
        this.selectionChanged = selectionChanged ?? throw new ArgumentNullException(nameof(selectionChanged));
        StableSortKey = Id.ToUpperInvariant();
        ApplicationSortKey = $"{Name.ToUpperInvariant()}|{StableSortKey}";
        VendorSortKey = $"{Vendor.ToUpperInvariant()}|{StableSortKey}";
        var version = VersionLabel.StartsWith("Catalog: ", StringComparison.Ordinal) ? VersionLabel[9..] : VersionLabel;
        VersionSortKey = AVWorkstationToolkit.Domain.Versions.VersionSortKey.Create(version);
    }

    public PackageState State { get; }
    public PackageDefinition Package => State.Package;
    public int Order { get; }
    public string Id => Package.Id;
    public string Name => Package.Name;
    public string Vendor => Package.Vendor;
    public string Priority => Package.Priority switch
    {
        PackagePriority.P1 => "Priority 1",
        PackagePriority.P2 => "Priority 2",
        PackagePriority.Dev => "Developer",
        _ => "Utility"
    };
    public PackageRisk Risk => Package.Risk;
    public string RiskLabel => Risk switch
    {
        PackageRisk.Driver => "Driver change",
        PackageRisk.Service => "Background service",
        PackageRisk.Listener => "Accepts network connections",
        _ => "Low impact"
    };
    public string RiskForeground => Risk switch
    {
        PackageRisk.Driver => "#FFB86B",
        PackageRisk.Service => "#E6A6FF",
        PackageRisk.Listener => "#FF9C9C",
        _ => "#7FCFAF"
    };
    public string Note => Package.Note;
    public PackageStatus Status => State.Status;
    public string StatusDetail => PackageStatePresentation.Detail(State);
    public string StatusLabel => PackageStatePresentation.Status(State);
    public string StatusBrush => Status switch
    {
        PackageStatus.Current => "#11372D",
        PackageStatus.Missing => "#123454",
        PackageStatus.UpdateAvailable => "#33215C",
        PackageStatus.ManualUpdate or PackageStatus.Held or PackageStatus.InventoryIncomplete => "#3B2B13",
        PackageStatus.Inventory => "#173349",
        PackageStatus.InventoryUnavailable or PackageStatus.CheckUnavailable => "#2D2B45",
        PackageStatus.Awareness => "#1D2F45",
        PackageStatus.Error => "#451A22",
        _ => "#252D39"
    };
    public string StatusBorder => Status switch
    {
        PackageStatus.Current => "#226C57",
        PackageStatus.Missing => "#245B8D",
        PackageStatus.UpdateAvailable => "#6745A2",
        PackageStatus.ManualUpdate or PackageStatus.Held or PackageStatus.InventoryIncomplete => "#7A5B20",
        PackageStatus.Inventory => "#2C617E",
        PackageStatus.InventoryUnavailable or PackageStatus.CheckUnavailable => "#55517A",
        PackageStatus.Awareness => "#365A7B",
        PackageStatus.Error => "#893044",
        _ => "#485568"
    };
    public string StatusForeground => Status switch
    {
        PackageStatus.Current => "#66E1B5",
        PackageStatus.Missing => "#79BEFF",
        PackageStatus.UpdateAvailable => "#C4A4FF",
        PackageStatus.ManualUpdate or PackageStatus.Held or PackageStatus.InventoryIncomplete => "#FFD27A",
        PackageStatus.Inventory => "#8BD2FF",
        PackageStatus.InventoryUnavailable or PackageStatus.CheckUnavailable => "#C8C3FF",
        PackageStatus.Awareness => "#9AC7EF",
        PackageStatus.Error => "#FF9AAA",
        _ => "#B8C3D2"
    };
    public string VersionLabel => Status == PackageStatus.Awareness && Package.KnownVersion.Length > 0
        ? $"Catalog: {Package.KnownVersion}"
        : PackageStatePresentation.InstalledVersion(State);
    public string AvailableLabel => State.AvailableVersion.Length == 0 ? string.Empty
        : Status is PackageStatus.UpdateAvailable or PackageStatus.ManualUpdate
            ? $"Available: {State.AvailableVersion}"
            : $"Catalog: {State.AvailableVersion}";
    public PackageAction Action => State.Action;
    public bool CanSelect => State.CanSelect && Package.HasManagedExecutionAuthority;
    public bool SelectionEnabled => CanSelect && !busy;
    public string SelectionHint => PackageStatePresentation.SelectionHint(State);

    public bool Selected
    {
        get => selected;
        set
        {
            var allowed = value && SelectionEnabled;
            if (!SetProperty(ref selected, allowed)) return;
            selectionChanged();
        }
    }

    public string StableSortKey { get; }
    public string ApplicationSortKey { get; }
    public string VendorSortKey { get; }
    public int PrioritySortKey => Package.Priority switch { PackagePriority.P1 => 0, PackagePriority.P2 => 1, PackagePriority.Utility => 2, PackagePriority.Dev => 3, _ => 99 };
    public int StatusSortKey => Status switch
    {
        PackageStatus.UpdateAvailable => 0,
        PackageStatus.Missing => 1,
        PackageStatus.ManualUpdate => 2,
        PackageStatus.Held => 3,
        PackageStatus.Error => 4,
        PackageStatus.InventoryIncomplete => 5,
        PackageStatus.InventoryUnavailable => 6,
        PackageStatus.CheckUnavailable => 7,
        PackageStatus.Current => 8,
        PackageStatus.Inventory => 9,
        PackageStatus.NotDetected => 10,
        PackageStatus.Manual => 11,
        PackageStatus.Awareness => 12,
        _ => 99
    };
    public string VersionSortKey { get; }
    public int RiskSortKey => Risk switch { PackageRisk.None => 0, PackageRisk.Service => 1, PackageRisk.Listener => 2, PackageRisk.Driver => 3, _ => 99 };

    internal void SetBusy(bool value)
    {
        if (busy == value) return;
        busy = value;
        OnPropertyChanged(nameof(SelectionEnabled));
    }

    internal void RestoreSelection()
    {
        if (!CanSelect || selected) return;
        selected = true;
        OnPropertyChanged(nameof(Selected));
        selectionChanged();
    }
}
