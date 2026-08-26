namespace AVWorkstationToolkit.Launcher;

internal static class SafePath
{
    public static string RequireAbsoluteNonRoot(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1_024 || value != value.Trim() || value.Any(char.IsControl))
        {
            throw new InvalidDataException($"The {label} is invalid.");
        }
        if (!Path.IsPathRooted(value))
        {
            throw new InvalidDataException($"The {label} must be an absolute path.");
        }

        var fullPath = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var volumeRoot = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(fullPath) || fullPath.Equals(volumeRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The {label} cannot be a filesystem or volume root.");
        }
        return fullPath;
    }

    public static bool IsStrictChild(string parentPath, string candidatePath)
    {
        var parent = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(candidatePath);
        return candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ContainsReparsePoint(string rootPath, string candidatePath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = Path.GetFullPath(candidatePath);
        if (!current.Equals(root, StringComparison.OrdinalIgnoreCase) && !IsStrictChild(root, current))
        {
            return true;
        }

        while (!string.IsNullOrWhiteSpace(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
            if (current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }
        return true;
    }
}
