using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Workstation;
using Microsoft.Win32;

namespace AVWorkstationToolkit.Infrastructure.Windows.Processes;

/// <summary>Where a managed package's installed programs are, so the apps running them can be found.</summary>
public interface IInstalledProgramLocator
{
    /// <summary>Fully qualified paths of the package's installed program files; empty when they can't be located.</summary>
    IReadOnlyList<string> Locate(string packageId);
}

/// <summary>
/// Finds and closes open apps the way installers do, through Windows Restart Manager. An app is identified by the
/// program files it runs, located from its own uninstall registration, never by process name. It is only asked to close
/// (no forced shutdown), services, Windows Explorer, and critical system processes are never touched, and only the
/// technician's own session is affected. Apps that registered with Windows for restart are restarted afterward.
/// </summary>
public sealed class RestartManagerOpenApplications : IOpenApplicationService
{
    private readonly IInstalledProgramLocator locator;

    public RestartManagerOpenApplications(IInstalledProgramLocator? locator = null) =>
        this.locator = locator ?? new RegistryInstalledProgramLocator();

    public IReadOnlyList<string> FindOpen(string packageId)
    {
        var programs = locator.Locate(packageId);
        return programs.Count == 0 ? [] : RestartManager.AppsUsing(programs).Select(app => app.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IOpenApplicationClosure Close(string packageId)
    {
        var programs = locator.Locate(packageId);
        if (programs.Count == 0) return new Closure(null, [], [], []);
        var open = RestartManager.AppsUsing(programs);
        if (open.Count == 0) return new Closure(null, [], [], []);

        RestartManager.Session? session = null;
        try
        {
            session = RestartManager.Session.Start();
            session.RegisterProcesses(open.Select(app => app.Process).ToArray());
            // No RmForceShutdown: an app that doesn't close when asked, for example over unsaved work, stays open.
            session.ShutDown();
        }
        catch (Win32Exception)
        {
            // Nothing more can be asked; whatever is still running is reported below as still open.
        }
        var remaining = RestartManager.AppsUsing(programs);
        var closed = open.Where(app => !remaining.Any(other => other.Process.Equals(app.Process))).ToArray();
        return new Closure(session,
            closed.Select(app => app.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            closed.Where(app => app.Restartable).Select(app => app.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            remaining.Select(app => app.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private sealed class Closure(RestartManager.Session? session, IReadOnlyList<string> closed, IReadOnlyList<string> restartable, IReadOnlyList<string> stillOpen)
        : IOpenApplicationClosure
    {
        private bool reopened;

        public IReadOnlyList<string> Closed { get; } = closed;
        public IReadOnlyList<string> StillOpen { get; } = stillOpen;

        public IReadOnlyList<string> Reopen()
        {
            if (session is null || reopened || restartable.Count == 0) return [];
            reopened = true;
            try
            {
                session.Restart();
                return restartable;
            }
            catch (Win32Exception)
            {
                return [];
            }
        }

        public void Dispose() => session?.Dispose();
    }
}

/// <summary>
/// Locates a managed package's programs from the Windows uninstall registration its detector recognizes: the
/// executables directly inside InstallLocation and the DisplayIcon executable. A location that is a drive, a Windows
/// folder, or a shared root such as Program Files is ignored, so a malformed registration can't sweep in unrelated apps.
/// </summary>
public sealed class RegistryInstalledProgramLocator : IInstalledProgramLocator
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    internal const int MaximumPrograms = 64;
    private readonly IReadOnlyDictionary<string, string> detectors;

    public RegistryInstalledProgramLocator(IReadOnlyDictionary<string, string>? detectors = null) =>
        this.detectors = detectors ?? ManagedApplicationDetectors.Patterns;

    public IReadOnlyList<string> Locate(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId) || !detectors.TryGetValue(packageId, out var pattern)) return [];
        var programs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default)
                 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(UninstallPath, writable: false);
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(name, writable: false);
                    var displayName = entry?.GetValue("DisplayName")?.ToString()?.Trim() ?? string.Empty;
                    if (displayName.Length == 0 || !CatalogRegistryDetection.MatchesDisplayName(pattern, displayName)) continue;
                    foreach (var program in ProgramsFrom(entry?.GetValue("InstallLocation")?.ToString(), entry?.GetValue("DisplayIcon")?.ToString()))
                    {
                        if (programs.Count >= MaximumPrograms) break;
                        programs.Add(program);
                    }
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                // A registry view that can't be read contributes nothing; the others still count.
            }
        }
        return programs.ToArray();
    }

    internal static IEnumerable<string> ProgramsFrom(string? installLocation, string? displayIcon)
    {
        if (ProgramFolder(installLocation) is { } folder)
        {
            IEnumerable<string> executables;
            try
            {
                executables = Directory.EnumerateFiles(folder, "*.exe", new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
                }).Take(MaximumPrograms).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                executables = [];
            }
            foreach (var executable in executables) yield return executable;
        }
        if (IconProgram(displayIcon) is { } icon) yield return icon;
    }

    /// <summary>The program folder an InstallLocation names, or null when it isn't a specific, existing, local folder.</summary>
    internal static string? ProgramFolder(string? installLocation)
    {
        var path = Unquote(installLocation);
        if (path.Length == 0 || !IsLocalFullPath(path)) return null;
        try
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!Directory.Exists(path) || new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
        return IsSharedFolder(path) ? null : path;
    }

    /// <summary>The executable a DisplayIcon value names ("C:\App\App.exe,0"), or null when it isn't an existing local .exe.</summary>
    internal static string? IconProgram(string? displayIcon)
    {
        var value = displayIcon?.Trim() ?? string.Empty;
        var comma = value.LastIndexOf(',');
        if (comma > 0 && int.TryParse(value[(comma + 1)..].Trim(), out _)) value = value[..comma];
        var path = Unquote(value);
        if (path.Length == 0 || !IsLocalFullPath(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            path = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(path);
            if (directory is null || IsSharedFolder(Path.TrimEndingDirectorySeparator(directory)) || !File.Exists(path) ||
                File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
        return path;
    }

    private static string Unquote(string? value) => (value ?? string.Empty).Trim().Trim('"').Trim();

    // A drive-rooted local path; UNC, device, and relative paths are never followed.
    private static bool IsLocalFullPath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/' &&
        Path.IsPathFullyQualified(path) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0;

    internal static bool IsSharedFolder(string folder)
    {
        var path = Path.TrimEndingDirectorySeparator(folder);
        if (Path.GetPathRoot(path) is { } root && Path.TrimEndingDirectorySeparator(root).Equals(path, StringComparison.OrdinalIgnoreCase)) return true;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0 && (path.Equals(windows, StringComparison.OrdinalIgnoreCase) ||
                                   path.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return true;
        var shared = new[]
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.CommonProgramFiles,
            Environment.SpecialFolder.CommonProgramFilesX86
        }.Select(Environment.GetFolderPath).Where(item => item.Length > 0).Select(Path.TrimEndingDirectorySeparator).ToList();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (localAppData.Length > 0) shared.Add(Path.Combine(localAppData, "Programs"));
        return shared.Any(item => item.Equals(path, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>The Windows Restart Manager calls installers use to find, close, and restart apps that hold files open.</summary>
internal static class RestartManager
{
    private const int ErrorMoreData = 234;
    private const int MaximumApps = 256;

    internal sealed record OpenApp(UniqueProcess Process, string Name, bool Restartable, string Kind);

    internal readonly record struct UniqueProcess(int ProcessId, long StartTime);

    /// <summary>The apps in this session running or holding the given files, excluding services, Explorer, critical processes, and this process.</summary>
    public static IReadOnlyList<OpenApp> AppsUsing(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return [];
        try
        {
            using var session = Session.Start();
            session.RegisterFiles(files);
            return session.List();
        }
        catch (Win32Exception)
        {
            // Restart Manager couldn't answer, so nothing is known to be open; the installer runs as it always has.
            return [];
        }
    }

    internal sealed class Session : IDisposable
    {
        private uint handle;

        private Session(uint handle) => this.handle = handle;

        public static Session Start()
        {
            var key = new StringBuilder(CchRmSessionKey + 1);
            var result = RmStartSession(out var handle, 0, key);
            if (result != 0) throw new Win32Exception(result);
            return new Session(handle);
        }

        public void RegisterFiles(IReadOnlyList<string> files) =>
            Check(RmRegisterResources(handle, (uint)files.Count, files.ToArray(), 0, null, 0, null));

        public void RegisterProcesses(IReadOnlyList<UniqueProcess> processes)
        {
            var native = processes.Select(unique => new RM_UNIQUE_PROCESS
            {
                dwProcessId = unique.ProcessId,
                ProcessStartTime = new System.Runtime.InteropServices.ComTypes.FILETIME
                {
                    dwLowDateTime = unchecked((int)(unique.StartTime & 0xFFFFFFFF)),
                    dwHighDateTime = unchecked((int)(unique.StartTime >> 32))
                }
            }).ToArray();
            Check(RmRegisterResources(handle, 0, null, (uint)native.Length, native, 0, null));
        }

        public void ShutDown() => Check(RmShutdown(handle, 0, IntPtr.Zero));

        public void Restart() => Check(RmRestart(handle, 0, IntPtr.Zero));

        public IReadOnlyList<OpenApp> List()
        {
            var session = (uint)Process.GetCurrentProcess().SessionId;
            var self = Environment.ProcessId;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                uint count = 0;
                uint reasons = 0;
                var result = RmGetList(handle, out var needed, ref count, null, ref reasons);
                if (result == 0) return [];
                if (result != ErrorMoreData) throw new Win32Exception(result);
                count = Math.Min(needed, MaximumApps);
                var infos = new RM_PROCESS_INFO[count];
                result = RmGetList(handle, out needed, ref count, infos, ref reasons);
                if (result == ErrorMoreData && needed <= MaximumApps) continue;
                if (result != 0 && result != ErrorMoreData) throw new Win32Exception(result);
                return infos.Take((int)Math.Min(count, (uint)infos.Length))
                    .Where(info => info.ApplicationType is RM_APP_TYPE.RmMainWindow or RM_APP_TYPE.RmOtherWindow or RM_APP_TYPE.RmConsole or RM_APP_TYPE.RmUnknownApp)
                    .Where(info => info.TSSessionId == session && info.Process.dwProcessId != self)
                    .Select(info => new OpenApp(
                        new UniqueProcess(info.Process.dwProcessId,
                            ((long)info.Process.ProcessStartTime.dwHighDateTime << 32) | (uint)info.Process.ProcessStartTime.dwLowDateTime),
                        AppName(info.strAppName),
                        info.bRestartable,
                        info.ApplicationType.ToString()))
                    .ToArray();
            }
            throw new Win32Exception(ErrorMoreData);
        }

        public void Dispose()
        {
            if (handle == 0) return;
            _ = RmEndSession(handle);
            handle = 0;
        }

        private static void Check(int result)
        {
            if (result != 0) throw new Win32Exception(result);
        }
    }

    private static string AppName(string? value)
    {
        var name = DiagnosticText.Sanitize(value).Trim();
        if (name.Length > 128) name = name[..128].TrimEnd();
        return name.Length == 0 ? "An app" : name;
    }

    private const int CchRmSessionKey = 32;
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;

    private enum RM_APP_TYPE
    {
        RmUnknownApp = 0,
        RmMainWindow = 1,
        RmOtherWindow = 2,
        RmService = 3,
        RmExplorer = 4,
        RmConsole = 5,
        RmCritical = 1000
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)] public string strServiceShortName;
        public RM_APP_TYPE ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[]? rgsFilenames,
        uint nApplications, [In] RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmShutdown(uint pSessionHandle, uint lActionFlags, IntPtr fnStatus);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmRestart(uint pSessionHandle, int dwRestartFlags, IntPtr fnStatus);
}
