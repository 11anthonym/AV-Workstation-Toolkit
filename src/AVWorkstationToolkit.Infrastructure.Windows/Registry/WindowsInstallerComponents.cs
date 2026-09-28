using Microsoft.Win32;

namespace AVWorkstationToolkit.Infrastructure.Windows.Registry;

/// <summary>
/// The key paths Windows Installer records for each installed product's components, read as registry data only from
/// HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData. Windows Installer's own APIs and WMI product queries are
/// never used, because they can start a repair. One index serves a whole run: it is rebuilt at most every two minutes.
/// </summary>
internal static class WindowsInstallerComponents
{
    private const string UserDataPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData";
    private const int MaximumComponents = 200_000;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private static readonly object Gate = new();
    private static Dictionary<string, List<string>>? index;
    private static DateTimeOffset builtAt;

    /// <summary>The recorded key paths of the given products' components (braced product-code GUIDs).</summary>
    public static IReadOnlyList<string> KeyPaths(IEnumerable<string> productCodes)
    {
        var current = Current();
        var paths = new List<string>();
        foreach (var productCode in productCodes)
        {
            string packed;
            try { packed = MsiPackedGuid.Pack(productCode); }
            catch (FormatException) { continue; }
            if (current.TryGetValue(packed, out var found)) paths.AddRange(found);
        }
        return paths;
    }

    private static Dictionary<string, List<string>> Current()
    {
        lock (Gate)
        {
            if (index is not null && DateTimeOffset.UtcNow - builtAt < Lifetime) return index;
            index = Build();
            builtAt = DateTimeOffset.UtcNow;
            return index;
        }
    }

    private static Dictionary<string, List<string>> Build()
    {
        var built = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var read = 0;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var userData = baseKey.OpenSubKey(UserDataPath, writable: false);
            if (userData is null) return built;
            foreach (var sid in userData.GetSubKeyNames())
            {
                using var components = OpenOrNull(userData, sid + @"\Components");
                if (components is null) continue;
                foreach (var component in components.GetSubKeyNames())
                {
                    if (++read > MaximumComponents) return built;
                    using var key = OpenOrNull(components, component);
                    if (key is null) continue;
                    foreach (var product in key.GetValueNames())
                    {
                        if (product.Length != 32 || key.GetValue(product) is not string keyPath || keyPath.Length == 0) continue;
                        if (!built.TryGetValue(product, out var list)) built.Add(product, list = []);
                        list.Add(keyPath);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Without this data a Windows Installer app just isn't located; its installer runs as it always has.
        }
        return built;
    }

    private static RegistryKey? OpenOrNull(RegistryKey parent, string name)
    {
        try { return parent.OpenSubKey(name, writable: false); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
