using System.Text.Json;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class ProductionInventoryDetectionTests
{
    [TestMethod]
    public void RealInstalledProgramNamesResolveToExactlyTheirCatalogRecord()
    {
        var root = RepositoryRoot();
        var parser = new CatalogParser(DateOnly.FromDateTime(DateTime.UtcNow));
        var packages = parser.ParseExternalCatalog(File.ReadAllText(Path.Combine(root, "manifests", "external-applications.json"))).Items
            .Concat(parser.ParseExternalCatalog(File.ReadAllText(Path.Combine(root, "manifests", "commercial-av-catalog.json")), CatalogAuthority.AwarenessOnly).Items)
            .Where(package => package.DetectionMode == DetectionMode.Registry)
            .ToArray();
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "inventory", "installed-program-names.json")));
        var matcher = new ExternalInventoryMatcher();
        var failures = new List<string>();

        foreach (var entry in fixture.RootElement.GetProperty("Entries").EnumerateArray())
        {
            var name = entry.GetProperty("DisplayName").GetString()!;
            var expected = entry.GetProperty("Expected").GetString();
            var inventory = new RegistryInventoryResult(ProviderQuality.Complete, ProviderFailureKind.None,
                [new RegistryUninstallRecord(RegistryInventorySource.Hklm64, name, "1.0")],
                [new(RegistryInventorySource.Hklm64, true, 1, "ok"), new(RegistryInventorySource.Hklm32, true, 0, "ok"), new(RegistryInventorySource.Hkcu, true, 0, "ok")], "ok");
            var detected = packages.Where(package => matcher.Match(package, inventory).Installed).Select(package => package.Id).ToArray();
            var wanted = expected is null ? [] : new[] { expected };
            if (!detected.SequenceEqual(wanted, StringComparer.Ordinal))
                failures.Add($"'{name}' → [{string.Join(", ", detected)}], expected [{string.Join(", ", wanted)}]");
        }

        Assert.HasCount(0, failures, string.Join(Environment.NewLine, failures));
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "VERSION"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
