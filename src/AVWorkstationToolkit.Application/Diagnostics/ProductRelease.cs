using System.Text.RegularExpressions;

namespace AVWorkstationToolkit.Application.Diagnostics;

/// <summary>
/// Identifies a build for people: "1.1.1 Beta 2" in titles and "1.1.1-beta.2" in diagnostics.
/// Catalog trust, the runtime folder, and assembly identity keep using the numeric <see cref="Version"/>;
/// a pre-release label never reaches them.
/// </summary>
public sealed partial class ProductRelease
{
    public ProductRelease(string version, string prerelease = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        prerelease ??= string.Empty;
        if (prerelease.Length > 0 && !PrereleasePattern().IsMatch(prerelease))
            throw new ArgumentException("A pre-release label must be alpha.N, beta.N, or rc.N.", nameof(prerelease));
        Version = version;
        Prerelease = prerelease;
    }

    public string Version { get; }
    public string Prerelease { get; }
    public string SemanticVersion => Prerelease.Length == 0 ? Version : $"{Version}-{Prerelease}";

    public string DisplayVersion
    {
        get
        {
            if (Prerelease.Length == 0) return Version;
            var parts = Prerelease.Split('.');
            var stage = parts[0] switch { "alpha" => "Alpha", "beta" => "Beta", _ => "RC" };
            return $"{Version} {stage} {parts[1]}";
        }
    }

    /// <summary>
    /// Reads the label from an informational version such as "1.1.1-beta.2+commit". A label is accepted only
    /// when it extends exactly <paramref name="version"/>; anything else is treated as a build without one.
    /// </summary>
    public static ProductRelease FromInformationalVersion(string version, string? informationalVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var semantic = (informationalVersion ?? string.Empty).Split('+', 2)[0];
        var prefix = version + "-";
        var label = semantic.StartsWith(prefix, StringComparison.Ordinal) ? semantic[prefix.Length..] : string.Empty;
        return new(version, PrereleasePattern().IsMatch(label) ? label : string.Empty);
    }

    [GeneratedRegex("^(?:alpha|beta|rc)\\.[1-9][0-9]{0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex PrereleasePattern();
}
