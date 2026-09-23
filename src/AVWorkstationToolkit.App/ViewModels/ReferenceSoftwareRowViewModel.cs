using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.App.ViewModels;

/// <summary>Every member the main Software table binds. Managed catalog rows and reference-only rows both provide them.</summary>
public interface ISoftwareTableRow
{
    string Name { get; }
    string Vendor { get; }
    string Priority { get; }
    string StatusLabel { get; }
    string StatusDetail { get; }
    string StatusBrush { get; }
    string StatusBorder { get; }
    string StatusForeground { get; }
    string VersionLabel { get; }
    string AvailableLabel { get; }
    string RiskLabel { get; }
    string RiskForeground { get; }
    string Note { get; }
    bool Selected { get; set; }
    bool SelectionEnabled { get; }
    string SelectionHint { get; }
}

/// <summary>
/// Software documented by the descriptive reference catalog that has no package in the app catalog. The row is
/// informational only: it has no installed, version, update, or risk state, can never be selected for an action,
/// and grants no execution authority.
/// </summary>
public sealed class ReferenceSoftwareRowViewModel(
    SoftwareProductId productId,
    string name,
    string vendor,
    IReadOnlyList<DeviceSoftwarePurpose> purposes,
    IReadOnlyList<string> devices) : ISoftwareTableRow
{
    public SoftwareProductId ProductId { get; } = productId;
    public string Name { get; } = name;
    public string Vendor { get; } = vendor;
    public IReadOnlyList<DeviceSoftwarePurpose> Purposes { get; } = purposes;
    public string Priority => string.Empty;
    public string StatusLabel => "Reference only";
    public string StatusDetail => "Listed in the device reference catalog for information. AVWT doesn't install, update, or check this software.";
    public string StatusBrush => "#1D2F45";
    public string StatusBorder => "#365A7B";
    public string StatusForeground => "#9AC7EF";
    public string VersionLabel => string.Empty;
    public string AvailableLabel => string.Empty;
    public string RiskLabel => string.Empty;
    public string RiskForeground => "#B8C3D2";
    public string Note { get; } = DescribeUse(purposes, devices);
    public bool CanSelect => false;

    // The table binds selection two-way, so a setter must exist; a reference-only row is never selected.
    public bool Selected { get => false; set { } }
    public bool SelectionEnabled => false;
    public string SelectionHint => "Reference information only. AVWT can't install or update this software.";

    private static string DescribeUse(IReadOnlyList<DeviceSoftwarePurpose> purposes, IReadOnlyList<string> devices)
    {
        if (purposes.Count == 0) return "Listed in the device reference catalog.";
        var use = JoinWords(purposes.Select(purpose => purpose == DeviceSoftwarePurpose.LegacyService
            ? "legacy service"
            : purpose.ToString().ToLowerInvariant()).ToArray());
        var targets = devices.Count <= 3 ? JoinWords(devices) : $"{string.Join(", ", devices.Take(3))}, and {devices.Count - 3} more";
        return $"{char.ToUpperInvariant(use[0])}{use[1..]} software for {targets}.";
    }

    private static string JoinWords(IReadOnlyList<string> values) => values.Count switch
    {
        1 => values[0],
        2 => $"{values[0]} and {values[1]}",
        _ => $"{string.Join(", ", values.Take(values.Count - 1))}, and {values[^1]}"
    };
}
