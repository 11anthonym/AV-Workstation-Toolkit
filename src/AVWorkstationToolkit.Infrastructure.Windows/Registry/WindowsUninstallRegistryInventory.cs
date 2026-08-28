using Microsoft.Win32;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Infrastructure.Windows.Processes;

namespace AVWorkstationToolkit.Infrastructure.Windows.Registry;

public sealed class WindowsUninstallRegistryInventory : IExternalApplicationInventory
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public Task<RegistryInventoryResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var states = new[]
        {
            ReadSource(RegistryInventorySource.Hklm64, RegistryHive.LocalMachine, RegistryView.Registry64, cancellationToken),
            ReadSource(RegistryInventorySource.Hklm32, RegistryHive.LocalMachine, RegistryView.Registry32, cancellationToken),
            ReadSource(RegistryInventorySource.Hkcu, RegistryHive.CurrentUser, RegistryView.Default, cancellationToken)
        };
        var records = states.SelectMany(state => state.Records).ToArray();
        var statuses = states.Select(state => state.Status).ToArray();
        var available = statuses.Count(status => status.Available);
        var quality = available == statuses.Length ? ProviderQuality.Complete : available == 0 ? ProviderQuality.Unavailable : ProviderQuality.Partial;
        var failure = quality switch
        {
            ProviderQuality.Complete => ProviderFailureKind.None,
            ProviderQuality.Partial => ProviderFailureKind.PartialInventory,
            _ => ProviderFailureKind.ProviderUnavailable
        };
        var detail = quality switch
        {
            ProviderQuality.Complete => "All uninstall-registry sources were read successfully.",
            ProviderQuality.Partial => $"{statuses.Length - available} of {statuses.Length} registry sources unavailable.",
            _ => "All registry sources are unavailable."
        };
        return Task.FromResult(new RegistryInventoryResult(quality, failure, records, statuses, detail));
    }

    private static SourceReadResult ReadSource(
        RegistryInventorySource source,
        RegistryHive hive,
        RegistryView view,
        CancellationToken cancellationToken)
    {
        var records = new List<RegistryUninstallRecord>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallPath, writable: false);
            if (uninstall is null)
                return new(new(source, true, 0, "Registry source contains no uninstall records."), records);
            foreach (var subkeyName in uninstall.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var subkey = uninstall.OpenSubKey(subkeyName, writable: false);
                var displayName = NormalizeValue(subkey?.GetValue("DisplayName"));
                if (displayName.Length == 0) continue;
                records.Add(new(source, displayName, NormalizeValue(subkey?.GetValue("DisplayVersion"))));
            }
            return new(new(source, true, records.Count, "Registry source read successfully."), records);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return new(new(source, false, 0, DiagnosticText.Sanitize(exception.Message)), []);
        }
    }

    private static string NormalizeValue(object? value)
    {
        var text = value?.ToString()?.Trim() ?? string.Empty;
        if (text.Length > 4096) text = text[..4096];
        return DiagnosticText.Sanitize(text);
    }

    private sealed record SourceReadResult(RegistrySourceStatus Status, IReadOnlyList<RegistryUninstallRecord> Records);
}
