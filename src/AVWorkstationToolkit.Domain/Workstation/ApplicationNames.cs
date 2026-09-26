using System.Text;
using System.Text.RegularExpressions;

namespace AVWorkstationToolkit.Domain.Workstation;

/// <summary>
/// Deterministic display-name and publisher normalization. Normalized names group duplicate discoveries on one
/// workstation and compare applications across workstations. They are comparison evidence only: a normalized
/// name never identifies a catalog record strongly enough to authorize an action.
/// </summary>
public static partial class ApplicationNames
{
    public const int MaximumTextLength = 512;

    [GeneratedRegex(@"\([^()]*\)|\[[^\[\]]*\]", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BracketedPattern();

    // Dotted numeric versions, including one glued to a word ("Database200.460.001.00") or prefixed with v.
    [GeneratedRegex(@"v?\d+(?:[._]\d+)+[a-z]?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"(?<![a-z0-9])(?:x64|x86|amd64|arm64|aarch64|x86_64|win64|64-bit|32-bit|64 bit|32 bit|version)(?![a-z0-9])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex NoiseTokenPattern();

    [GeneratedRegex(@"(?<![a-z0-9])(?:arm64|aarch64)(?![a-z0-9])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Arm64Pattern();

    [GeneratedRegex(@"(?<![a-z0-9])(?:x64|amd64|x86_64|win64|64-bit|64 bit)(?![a-z0-9])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex X64Pattern();

    [GeneratedRegex(@"(?<![a-z0-9])(?:x86|32-bit|32 bit)(?![a-z0-9])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex X86Pattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex WhitespacePattern();

    private static readonly HashSet<string> PublisherNoiseWords = new(StringComparer.Ordinal)
    {
        "the", "inc", "incorporated", "llc", "ltd", "limited", "corp", "corporation", "co", "company", "gmbh", "ag",
        "sa", "sas", "srl", "bv", "nv", "plc", "pty", "oy", "ab", "kk", "team", "software", "technologies",
        "technology", "electronics", "systems", "foundation", "project", "community", "contributors", "developers"
    };

    /// <summary>"Wireshark 4.4.0 x64" becomes "wireshark"; "7-Zip 26.03 (x64 edition)" becomes "7-zip".</summary>
    public static string NameKey(string? displayName)
    {
        var text = Clean(displayName).ToLowerInvariant();
        text = BracketedPattern().Replace(text, " ");
        text = NoiseTokenPattern().Replace(text, " ");
        text = VersionPattern().Replace(text, " ");
        text = WhitespacePattern().Replace(text, " ").Trim(' ', '-', '–', '—', '_', ',', '.', ':', ';');
        // A dangling separator left by a removed version ("redistributable - 14.44") is not part of the name.
        while (text.EndsWith(" -", StringComparison.Ordinal)) text = text[..^2].TrimEnd();
        return WhitespacePattern().Replace(text, " ").Trim();
    }

    /// <summary>The name key without separators: "Microsoft Visual Studio Code" becomes "microsoftvisualstudiocode".</summary>
    public static string CompactKey(string? displayName) => Compact(NameKey(displayName));

    public static string Compact(string? value)
    {
        var builder = new StringBuilder();
        foreach (var character in (value ?? string.Empty).ToLowerInvariant())
            if (character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '#') builder.Append(character);
        return builder.ToString();
    }

    /// <summary>"Crestron Electronics, Inc." becomes "crestron"; legal and organizational suffixes are dropped.</summary>
    public static string PublisherKey(string? publisher) => string.Concat(PublisherWords(publisher));

    /// <summary>The first significant publisher word, such as "git" for "The Git Development Community".</summary>
    public static string PublisherLead(string? publisher) => PublisherWords(publisher).FirstOrDefault() ?? string.Empty;

    public static bool PublishersAgree(string? left, string? right)
    {
        var a = PublisherKey(left);
        var b = PublisherKey(right);
        return a.Length > 0 && b.Length > 0 && (a == b || a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal));
    }

    /// <summary>Architecture stated by a display name, or implied by the uninstall view that registered it.</summary>
    public static string Architecture(string? displayName, UninstallHive? hive)
    {
        var text = Clean(displayName);
        if (Arm64Pattern().IsMatch(text)) return "arm64";
        if (X64Pattern().IsMatch(text)) return "x64";
        if (X86Pattern().IsMatch(text)) return "x86";
        return hive switch
        {
            UninstallHive.Machine64 => "x64",
            UninstallHive.Machine32 => "x86",
            _ => string.Empty
        };
    }

    /// <summary>Trims, removes control characters, and bounds descriptive text taken from the registry or a file.</summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var builder = new StringBuilder(Math.Min(value.Length, MaximumTextLength));
        foreach (var character in value)
        {
            if (builder.Length == MaximumTextLength) break;
            builder.Append(char.IsControl(character) ? ' ' : character);
        }
        return WhitespacePattern().Replace(builder.ToString(), " ").Trim();
    }

    private static IEnumerable<string> PublisherWords(string? publisher)
    {
        var text = BracketedPattern().Replace(Clean(publisher).ToLowerInvariant(), " ");
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
            builder.Append(character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' ? character : ' ');
        return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(word => !PublisherNoiseWords.Contains(word));
    }
}
