namespace AVWorkstationToolkit.Infrastructure.Windows.Files;

public static class ProductionRuntimePolicy
{
    public const string DataDirectoryName = "AVWorkstationToolkit";

    public static string RequireDataRoot(string path)
    {
        var root = ActionArtifactPathPolicy.RequireAbsoluteNonRoot(path, "production data root");
        var expected = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataDirectoryName));
        if (!string.Equals(root, expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Production actions require the canonical per-user AV Workstation Toolkit data root.");
        Directory.CreateDirectory(root);
        RejectReparseChain(Path.GetDirectoryName(root)!, root);
        return root;
    }

    public static string RequireApplicationRoot(string dataRoot, string applicationRoot)
    {
        var root = RequireDataRoot(dataRoot);
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
