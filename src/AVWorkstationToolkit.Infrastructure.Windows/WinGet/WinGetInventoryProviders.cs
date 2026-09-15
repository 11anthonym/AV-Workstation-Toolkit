using System.Text.Json;
using System.Text.RegularExpressions;
using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Infrastructure.Windows.Processes;

namespace AVWorkstationToolkit.Infrastructure.Windows.WinGet;

public static partial class WinGetInventoryParsers
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9+_.-]{1,127}$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PackageIdPattern();

    [GeneratedRegex(@"^\s*Name\s+Id\s+Version\s+Available(?:\s+Source)?\s*$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex UpdateHeaderPattern();

    // Exact English resources emitted by WinGet's ReportListResult/upgrade flow.
    // Do not use a wildcard suffix: unfamiliar summaries must fail closed.
    [GeneratedRegex(@"^\d+\s+(?:upgrades? available\.?|(?:package\(s\)|packages?) (?:have version numbers that cannot be determined\. Use --include-unknown to see all results\.|are pinned and need to be explicitly upgraded\.|have pins that prevent upgrade\. Use the 'winget pin' command to view and edit pins\. Using the '--include-pinned' argument may show more results\.|have a pin that needs to be removed before upgrade\.?|have upgrades blocked because newer versions use a different install technology than the current installation\. Uninstall each package, then install the newer version\.))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex UpdateSummaryPattern();

    [GeneratedRegex(@"^\d+\s+(?:upgrades?|package\(s\)|packages?)(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex UpdateSummaryPrefixPattern();

    [GeneratedRegex(@"\S+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex UpdateCellPattern();

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
        var lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var foundTable = false;
        var foundNoUpdates = false;
        var tableNumber = 0;
        var section = "available updates";
        var blockedSection = false;
        var awaitingExplicitTable = false;
        UpdateTableColumns? columns = null;
        var updates = new Dictionary<string, AvailableUpdateRecord>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Contains('\u2026') || line.Trim().Equals("<Search results are truncated>", StringComparison.OrdinalIgnoreCase))
                throw UpdateOutputError("WinGet update output contains a truncation marker.", index, section, tableNumber, line);
            if (TryReadColumns(line, out var candidateColumns))
            {
                if (index + 1 >= lines.Length || lines[index + 1].Trim().Length < 8 ||
                    lines[index + 1].Trim().Any(character => character != '-'))
                    throw UpdateOutputError("WinGet update output contains a table header without a valid separator.", index, section, tableNumber + 1, line);
                foundTable = true;
                awaitingExplicitTable = false;
                tableNumber++;
                columns = candidateColumns;
                index++;
                continue;
            }
            var message = line.Trim();
            if (message.Equals("No installed package found matching input criteria.", StringComparison.OrdinalIgnoreCase) ||
                message.Equals("No applicable upgrade found.", StringComparison.OrdinalIgnoreCase))
            {
                foundNoUpdates = true;
                columns = null;
                continue; // A later explicit-target table can still contain updates.
            }
            if (message.Equals("The following packages have an upgrade available, but require explicit targeting for upgrade:", StringComparison.OrdinalIgnoreCase))
            {
                section = "explicit targeting";
                blockedSection = false;
                awaitingExplicitTable = true;
                columns = null;
                continue;
            }
            if (UpdateSummaryPattern().IsMatch(message))
            {
                if (message.Contains("have a pin that needs to be removed before upgrade", StringComparison.OrdinalIgnoreCase))
                {
                    section = "blocked by pin";
                    blockedSection = true;
                }
                columns = null;
                continue;
            }
            if (UpdateSummaryPrefixPattern().IsMatch(message) || message.StartsWith("The following packages", StringComparison.OrdinalIgnoreCase))
                throw UpdateOutputError("WinGet update output contains an unrecognized package summary.", index, section, tableNumber, line);
            // Redirected WinGet progress uses CR-delimited spinner frames. Keep
            // this finite allowance separate from table rows/source failures.
            if (columns is null && message is "-" or "\\" or "|" or "/") continue;
            if (!foundTable && message.Equals("The source requires review.", StringComparison.Ordinal)) continue;
            if (columns is null)
                throw UpdateOutputError("WinGet update output contains an unrecognized line outside a package table.", index, section, tableNumber, line);
            var current = columns.Value;
            var tokens = UpdateCellPattern().Matches(line);
            var minimum = current.HasSource ? 5 : 4;
            if (tokens.Count < minimum)
                throw UpdateOutputError("WinGet update output contains a malformed package row.", index, section, tableNumber, line);
            var idCell = tokens[^(current.HasSource ? 4 : 3)];
            var installedCell = tokens[^(current.HasSource ? 3 : 2)];
            var availableCell = tokens[^(current.HasSource ? 2 : 1)];
            var id = idCell.Value;
            var installed = installedCell.Value;
            var available = availableCell.Value;
            // ASCII redirected tables have exact column offsets. Unicode names
            // can have a different display width; retain right-hand cell parsing
            // for those rather than confusing display width with string length.
            var aligned = current.UsesTabs || line.Any(character => character > 127) ||
                (idCell.Index == current.Id && installedCell.Index == current.Version && availableCell.Index == current.Available &&
                 (!current.HasSource || tokens[^1].Index == current.Source));
            if (!aligned || !PackageIdPattern().IsMatch(id) ||
                (current.HasSource && !tokens[^1].Value.Equals("winget", StringComparison.OrdinalIgnoreCase)))
                throw UpdateOutputError("WinGet update output contains a malformed package row.", index, section, tableNumber, line);
            try
            {
                ValidateVersionText(id, installed);
                ValidateVersionText(id, available);
            }
            catch (InvalidDataException exception)
            {
                throw UpdateOutputError(exception.Message, index, section, tableNumber, line);
            }
            if (blockedSection) continue; // Validated blocked rows are not available updates.
            if (updates.TryGetValue(id, out var existing) && (existing.InstalledVersion != installed || existing.AvailableVersion != available))
                throw UpdateOutputError("WinGet update output contains conflicting package rows.", index, section, tableNumber, line);
            if (!updates.ContainsKey(id)) updates.Add(id, new(id, installed, available));
        }
        if (awaitingExplicitTable)
            throw UpdateOutputError("WinGet update output is missing the explicit-target package table.", lines.Length - 1, section, tableNumber, lines[^1]);
        if (!foundTable && !foundNoUpdates) throw new InvalidDataException("WinGet update output does not contain a valid table header.");
        return updates.Values.OrderBy(update => update.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool TryReadColumns(string line, out UpdateTableColumns columns)
    {
        var id = line.IndexOf("Id", StringComparison.Ordinal);
        var version = line.IndexOf("Version", StringComparison.Ordinal);
        var available = line.IndexOf("Available", StringComparison.Ordinal);
        var source = line.IndexOf("Source", StringComparison.Ordinal);
        var valid = UpdateHeaderPattern().IsMatch(line) && id > 4 && version > id && available > version;
        columns = valid ? new(id, version, available, source, source > available, line.Contains('\t')) : default;
        return valid;
    }

    private readonly record struct UpdateTableColumns(int Id, int Version, int Available, int Source, bool HasSource, bool UsesTabs);

    private static InvalidDataException UpdateOutputError(string reason, int lineIndex, string section, int tableNumber, string line)
    {
        var safeLine = DiagnosticsRedactor.Sanitize(DiagnosticText.Sanitize(line)).Trim();
        if (safeLine.Length > 240) safeLine = safeLine[..240] + " [excerpt shortened]";
        return new($"{reason} Line {lineIndex + 1}; section: {section}; table: {tableNumber}; excerpt: {safeLine}");
    }

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
        var diagnostic = DiagnosticText.Sanitize(output);
        if (process.Failure != ProviderFailureKind.None || process.ExitCode != 0)
            return new(ProviderQuality.Unavailable, process.Failure == ProviderFailureKind.None ? ProviderFailureKind.ExecutionFailed : process.Failure,
                [], "WinGet available-update query failed.", diagnostic);
        try
        {
            var updates = WinGetInventoryParsers.ParseAvailableUpdates(output);
            return new(ProviderQuality.Complete, ProviderFailureKind.None, updates,
                updates.Count == 0 ? "WinGet reports no available updates." : "Available updates loaded from validated WinGet output.", diagnostic);
        }
        catch (InvalidDataException exception)
        {
            return new(ProviderQuality.Malformed, ProviderFailureKind.MalformedOutput, [], exception.Message, diagnostic);
        }
    }
}
