namespace AVWorkstationToolkit.Infrastructure.Windows.Files;

/// <summary>
/// Restricts developer-only live rehearsal artifacts to one direct, regular
/// child of the current user's temporary directory.
/// </summary>
public static class LiveRehearsalRootPolicy
{
    public const string DirectoryPrefix = "awt-phase12-";

    public static string RequireExisting(string explicitRoot)
    {
        var root = ActionArtifactPathPolicy.RequireAbsoluteNonRoot(explicitRoot, "live rehearsal root");
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(root), temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(root).StartsWith(DirectoryPrefix, StringComparison.Ordinal) ||
            !Directory.Exists(root))
            throw new IOException("Live rehearsal requires an existing isolated Phase 12 root directly beneath the user temporary directory.");

        RejectReparse(root);
        return root;
    }

    private static void RejectReparse(string root)
    {
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = root;
        while (true)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The live rehearsal path contains an unsupported reparse point.");
            if (string.Equals(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), temporaryRoot, StringComparison.OrdinalIgnoreCase))
                return;
            current = Directory.GetParent(current)?.FullName
                ?? throw new IOException("The live rehearsal path is not contained by the user temporary directory.");
        }
    }
}
