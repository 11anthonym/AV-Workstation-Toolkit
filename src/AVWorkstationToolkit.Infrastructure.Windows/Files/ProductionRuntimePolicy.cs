namespace AVWorkstationToolkit.Infrastructure.Windows.Files;

public static class ProductionRuntimePolicy
{
    public const string DataDirectoryName = "AVWorkstationToolkit";

    /// <summary>The name prefix of the disposable data root that package QA gives the packaged production smoke.</summary>
    public const string PackageQaDataRootPrefix = "AVWorkstationToolkit-package-qa-";

    public static string RequireDataRoot(string path)
    {
        var root = ActionArtifactPathPolicy.RequireAbsoluteNonRoot(path, "production data root");
        if (!string.Equals(root, CanonicalDataRoot(), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Production actions require the canonical per-user AV Workstation Toolkit data root.");
        Directory.CreateDirectory(root);
        RejectReparseChain(Path.GetDirectoryName(root)!, root);
        return root;
    }

    public static string RequireApplicationRoot(string dataRoot, string applicationRoot) =>
        RequireVersionedRuntime(RequireDataRoot(dataRoot), applicationRoot);

    /// <summary>
    /// Accepts only a disposable AVWorkstationToolkit-package-qa-&lt;32 hex&gt; folder directly beneath the user's temporary
    /// folder, so package QA can run the packaged production composition without reading or writing the user's profile.
    /// Only the launcher's production smoke uses it; the worker and the worker launcher's launch path still require the
    /// canonical root.
    /// </summary>
    public static string RequirePackageQaDataRoot(string path)
    {
        var root = ActionArtifactPathPolicy.RequireAbsoluteNonRoot(path, "package QA data root");
        var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(root);
        if (!string.Equals(Path.GetDirectoryName(root), temporary, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(PackageQaDataRootPrefix, StringComparison.Ordinal) ||
            name.Length != PackageQaDataRootPrefix.Length + 32 ||
            !name[PackageQaDataRootPrefix.Length..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new IOException($"A package QA data root must be a {PackageQaDataRootPrefix}<32 lowercase hex> folder directly beneath the temporary folder.");
        var canonical = CanonicalDataRoot();
        if (string.Equals(root, canonical, StringComparison.OrdinalIgnoreCase) ||
            root.StartsWith(canonical + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A package QA data root cannot be inside the user's AV Workstation Toolkit data root.");
        Directory.CreateDirectory(root);
        RejectReparseChain(temporary, root);
        return root;
    }

    public static string RequirePackageQaApplicationRoot(string dataRoot, string applicationRoot) =>
        RequireVersionedRuntime(RequirePackageQaDataRoot(dataRoot), applicationRoot);

    private static string CanonicalDataRoot() => Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataDirectoryName));

    private static string RequireVersionedRuntime(string root, string applicationRoot)
    {
        var runtimeRoot = Path.Combine(root, "runtime");
        var candidate = ActionArtifactPathPolicy.RequireAbsoluteNonRoot(applicationRoot, "production application root");
        if (!Directory.Exists(candidate) ||
            !string.Equals(Path.GetDirectoryName(candidate), runtimeRoot, StringComparison.OrdinalIgnoreCase) ||
            !Version.TryParse(Path.GetFileName(candidate), out _))
            throw new IOException("The production application root is not a versioned direct child of the canonical runtime directory.");
        RejectReparseChain(root, candidate);
        return candidate;
    }

    private static void RejectReparseChain(string boundary, string candidate)
    {
        var stop = Path.GetFullPath(boundary).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = Path.GetFullPath(candidate);
        if (!current.Equals(stop, StringComparison.OrdinalIgnoreCase) &&
            !current.StartsWith(stop + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The production path escaped its reviewed boundary.");
        while (true)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The production path contains an unsupported reparse point.");
            if (current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Equals(stop, StringComparison.OrdinalIgnoreCase))
                return;
            current = Directory.GetParent(current)?.FullName
                ?? throw new IOException("The production path is not contained by its reviewed boundary.");
        }
    }
}
