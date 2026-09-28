namespace AVWorkstationToolkit.Application.Workers;

/// <summary>What the worker does about a WinGet result it can recognize.</summary>
public enum WinGetRecovery
{
    /// <summary>Nothing automatic: report the failure with its plain reason.</summary>
    None,
    /// <summary>A condition that usually clears by itself; the same exact request is tried again after a pause.</summary>
    RetryLater,
    /// <summary>The app is open; with the technician's consent it is asked to close and the package is tried again once.</summary>
    CloseOpenApps,
    /// <summary>WinGet found nothing to do; a fresh check decides whether the package is already current.</summary>
    CheckIfCurrent,
    /// <summary>The installer changed the machine but Windows must restart before the installation is complete.</summary>
    RestartRequired
}

/// <summary>A recognized WinGet or installer result: a plain reason, what to do next, and any automatic recovery.</summary>
public sealed record WinGetOutcome(string Reason, WinGetRecovery Recovery);

/// <summary>
/// Maps the exit codes WinGet documents in AppInstallerErrors.h (including the 0x8A1501xx installer-result categories it
/// derives from MSI, Inno Setup, and MSIX codes) to plain reasons and bounded recoveries. Unknown codes return null and are
/// reported with WinGet's own output as before. Nothing here grants authority: a retry is the same reviewed one-package
/// vector, preceded by the same fresh recheck as every package.
/// </summary>
public static class WinGetOutcomes
{
    private static readonly Dictionary<uint, WinGetOutcome> Known = new()
    {
        [0x8A150008] = new("WinGet couldn't download the installer", WinGetRecovery.RetryLater),
        [0x8A150107] = new("the installer couldn't reach the network", WinGetRecovery.RetryLater),
        [0x8A150045] = new("WinGet couldn't open its package source", WinGetRecovery.RetryLater),
        [0x8A150102] = new("another installation was already running on this PC, often Windows Update", WinGetRecovery.RetryLater),

        [0x8A150101] = new("the app was open, and its installer can't replace it while it runs", WinGetRecovery.CloseOpenApps),
        [0x8A150103] = new("a file the installer needed to replace was in use", WinGetRecovery.CloseOpenApps),
        [0x8A150111] = new("another app was using this package", WinGetRecovery.CloseOpenApps),

        [0x8A15002B] = new("WinGet found no newer version to install", WinGetRecovery.CheckIfCurrent),
        [0x8A150061] = new("the package is already installed", WinGetRecovery.CheckIfCurrent),
        [0x8A15010D] = new("the installer reported the app is already installed", WinGetRecovery.CheckIfCurrent),

        [0x8A150011] = new("the download didn't match the hash published for it, so it wasn't run. The publisher probably replaced the file; " +
                           "the package source is usually corrected within a few days", WinGetRecovery.None),
        [0x8A150010] = new("WinGet has no installer for this PC's architecture, scope, or Windows version", WinGetRecovery.None),
        [0x8A15003A] = new("WinGet is blocked by policy on this PC", WinGetRecovery.None),
        [0x8A15010F] = new("the installer was blocked by policy on this PC", WinGetRecovery.None),
        [0x8A150105] = new("the disk is full", WinGetRecovery.None),
        [0x8A150106] = new("there wasn't enough memory", WinGetRecovery.None),
        [0x8A150104] = new("a component the installer needs is missing", WinGetRecovery.None),
        [0x8A150110] = new("a dependency couldn't be installed", WinGetRecovery.None),
        [0x8A15010A] = new("Windows has to restart before this installer can run. Restart, then try again", WinGetRecovery.None),
        [0x8A150109] = new("Windows must restart to finish the installation", WinGetRecovery.RestartRequired),
        [0x8A15010B] = new("the installer started a restart", WinGetRecovery.None),
        [0x8A15010C] = new("the installation was cancelled at the installer or its Windows permission prompt", WinGetRecovery.None),
        [0x8A15010E] = new("a newer version is already installed", WinGetRecovery.None),
        [0x8A150113] = new("the installer doesn't support this PC", WinGetRecovery.None),
        [0x8A150114] = new("this installer can't upgrade the installed version; it has to be removed and installed again by hand", WinGetRecovery.None),
        [0x8A150108] = new("the installer asked to contact the publisher's support", WinGetRecovery.None),
        [0x8A150112] = new("the installer rejected its parameters", WinGetRecovery.None),
        [0x8A150056] = new("the installer refuses to run with administrator rights", WinGetRecovery.None),
        [0x8A150030] = new("WinGet couldn't run the installed version's uninstall command", WinGetRecovery.None),
        [0x8A150050] = new("WinGet can't tell which version is installed", WinGetRecovery.None),
        [0x8A15004F] = new("the available version isn't newer than the installed one", WinGetRecovery.None),
        [0x8A150041] = new("the package agreements weren't accepted", WinGetRecovery.None),
        [0x8A150046] = new("the package source agreements weren't accepted", WinGetRecovery.None)
    };

    /// <summary>The recognized outcome for a WinGet exit code, or null when WinGet reported something else.</summary>
    public static WinGetOutcome? For(int exitCode) => Known.TryGetValue(unchecked((uint)exitCode), out var outcome) ? outcome : null;

    // WinGet's en-US resource InstallFlowReturnCodeRebootRequiredToFinish (winget.resw).
    private const string RestartToFinishNotice = "Restart your PC to finish installation.";

    /// <summary>
    /// Whether WinGet said the installation needs a restart to finish. The WinGet command line treats an installer's
    /// "restart required to finish" result (MSI 3010, or a manifest's rebootRequiredToFinish code) as success, exits 0, and
    /// only prints this notice; 0x8A150109 is not the command's exit code. The notice is printed in WinGet's display
    /// language, so only the English notice is recognized; in another language the install is reported as verified.
    /// </summary>
    public static bool ReportsRestartToFinish(PackageExecutionResult execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return execution.StandardOutput.Contains(RestartToFinishNotice, StringComparison.OrdinalIgnoreCase) ||
               execution.StandardError.Contains(RestartToFinishNotice, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>How many times, and after what pauses, a condition that usually clears by itself is tried again.</summary>
public sealed record WorkerRetryPolicy(IReadOnlyList<TimeSpan> Pauses)
{
    /// <summary>Two more tries, after 20 seconds and then a minute: long enough for a download or another installer to clear.</summary>
    public static WorkerRetryPolicy Default { get; } = new([TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(1)]);
}
