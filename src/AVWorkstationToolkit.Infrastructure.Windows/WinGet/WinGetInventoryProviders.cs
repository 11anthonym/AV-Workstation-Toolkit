using System.Text.Json;
using System.Text.RegularExpressions;
using AVWorkstationToolkit.Application.Inventory;

namespace AVWorkstationToolkit.Infrastructure.Windows.WinGet;

public static partial class WinGetInventoryParsers
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9+_.-]{1,127}$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PackageIdPattern();

    public static IReadOnlyList<InstalledPackageRecord> ParseInstalledExport(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("Sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("WinGet export JSON does not contain the required Sources collection.");

        var packages = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty("Packages", out var sourcePackages)) continue;
            if (sourcePackages.ValueKind != JsonValueKind.Array) throw new InvalidDataException("WinGet export Packages value is not an array.");
            foreach (var raw in sourcePackages.EnumerateArray())
            {
                if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("PackageIdentifier", out var idValue) || idValue.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("WinGet export JSON contains a package without PackageIdentifier.");
                var id = idValue.GetString() ?? string.Empty;
                if (id != id.Trim() || !PackageIdPattern().IsMatch(id)) throw new InvalidDataException($"WinGet export JSON contains an invalid package identifier: '{id}'.");
                var version = string.Empty;
                if (raw.TryGetProperty("Version", out var versionValue))
                {
                    if (versionValue.ValueKind != JsonValueKind.String) throw new InvalidDataException($"WinGet export JSON contains a non-string version for '{id}'.");
                    version = versionValue.GetString()?.Trim() ?? string.Empty;
                }
                ValidateVersionText(id, version);
                if (!packages.TryGetValue(id, out var versions)) packages.Add(id, versions = []);
                if (version.Length > 0 && !versions.Contains(version, StringComparer.Ordinal)) versions.Add(version);
            }
        }
        return packages.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new InstalledPackageRecord(pair.Key, string.Join(" / ", pair.Value.OrderBy(value => value, StringComparer.Ordinal))))
            .ToArray();
    }

    public static IReadOnlyList<AvailableUpdateRecord> ParseAvailableUpdates(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("WinGet update output is empty.");
        if (text.Contains('\u2026')) throw new InvalidDataException("WinGet update output contains a truncation marker.");
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Any(line => line.Contains("No installed package found matching input criteria", StringComparison.OrdinalIgnoreCase) ||
                              line.Contains("No applicable upgrade found", StringComparison.OrdinalIgnoreCase))) return [];
        var foundTable = false;
        UpdateTableColumns? columns = null;
        var updates = new Dictionary<string, AvailableUpdateRecord>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (TryReadColumns(line, out var candidateColumns))
            {
                if (index + 1 >= lines.Length || lines[index + 1].Trim().Length < 8 ||
                    lines[index + 1].Trim().Any(character => character != '-'))
                    throw new InvalidDataException("WinGet update output contains a table header without a valid separator.");
                foundTable = true;
                columns = candidateColumns;
                index++;
                continue;
            }
            if (Regex.IsMatch(line, @"^\s*\d+\s+upgrades?\s+available\.\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) ||
                Regex.IsMatch(line, @"^\s*\d+\s+packages?\s+have\s+version\s+numbers?.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) ||
                line.Equals("The following packages have an upgrade available, but require explicit targeting for upgrade:", StringComparison.OrdinalIgnoreCase)) continue;
            if (columns is null) continue; // Bounded source diagnostics may precede the first validated table.
            var current = columns.Value;
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var minimum = current.HasSource ? 5 : 4;
            if (tokens.Length < minimum) throw new InvalidDataException("WinGet update output contains a malformed package row.");
            var id = tokens[^(current.HasSource ? 4 : 3)];
            var installed = tokens[^(current.HasSource ? 3 : 2)];
            var available = tokens[^(current.HasSource ? 2 : 1)];
            if (!PackageIdPattern().IsMatch(id) || installed.Length == 0 || available.Length == 0)
                throw new InvalidDataException("WinGet update output contains a malformed package row.");
            ValidateVersionText(id, installed);
            ValidateVersionText(id, available);
            if (!updates.ContainsKey(id)) updates.Add(id, new(id, installed, available));
        }
        if (!foundTable) throw new InvalidDataException("WinGet update output does not contain a valid table header.");
        return updates.Values.OrderBy(update => update.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool TryReadColumns(string line, out UpdateTableColumns columns)
    {
        var id = line.IndexOf("Id", StringComparison.Ordinal);
        var version = line.IndexOf("Version", StringComparison.Ordinal);
        var available = line.IndexOf("Available", StringComparison.Ordinal);
        var source = line.IndexOf("Source", StringComparison.Ordinal);
        var valid = line.StartsWith("Name", StringComparison.Ordinal) && id > 4 && version > id && available > version;
        columns = valid ? new(source > available) : default;
        return valid;
    }

    private readonly record struct UpdateTableColumns(bool HasSource);

    public static bool DiagnosticNamesMissingCatalogPackage(
        string diagnosticOutput,
        IEnumerable<(string Id, string Name)> catalogPackages,
        IReadOnlyCollection<InstalledPackageRecord> installed)
    {
        if (string.IsNullOrWhiteSpace(diagnosticOutput)) return false;
        var installedIds = installed.Select(package => package.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return catalogPackages.Where(package => !installedIds.Contains(package.Id)).Any(package =>
            diagnosticOutput.Contains(package.Id, StringComparison.OrdinalIgnoreCase) ||
            diagnosticOutput.Contains(package.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateVersionText(string id, string value)
    {
        if (value.Length > 256 || value.Any(character => char.IsControl(character) || character == '\u007f'))
            throw new InvalidDataException($"WinGet output contains an invalid version for '{id}'.");
    }
}

public sealed class WinGetInstalledPackageInventory : IInstalledPackageInventory
{
    private readonly IWinGetReadOnlyProcessRunner runner;
    private readonly IReadOnlyList<(string Id, string Name)> managedCatalog;

    public WinGetInstalledPackageInventory(IWinGetReadOnlyProcessRunner runner, IEnumerable<(string Id, string Name)> managedCatalog)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.managedCatalog = managedCatalog?.ToArray() ?? throw new ArgumentNullException(nameof(managedCatalog));
    }

    public async Task<InstalledPackageInventoryResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        var process = await runner.RunAsync(WinGetReadOnlyOperation.InstalledInventory, cancellationToken).ConfigureAwait(false);
        var diagnostic = string.Join(Environment.NewLine, new[] { process.StdOut, process.StdErr }.Where(value => value.Length > 0));
        if (process.Failure != ProviderFailureKind.None || process.ExitCode != 0)
            return new(ProviderQuality.Unavailable, process.Failure == ProviderFailureKind.None ? ProviderFailureKind.ExecutionFailed : process.Failure,
                [], "WinGet installed-package inventory command failed.", diagnostic);
        try
        {
            var packages = WinGetInventoryParsers.ParseInstalledExport(process.ExportJson);
            if (WinGetInventoryParsers.DiagnosticNamesMissingCatalogPackage(diagnostic, managedCatalog, packages))
                return new(ProviderQuality.Partial, ProviderFailureKind.PartialInventory, packages,
                    "WinGet reported a catalog package that could not be mapped into structured inventory.", diagnostic);
            return new(ProviderQuality.Complete, ProviderFailureKind.None, packages,
                "Installed-package inventory loaded from validated WinGet export JSON.", diagnostic);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            return new(ProviderQuality.Malformed, ProviderFailureKind.MalformedOutput, [], exception.Message, diagnostic);
        }
    }
}

public sealed class WinGetAvailableUpdateInventory : IAvailableUpdateInventory
{
    private readonly IWinGetReadOnlyProcessRunner runner;
    public WinGetAvailableUpdateInventory(IWinGetReadOnlyProcessRunner runner) => this.runner = runner ?? throw new ArgumentNullException(nameof(runner));

    public async Task<AvailableUpdateInventoryResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        var process = await runner.RunAsync(WinGetReadOnlyOperation.AvailableUpdates, cancellationToken).ConfigureAwait(false);
        var output = string.Join(Environment.NewLine, new[] { process.StdOut, process.StdErr }.Where(value => value.Length > 0));
        if (process.Failure != ProviderFailureKind.None || process.ExitCode != 0)
            return new(ProviderQuality.Unavailable, process.Failure == ProviderFailureKind.None ? ProviderFailureKind.ExecutionFailed : process.Failure,
                [], "WinGet available-update query failed.", output);
        try
        {
            var updates = WinGetInventoryParsers.ParseAvailableUpdates(output);
            return new(ProviderQuality.Complete, ProviderFailureKind.None, updates,
                updates.Count == 0 ? "WinGet reports no available updates." : "Available updates loaded from validated WinGet output.", output);
        }
        catch (InvalidDataException exception)
        {
            return new(ProviderQuality.Malformed, ProviderFailureKind.MalformedOutput, [], exception.Message, output);
        }
    }
}
