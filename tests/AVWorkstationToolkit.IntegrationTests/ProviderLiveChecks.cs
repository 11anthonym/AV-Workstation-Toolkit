using AVWorkstationToolkit.Infrastructure.Windows.Processes;
using AVWorkstationToolkit.Infrastructure.Windows.Reboot;
using AVWorkstationToolkit.Infrastructure.Windows.Registry;
using AVWorkstationToolkit.Infrastructure.Windows.WinGet;
using AVWorkstationToolkit.Application.Inventory;

namespace AVWorkstationToolkit.IntegrationTests;

/// <summary>
/// Exercises the production read-only Windows providers against the current host and reports what
/// each one could actually observe. It never installs, updates or otherwise mutates state, and it
/// never treats the host's software list as expected data.
/// </summary>
public static class ProviderLiveChecks
{
    public static async Task<object> RunAsync()
    {
        var resolver = new WindowsWinGetResolver();
        var resolution = await resolver.ResolveAsync().ConfigureAwait(false);
        var runner = new WinGetReadOnlyProcessRunner(resolver, TimeSpan.FromSeconds(30));
        var installed = await new WinGetInstalledPackageInventory(runner, []).ReadAsync().ConfigureAwait(false);
        var updates = await new WinGetAvailableUpdateInventory(runner).ReadAsync().ConfigureAwait(false);
        var registry = await new WindowsUninstallRegistryInventory().ReadAsync().ConfigureAwait(false);
        var reboot = await new WindowsRebootStateProvider().ReadAsync().ConfigureAwait(false);
        return new
        {
            WinGetResolution = resolution.Trusted ? "Available" : "Unavailable",
            WinGetResolutionDetail = resolution.Detail,
            InstalledInventory = installed.Quality.ToString(),
            InstalledFailure = installed.Failure.ToString(),
            InstalledCount = installed.Packages.Count,
            UpdateInventory = updates.Quality.ToString(),
            UpdateFailure = updates.Failure.ToString(),
            UpdateCount = updates.Updates.Count,
            RegistryInventory = registry.Quality.ToString(),
            RegistrySources = registry.Sources.Select(source => new { Source = source.Source.ToString(), source.Available, source.EntryCount }),
            RebootDetection = reboot.Quality.ToString(),
            reboot.Pending,
            Reasons = reboot.Reasons.Select(reason => reason.ToString())
        };
    }
}
