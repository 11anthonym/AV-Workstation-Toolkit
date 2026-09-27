namespace AVWorkstationToolkit.Application.Actions;

/// <summary>
/// Apps that are open and running a managed package's installed programs, which many installers refuse to replace
/// while they run (an Inno Setup installer with an app mutex exits with code 1 under a silent install). Implementations
/// identify apps by the program files they run, never by process name, and only ever ask an app to close, the way
/// Windows does at sign-out: nothing is forced, so an app with unsaved work can refuse.
/// </summary>
public interface IOpenApplicationService
{
    /// <summary>
    /// Read-only. The names of the apps open now that run this package's installed programs. Empty when none is open,
    /// and also when the package's programs can't be located, in which case its installer runs as it always has.
    /// </summary>
    IReadOnlyList<string> FindOpen(string packageId);

    /// <summary>
    /// Asks the apps running this package's installed programs to close. Call <see cref="IOpenApplicationClosure.Reopen"/>
    /// once the installer has finished, then dispose the closure.
    /// </summary>
    IOpenApplicationClosure Close(string packageId);
}

public interface IOpenApplicationClosure : IDisposable
{
    /// <summary>The apps that closed when asked.</summary>
    IReadOnlyList<string> Closed { get; }

    /// <summary>The apps still open afterward, because they refused or couldn't be asked.</summary>
    IReadOnlyList<string> StillOpen { get; }

    /// <summary>Restarts the closed apps that registered with Windows to be restarted, and returns their names.</summary>
    IReadOnlyList<string> Reopen();
}

/// <summary>Finds no open apps, for compositions without Windows Restart Manager such as preview builds and tests.</summary>
public sealed class NoOpenApplications : IOpenApplicationService
{
    public static NoOpenApplications Instance { get; } = new();

    public IReadOnlyList<string> FindOpen(string packageId) => [];

    public IOpenApplicationClosure Close(string packageId) => new Nothing();

    private sealed class Nothing : IOpenApplicationClosure
    {
        public IReadOnlyList<string> Closed => [];
        public IReadOnlyList<string> StillOpen => [];
        public IReadOnlyList<string> Reopen() => [];
        public void Dispose() { }
    }
}

/// <summary>Plain-language lists of app names for progress messages and questions.</summary>
public static class OpenApplicationText
{
    public static string Names(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var distinct = names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return distinct.Length switch
        {
            0 => "No app",
            1 => distinct[0],
            2 => $"{distinct[0]} and {distinct[1]}",
            _ => $"{string.Join(", ", distinct[..^1])}, and {distinct[^1]}"
        };
    }

    public static bool IsPlural(IReadOnlyList<string> names) =>
        names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1;
}
