using Microsoft.Win32;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Infrastructure.Windows.Processes;

namespace AVWorkstationToolkit.Infrastructure.Windows.Registry;

public sealed class WindowsUninstallRegistryInventory : IExternalApplicationInventory
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string MachineUpgradeCodesPath = @"SOFTWARE\Classes\Installer\UpgradeCodes";
    private const string UserUpgradeCodesPath = @"Software\Microsoft\Installer\UpgradeCodes";

    public Task<RegistryInventoryResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Windows Installer upgrade codes only enrich an MSI registration's identity. They are read from the registry,
        // never through Windows Installer APIs or WMI that can start repair or reconfiguration, and a failure to read
        // them leaves the uninstall inventory unchanged.
        var upgradeCodes = ReadUpgradeCodes(cancellationToken);
        var states = new[]
        {
            ReadSource(RegistryInventorySource.Hklm64, RegistryHive.LocalMachine, RegistryView.Registry64, upgradeCodes, cancellationToken),
            ReadSource(RegistryInventorySource.Hklm32, RegistryHive.LocalMachine, RegistryView.Registry32, upgradeCodes, cancellationToken),
            ReadSource(RegistryInventorySource.Hkcu, RegistryHive.CurrentUser, RegistryView.Default, upgradeCodes, cancellationToken)
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
        IReadOnlyDictionary<string, string> upgradeCodes,
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
                var windowsInstaller = IsSet(subkey?.GetValue("WindowsInstaller"));
                var keyName = NormalizeValue(subkeyName, 256);
                upgradeCodes.TryGetValue(keyName, out var upgradeCode);
                records.Add(new(
                    source,
                    displayName,
                    NormalizeValue(subkey?.GetValue("DisplayVersion")),
                    NormalizeValue(subkey?.GetValue("Publisher"), 512),
                    keyName,
                    IsSet(subkey?.GetValue("SystemComponent")),
                    windowsInstaller,
                    NormalizeValue(subkey?.GetValue("ParentKeyName"), 256),
                    NormalizeValue(subkey?.GetValue("ReleaseType"), 64),
                    windowsInstaller ? upgradeCode ?? string.Empty : string.Empty));
            }
            return new(new(source, true, records.Count, "Registry source read successfully."), records);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return new(new(source, false, 0, DiagnosticText.Sanitize(exception.Message)), []);
        }
    }

    private static Dictionary<string, string> ReadUpgradeCodes(CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view, path) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64, MachineUpgradeCodesPath),
                     (RegistryHive.CurrentUser, RegistryView.Default, UserUpgradeCodesPath)
                 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var codes = baseKey.OpenSubKey(path, writable: false);
                if (codes is null) continue;
                foreach (var packedUpgrade in codes.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!MsiPackedGuid.TryUnpack(packedUpgrade, out var upgradeCode)) continue;
                    using var products = codes.OpenSubKey(packedUpgrade, writable: false);
                    foreach (var packedProduct in products?.GetValueNames() ?? [])
                        if (MsiPackedGuid.TryUnpack(packedProduct, out var productCode))
                            map.TryAdd(productCode, upgradeCode);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                // Upgrade codes are optional enrichment; the uninstall inventory stays authoritative without them.
            }
        }
        return map;
    }

    private static bool IsSet(object? value) => value switch
    {
        int number => number == 1,
        long number => number == 1,
        string text => text.Trim() == "1",
        _ => false
    };

    private static string NormalizeValue(object? value, int maximumLength = 4096)
    {
        var text = value?.ToString()?.Trim() ?? string.Empty;
        if (text.Length > maximumLength) text = text[..maximumLength];
        return DiagnosticText.Sanitize(text);
    }

    private sealed record SourceReadResult(RegistrySourceStatus Status, IReadOnlyList<RegistryUninstallRecord> Records);
}

/// <summary>Converts the packed GUID form Windows Installer uses for registry key and value names.</summary>
internal static class MsiPackedGuid
{
    // Packed form: the first three GUID groups reversed character by character, then each remaining byte's two
    // hexadecimal digits swapped.
    public static bool TryUnpack(string packed, out string guid)
    {
        guid = string.Empty;
        if (packed is null || packed.Length != 32 || !packed.All(Uri.IsHexDigit)) return false;
        var text = packed.ToUpperInvariant();
        static string Reverse(string value) => new(value.Reverse().ToArray());
        var tail = string.Concat(Enumerable.Range(0, 8).Select(index => Reverse(text.Substring(16 + index * 2, 2))));
        guid = $"{{{Reverse(text[..8])}-{Reverse(text[8..12])}-{Reverse(text[12..16])}-{tail[..4]}-{tail[4..]}}}";
        return true;
    }

    public static string Pack(string guid)
    {
        var text = guid.Trim('{', '}').Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (text.Length != 32 || !text.All(Uri.IsHexDigit)) throw new FormatException("A braced GUID is required.");
        static string Reverse(string value) => new(value.Reverse().ToArray());
        return Reverse(text[..8]) + Reverse(text[8..12]) + Reverse(text[12..16]) +
            string.Concat(Enumerable.Range(0, 8).Select(index => Reverse(text.Substring(16 + index * 2, 2))));
    }
}
