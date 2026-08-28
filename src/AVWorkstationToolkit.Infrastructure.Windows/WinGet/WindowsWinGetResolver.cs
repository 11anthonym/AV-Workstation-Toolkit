using System.Xml;
using Microsoft.Win32;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Infrastructure.Windows.Authenticode;
using AVWorkstationToolkit.Infrastructure.Windows.Processes;

namespace AVWorkstationToolkit.Infrastructure.Windows.WinGet;

public sealed record WinGetCandidateFacts(
    string PackageName,
    string PackagePublisher,
    string Version,
    string InstallLocation,
    string ExecutablePath,
    bool FileExists,
    bool RegularFile,
    bool PathContained,
    bool ReparseFree,
    bool SignatureValid,
    string SignerSubject);

public static class WinGetCandidatePolicy
{
    public static TrustedWinGetResolution Evaluate(WinGetCandidateFacts candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var valid = candidate.PackageName.Equals("Microsoft.DesktopAppInstaller", StringComparison.Ordinal) &&
            HasMicrosoftOrganization(candidate.PackagePublisher) &&
            candidate.FileExists && candidate.RegularFile && candidate.PathContained && candidate.ReparseFree &&
            candidate.SignatureValid && HasMicrosoftOrganization(candidate.SignerSubject) &&
            Path.GetFileName(candidate.ExecutablePath).Equals("winget.exe", StringComparison.OrdinalIgnoreCase);
        return valid
            ? new(true, Path.GetFullPath(candidate.ExecutablePath), ProviderFailureKind.None, "Trusted Microsoft Desktop App Installer WinGet candidate resolved.")
            : new(false, string.Empty, ProviderFailureKind.TrustFailure, "WinGet candidate failed Desktop App Installer path, file, package, reparse, or publisher trust policy.");
    }

    private static bool HasMicrosoftOrganization(string value) =>
        value.Split(',', StringSplitOptions.TrimEntries).Any(part => part.Equals("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase));
}

public sealed class WindowsWinGetResolver : IWinGetResolver
{
    private readonly IAuthenticodeTrustVerifier signatureVerifier;

    public WindowsWinGetResolver(IAuthenticodeTrustVerifier? signatureVerifier = null) =>
        this.signatureVerifier = signatureVerifier ?? new WinTrustAuthenticodeVerifier();

    public Task<TrustedWinGetResolution> ResolveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var root = Path.GetFullPath(Path.Combine(programFiles, "WindowsApps"));
            if (!Directory.Exists(root)) return Task.FromResult(Unavailable("Protected WindowsApps directory is unavailable."));
            var candidates = DiscoverRegisteredPackageLocations()
                .Select(path => ReadCandidate(root, path))
                .Where(candidate => candidate is not null)
                .Cast<WinGetCandidateFacts>()
                .OrderByDescending(candidate => Version.TryParse(candidate.Version, out var version) ? version : new Version())
                .ToArray();
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = WinGetCandidatePolicy.Evaluate(candidate);
                if (result.Trusted) return Task.FromResult(result);
            }
            return Task.FromResult(Unavailable("A trusted Microsoft winget.exe could not be resolved from Desktop App Installer."));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
        {
            return Task.FromResult(Unavailable($"Trusted WinGet discovery failed: {exception.Message}"));
        }
    }

    private static IReadOnlyList<string> DiscoverRegisteredPackageLocations()
    {
        const string repositoryPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var packages = currentUser.OpenSubKey(repositoryPath, writable: false);
        if (packages is null) return [];
        var results = new List<string>();
        foreach (var packageName in packages.GetSubKeyNames().Where(name =>
                     name.StartsWith("Microsoft.DesktopAppInstaller_", StringComparison.Ordinal) &&
                     name.EndsWith("__8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase)))
        {
            using var package = packages.OpenSubKey(packageName, writable: false);
            if (package?.GetValue("PackageRootFolder") is string root && !string.IsNullOrWhiteSpace(root)) results.Add(root);
        }
        return results;
    }

    public static bool IsExpectedExecutablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !Path.GetFileName(path).Equals("winget.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var root = Path.GetFullPath(Path.Combine(programFiles, "WindowsApps")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        var relative = Path.GetRelativePath(root, fullPath);
        var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return firstSegment.StartsWith("Microsoft.DesktopAppInstaller_", StringComparison.Ordinal) &&
            firstSegment.EndsWith("__8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase);
    }

    private WinGetCandidateFacts? ReadCandidate(string root, string installLocation)
    {
        var fullLocation = Path.GetFullPath(installLocation);
        var executable = Path.GetFullPath(Path.Combine(fullLocation, "winget.exe"));
        var manifest = Path.Combine(fullLocation, "AppxManifest.xml");
        var contained = IsContained(root, executable);
        var reparseFree = contained && IsReparseFree(root, executable);
        if (!File.Exists(manifest)) return null;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 };
        using var reader = XmlReader.Create(manifest, settings);
        var document = new XmlDocument { XmlResolver = null };
        document.Load(reader);
        var identity = document.DocumentElement?.ChildNodes.Cast<XmlNode>().FirstOrDefault(node => node.LocalName == "Identity");
        if (identity?.Attributes is null) return null;
        var packageName = identity.Attributes["Name"]?.Value ?? string.Empty;
        var publisher = identity.Attributes["Publisher"]?.Value ?? string.Empty;
        var version = identity.Attributes["Version"]?.Value ?? string.Empty;
        var exists = File.Exists(executable);
        var regular = exists && (File.GetAttributes(executable) & (FileAttributes.Directory | FileAttributes.Device)) == 0;
        var signature = exists && regular && contained && reparseFree
            ? signatureVerifier.Verify(executable)
            : new AuthenticodeTrustResult(false, string.Empty, "Candidate file policy failed before signature verification.");
        return new(packageName, publisher, version, fullLocation, executable, exists, regular, contained, reparseFree, signature.Valid, signature.SignerSubject);
    }

    private static bool IsContained(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReparseFree(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var current = new FileInfo(path) as FileSystemInfo;
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            if (current.FullName.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)) return true;
            current = current switch { FileInfo file => file.Directory, DirectoryInfo directory => directory.Parent, _ => null };
        }
        return false;
    }

    private static TrustedWinGetResolution Unavailable(string detail) =>
        new(false, string.Empty, ProviderFailureKind.TrustFailure, DiagnosticText.Sanitize(detail));
}
