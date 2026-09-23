namespace AVWorkstationToolkit.Domain.Catalog;

public static class CatalogTokens
{
    public static T Parse<T>(string value, string field) where T : struct, Enum
    {
        var normalized = typeof(T) switch
        {
            var type when type == typeof(PackagePriority) => value switch { "UTILITY" => "Utility", "DEV" => "Dev", _ => value },
            var type when type == typeof(LicensingModel) => value switch
            {
                "FREE" => "Free",
                "FREEMIUM" => "Freemium",
                "PAID" => "Paid",
                "LICENSE" => "License",
                "SUBSCRIPTION" => "Subscription",
                "HARDWARE-LICENSE" => "HardwareLicense",
                "DEALER-LICENSE" => "DealerLicense",
                "UNKNOWN-COST" => "UnknownCost",
                _ => value
            },
            var type when type == typeof(InstallationForm) => value switch
            {
                "MSI" => "Msi",
                "EXE" => "Exe",
                "ZIP" => "Zip",
                _ => value
            },
            var type when type == typeof(SupportedOperatingSystem) => value switch
            {
                "macOS" => "MacOS",
                "iOS" => "IOS",
                _ => value
            },
            _ => value
        };
        if (!Enum.TryParse<T>(normalized, false, out var parsed) || !Enum.IsDefined(parsed))
        {
            throw new CatalogValidationException($"{field} contains unsupported value '{value}'.");
        }
        return parsed;
    }

    public static string ToToken<T>(this T value) where T : struct, Enum => value switch
    {
        PackagePriority.Utility => "UTILITY",
        PackagePriority.Dev => "DEV",
        LicensingModel.Free => "FREE",
        LicensingModel.Freemium => "FREEMIUM",
        LicensingModel.Paid => "PAID",
        LicensingModel.License => "LICENSE",
        LicensingModel.Subscription => "SUBSCRIPTION",
        LicensingModel.HardwareLicense => "HARDWARE-LICENSE",
        LicensingModel.DealerLicense => "DEALER-LICENSE",
        LicensingModel.UnknownCost => "UNKNOWN-COST",
        InstallationForm.Msi => "MSI",
        InstallationForm.Exe => "EXE",
        InstallationForm.Zip => "ZIP",
        SupportedOperatingSystem.MacOS => "macOS",
        SupportedOperatingSystem.IOS => "iOS",
        _ => value.ToString()
    };
}

public class CatalogValidationException(string message) : Exception(message);
