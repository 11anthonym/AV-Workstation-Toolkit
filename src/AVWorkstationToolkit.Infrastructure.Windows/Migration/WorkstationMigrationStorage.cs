using System.Runtime.InteropServices;
using Microsoft.Win32;
using AVWorkstationToolkit.Application.Workstation;
using AVWorkstationToolkit.Domain.Workstation;

namespace AVWorkstationToolkit.Infrastructure.Windows.Migration;

/// <summary>Bounded, reparse-rejecting reads and atomic writes for workstation inventories, profiles, and saved migrations.</summary>
public static class WorkstationDocumentFiles
{
    public static byte[] ReadBounded(string path, int maximumBytes)
    {
        var fullPath = RequireAbsolute(path);
        var file = new FileInfo(fullPath);
        if (!file.Exists) throw new FileNotFoundException("The selected file doesn't exist.", fullPath);
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The selected file is a link or reparse point, so it wasn't opened.");
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
            throw new WorkstationDocumentException($"The file is larger than the {maximumBytes / (1024 * 1024)} MiB limit for this document.");
        var buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>Writes through a same-directory temporary file and replaces the target only after the bytes are durable.</summary>
    public static void WriteAtomic(string path, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var fullPath = RequireAbsolute(path);
        var parent = Path.GetDirectoryName(fullPath) ?? throw new IOException("The destination folder is unavailable.");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The destination folder is unavailable.");
        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("The destination folder is a link or reparse point.");
        if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The destination file is a link or reparse point, so it wasn't replaced.");
        var temporary = Path.Combine(parent, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Creates (if needed) a direct child folder of the data root, rejecting links at either level.</summary>
    public static string DataFolder(string dataRoot, string name)
    {
        var root = RequireDataRoot(dataRoot);
        Directory.CreateDirectory(root);
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        foreach (var path in new[] { root, folder })
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The application data folder uses a link or reparse point.");
        return folder;
    }

    public static string RequireDataRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new IOException("The application data root must be absolute.");
        var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
        var volume = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(full, volume, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The application data root cannot be a filesystem root.");
        return full;
    }

    private static string RequireAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new IOException("A full file path is required.");
        return Path.GetFullPath(path);
    }
}

/// <summary>Keeps the one active migration checklist at migration\session.json beneath the per-user data root.</summary>
public sealed class MigrationSessionFileStore(string dataRoot) : IMigrationSessionStore
{
    private readonly string dataRoot = WorkstationDocumentFiles.RequireDataRoot(dataRoot);

    public string SessionPath => Path.Combine(dataRoot, "migration", "session.json");

    public MigrationSession? Load()
    {
        if (!File.Exists(SessionPath)) return null;
        WorkstationDocumentFiles.DataFolder(dataRoot, "migration");
        return MigrationSessionCodec.Parse(WorkstationDocumentFiles.ReadBounded(SessionPath, MigrationSessionCodec.MaximumBytes));
    }

    public void Save(MigrationSession session)
    {
        var content = MigrationSessionCodec.Serialize(session);
        WorkstationDocumentFiles.DataFolder(dataRoot, "migration");
        WorkstationDocumentFiles.WriteAtomic(SessionPath, content);
    }

    public void Delete()
    {
        if (!File.Exists(SessionPath)) return;
        if ((File.GetAttributes(SessionPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The saved migration is a link or reparse point, so it wasn't deleted.");
        File.Delete(SessionPath);
    }
}

/// <summary>Reads the computer name, Windows edition, version, and build for an inventory header. It reads no user data.</summary>
public sealed class WindowsWorkstationMachineInfoProvider : IWorkstationMachineInfoProvider
{
    public WorkstationMachine Read()
    {
        var edition = string.Empty;
        var version = string.Empty;
        var build = string.Empty;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);
            edition = key?.GetValue("ProductName") as string ?? string.Empty;
            version = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string ?? string.Empty;
            var currentBuild = key?.GetValue("CurrentBuild") as string ?? string.Empty;
            build = key?.GetValue("UBR") is int revision ? $"{currentBuild}.{revision}" : currentBuild;
            // Windows 11 still reports a "Windows 10" product name; the build number distinguishes them.
            if (int.TryParse(currentBuild, out var number) && number >= 22000 && edition.StartsWith("Windows 10", StringComparison.Ordinal))
                edition = "Windows 11" + edition["Windows 10".Length..];
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // The header is descriptive; the inventory remains valid without it.
        }
        return new WorkstationMachine(
            ApplicationNames.Clean(Environment.MachineName),
            ApplicationNames.Clean(edition),
            ApplicationNames.Clean(version),
            ApplicationNames.Clean(build),
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
    }
}
