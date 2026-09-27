using System.Text.RegularExpressions;

namespace AVWorkstationToolkit.Application.Diagnostics;

/// <summary>
/// Identifies a build for people: "1.1.3 RC 1" in titles and "1.1.3-rc.1" in diagnostics.
/// Catalog trust, the runtime folder, and assembly identity keep using the numeric <see cref="Version"/>;
/// a release-candidate label never reaches them. rc.N is the only label this source builds.
/// </summary>
public sealed partial class ProductRelease
{
    public ProductRelease(string version, string prerelease = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        prerelease ??= string.Empty;
        if (prerelease.Length > 0 && !PrereleasePattern().IsMatch(prerelease))
            throw new ArgumentException("A release-candidate label must be rc.N.", nameof(prerelease));
        Version = version;
        Prerelease = prerelease;
    }

    public string Version { get; }
    public string Prerelease { get; }
    public string SemanticVersion => Prerelease.Length == 0 ? Version : $"{Version}-{Prerelease}";

    public string DisplayVersion => Prerelease.Length == 0 ? Version : $"{Version} RC {Prerelease["rc.".Length..]}";

    /// <summary>
    /// Reads the label from an informational version such as "1.1.3-rc.1+commit". A label is accepted only
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

    [GeneratedRegex(@"^rc\.[1-9][0-9]{0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex PrereleasePattern();
}
