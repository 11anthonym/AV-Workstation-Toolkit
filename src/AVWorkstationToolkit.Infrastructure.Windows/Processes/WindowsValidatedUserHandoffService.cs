using System.Diagnostics;
using AVWorkstationToolkit.Application.Details;
using AVWorkstationToolkit.Application.Vendors;
using AVWorkstationToolkit.Infrastructure.Windows.Vendors;

namespace AVWorkstationToolkit.Infrastructure.Windows.Processes;

public sealed class WindowsValidatedUserHandoffService(
    VendorCachePathPolicy cachePaths,
    IVendorPayloadVerifier verifier) : IValidatedUserHandoffService
{
    public void OpenOfficialUri(OpenOfficialUriIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Uri.Scheme != Uri.UriSchemeHttps || intent.Uri.UserInfo.Length != 0 || intent.Uri.DnsSafeHost.Length == 0)
            throw new InvalidDataException("Only a validated official HTTPS URI can be opened.");
        Process.Start(new ProcessStartInfo
        {
            FileName = intent.Uri.AbsoluteUri,
            UseShellExecute = true
        })?.Dispose();
    }

    public void RevealVerifiedPayload(VendorDeliveryAuthorization authorization, VendorDownloadResult payload, string explicitDataRoot)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.State != VendorPayloadState.Verified)
            throw new InvalidOperationException("Only a verified cached payload can be revealed.");
        var verified = verifier.ResolveCached(authorization, explicitDataRoot,
            cachePaths.ValidateCachedPayload(explicitDataRoot, authorization, payload.Path));
        if (verified.State != VendorPayloadState.Verified)
            throw new InvalidDataException("The cached payload no longer satisfies its verification policy.");
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (!File.Exists(explorer) || (File.GetAttributes(explorer) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new FileNotFoundException("The trusted Windows Explorer executable is unavailable.", explorer);
        var start = new ProcessStartInfo { FileName = explorer, UseShellExecute = false };
        start.ArgumentList.Add("/select,");
        start.ArgumentList.Add(verified.Path);
        Process.Start(start)?.Dispose();
    }

    public void OpenLogs(string explicitDataRoot)
    {
        var logs = PrepareLogsDirectory(explicitDataRoot);
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (!File.Exists(explorer) || (File.GetAttributes(explorer) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new FileNotFoundException("The trusted Windows Explorer executable is unavailable.", explorer);
        var start = new ProcessStartInfo { FileName = explorer, UseShellExecute = false };
        start.ArgumentList.Add(logs);
        Process.Start(start)?.Dispose();
    }

    internal static string PrepareLogsDirectory(string explicitDataRoot)
    {
        if (string.IsNullOrWhiteSpace(explicitDataRoot) || !Path.IsPathFullyQualified(explicitDataRoot))
            throw new IOException("The application data root must be absolute.");
        var root = Path.GetFullPath(explicitDataRoot).TrimEnd(Path.DirectorySeparatorChar);
        var volume = Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(root, volume, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The application data root cannot be a filesystem root.");
        Directory.CreateDirectory(root);
        RejectDirectoryReparse(root);
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        RejectDirectoryReparse(logs);
        return logs;
    }

    private static void RejectDirectoryReparse(string path)
    {
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The application log path is unavailable or uses a reparse point.");
    }
}
