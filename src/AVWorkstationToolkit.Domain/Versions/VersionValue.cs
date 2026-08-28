using System.Globalization;
using System.Text.RegularExpressions;

namespace AVWorkstationToolkit.Domain.Versions;

public readonly record struct VersionValue : IComparable<VersionValue>
{
    private static readonly Regex SupportedPattern = new(@"^\d+(?:\.\d+){1,3}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly int major;
    private readonly int minor;
    private readonly int build;
    private readonly int revision;

    private VersionValue(int major, int minor, int build, int revision)
    {
        this.major = major;
        this.minor = minor;
        this.build = build;
        this.revision = revision;
    }

    public static VersionValue Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!SupportedPattern.IsMatch(value))
        {
            throw new FormatException($"Version must contain two to four numeric fields: {value}");
        }
        var input = value.Split('.');
        var normalized = new int[4];
        for (var index = 0; index < input.Length; index++)
        {
            if (!int.TryParse(input[index], NumberStyles.None, CultureInfo.InvariantCulture, out normalized[index]) || normalized[index] < 0)
            {
                throw new FormatException($"Version contains an invalid numeric field: {value}");
            }
        }
        return new VersionValue(normalized[0], normalized[1], normalized[2], normalized[3]);
    }

    public static bool TryParse(string? value, out VersionValue result)
    {
        try { result = Parse(value!); return true; }
        catch (Exception exception) when (exception is ArgumentNullException or FormatException) { result = default; return false; }
    }

    public int CompareTo(VersionValue other)
    {
        var comparison = major.CompareTo(other.major);
        if (comparison != 0) return comparison;
        comparison = minor.CompareTo(other.minor);
        if (comparison != 0) return comparison;
        comparison = build.CompareTo(other.build);
        return comparison != 0 ? comparison : revision.CompareTo(other.revision);
    }

    public string Normalized => $"{major}.{minor}.{build}.{revision}";
    public override string ToString() => Normalized;
}

public static partial class VersionSortKey
{
    [GeneratedRegex(@"^\s*[vV]?(?<numeric>\d+(?:\.\d+){0,7})(?<suffix>(?:[-+][0-9A-Za-z.-]+)?)\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SortPattern();

    public static string Create(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "4|";
        var value = label.Trim();
        if (value.StartsWith("Known: ", StringComparison.OrdinalIgnoreCase)) value = value[7..].Trim();
        if (value.Equals("Not installed", StringComparison.OrdinalIgnoreCase)) return "2|";
        if (value.Equals("Not evaluated", StringComparison.OrdinalIgnoreCase)) return "3|";
        var match = SortPattern().Match(value);
        if (!match.Success) return $"1|{value.ToUpperInvariant()}";
        var input = match.Groups["numeric"].Value.Split('.');
        var segments = new string[8];
        for (var index = 0; index < segments.Length; index++)
        {
            var part = index < input.Length ? input[index].TrimStart('0') : string.Empty;
            if (part.Length == 0) part = "0";
            segments[index] = $"{part.Length:D3}:{part}";
        }
        return $"0|{string.Join('|', segments)}|{match.Groups["suffix"].Value.ToUpperInvariant()}";
    }
}
