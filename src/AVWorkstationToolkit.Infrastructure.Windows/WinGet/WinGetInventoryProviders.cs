using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Infrastructure.Windows.Processes;

namespace AVWorkstationToolkit.Infrastructure.Windows.WinGet;

public static partial class WinGetInventoryParsers
{
    // Unicode EastAsianWidth W/F ranges, kept sorted for binary search. WinGet
    // 1.29.290 uses the same property through ICU when TableOutput pads cells.
    private static readonly (int Start, int End)[] WideOrFullWidthRanges =
    [
        (0x1100, 0x115f),
        (0x231a, 0x231b),
        (0x2329, 0x232a),
        (0x23e9, 0x23ec),
        (0x23f0, 0x23f0),
        (0x23f3, 0x23f3),
        (0x25fd, 0x25fe),
        (0x2614, 0x2615),
        (0x2630, 0x2637),
        (0x2648, 0x2653),
        (0x267f, 0x267f),
        (0x268a, 0x268f),
        (0x2693, 0x2693),
        (0x26a1, 0x26a1),
        (0x26aa, 0x26ab),
        (0x26bd, 0x26be),
        (0x26c4, 0x26c5),
        (0x26ce, 0x26ce),
        (0x26d4, 0x26d4),
        (0x26ea, 0x26ea),
        (0x26f2, 0x26f3),
        (0x26f5, 0x26f5),
        (0x26fa, 0x26fa),
        (0x26fd, 0x26fd),
        (0x2705, 0x2705),
        (0x270a, 0x270b),
        (0x2728, 0x2728),
        (0x274c, 0x274c),
        (0x274e, 0x274e),
        (0x2753, 0x2755),
        (0x2757, 0x2757),
        (0x2795, 0x2797),
        (0x27b0, 0x27b0),
        (0x27bf, 0x27bf),
        (0x2b1b, 0x2b1c),
        (0x2b50, 0x2b50),
        (0x2b55, 0x2b55),
        (0x2e80, 0x2e99),
        (0x2e9b, 0x2ef3),
        (0x2f00, 0x2fd5),
        (0x2ff0, 0x303e),
        (0x3041, 0x3096),
        (0x3099, 0x30ff),
        (0x3105, 0x312f),
        (0x3131, 0x318e),
        (0x3190, 0x31e5),
        (0x31ef, 0x321e),
        (0x3220, 0x3247),
        (0x3250, 0xa48c),
        (0xa490, 0xa4c6),
        (0xa960, 0xa97c),
        (0xac00, 0xd7a3),
        (0xf900, 0xfaff),
        (0xfe10, 0xfe19),
        (0xfe30, 0xfe52),
        (0xfe54, 0xfe66),
        (0xfe68, 0xfe6b),
        (0xff01, 0xff60),
        (0xffe0, 0xffe6),
        (0x16fe0, 0x16fe4),
        (0x16ff0, 0x16ff6),
        (0x17000, 0x18cd5),
        (0x18cff, 0x18d1e),
        (0x18d80, 0x18df2),
        (0x1aff0, 0x1aff3),
        (0x1aff5, 0x1affb),
        (0x1affd, 0x1affe),
        (0x1b000, 0x1b122),
        (0x1b132, 0x1b132),
        (0x1b150, 0x1b152),
        (0x1b155, 0x1b155),
        (0x1b164, 0x1b167),
        (0x1b170, 0x1b2fb),
        (0x1d300, 0x1d356),
        (0x1d360, 0x1d376),
        (0x1f004, 0x1f004),
        (0x1f0cf, 0x1f0cf),
        (0x1f18e, 0x1f18e),
        (0x1f191, 0x1f19a),
        (0x1f200, 0x1f202),
        (0x1f210, 0x1f23b),
        (0x1f240, 0x1f248),
        (0x1f250, 0x1f251),
        (0x1f260, 0x1f265),
        (0x1f300, 0x1f320),
        (0x1f32d, 0x1f335),
        (0x1f337, 0x1f37c),
        (0x1f37e, 0x1f393),
        (0x1f3a0, 0x1f3ca),
        (0x1f3cf, 0x1f3d3),
        (0x1f3e0, 0x1f3f0),
        (0x1f3f4, 0x1f3f4),
        (0x1f3f8, 0x1f43e),
        (0x1f440, 0x1f440),
        (0x1f442, 0x1f4fc),
        (0x1f4ff, 0x1f53d),
        (0x1f54b, 0x1f54e),
        (0x1f550, 0x1f567),
        (0x1f57a, 0x1f57a),
        (0x1f595, 0x1f596),
        (0x1f5a4, 0x1f5a4),
        (0x1f5fb, 0x1f64f),
        (0x1f680, 0x1f6c5),
        (0x1f6cc, 0x1f6cc),
        (0x1f6d0, 0x1f6d2),
        (0x1f6d5, 0x1f6d8),
        (0x1f6dc, 0x1f6df),
        (0x1f6eb, 0x1f6ec),
        (0x1f6f4, 0x1f6fc),
        (0x1f7e0, 0x1f7eb),
        (0x1f7f0, 0x1f7f0),
        (0x1f90c, 0x1f93a),
        (0x1f93c, 0x1f945),
        (0x1f947, 0x1f9ff),
        (0x1fa70, 0x1fa7c),
        (0x1fa80, 0x1fa8a),
        (0x1fa8e, 0x1fac6),
        (0x1fac8, 0x1fac8),
        (0x1facd, 0x1fadc),
        (0x1fadf, 0x1faea),
        (0x1faef, 0x1faf8),
        (0x20000, 0x2fffd),
        (0x30000, 0x3fffd)
    ];

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9+_.-]{1,127}$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PackageIdPattern();

    [GeneratedRegex(@"^Name +Id +Version +Available(?: +Source)? *$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex UpdateHeaderPattern();

    // Exact English resources emitted by WinGet's ReportListResult/upgrade flow.
    // Do not use a wildcard suffix: unfamiliar summaries must fail closed.
    [GeneratedRegex(@"^\d+\s+(?:upgrades? available\.?|(?:package\(s\)|packages?) (?:have version numbers that cannot be determined\. Use --include-unknown to see all results\.|are pinned and need to be explicitly upgraded\.|have pins that prevent upgrade\. Use the 'winget pin' command to view and edit pins\. Using the '--include-pinned' argument may show more results\.|have a pin that needs to be removed before upgrade\.?|have upgrades blocked because newer versions use a different install technology than the current installation\. Uninstall each package, then install the newer version\.))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex UpdateSummaryPattern();

    [GeneratedRegex(@"^\d+\s+(?:upgrades?|package\(s\)|packages?)(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex UpdateSummaryPrefixPattern();

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
            var line = lines[index].TrimEnd(' ');
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Contains('\u2026') || line.Trim().Equals("<Search results are truncated>", StringComparison.OrdinalIgnoreCase))
                throw UpdateOutputError("WinGet update output contains a truncation marker.", index, section, tableNumber, line);
            if (TryReadColumns(line, out var candidateColumns))
            {
                var separator = index + 1 < lines.Length ? lines[index + 1].Trim() : string.Empty;
                var minimumWidth = candidateColumns.HasSource ? candidateColumns.Source + "Source".Length : candidateColumns.Available + "Available".Length;
                if (separator.Length < Math.Max(8, minimumWidth) || separator.Any(character => character != '-'))
                    throw UpdateOutputError("WinGet update output contains a table header without a valid separator.", index, section, tableNumber + 1, line);
                foundTable = true;
                awaitingExplicitTable = false;
                tableNumber++;
                columns = candidateColumns with { TableWidth = separator.Length };
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
            if (!TryReadPackageRow(line, current, out var row))
                throw UpdateOutputError("WinGet update output contains a malformed package row.", index, section, tableNumber, line);
            var id = row.Id;
            var installed = row.InstalledVersion;
            var available = row.AvailableVersion;
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
        if (line.Contains('\t'))
        {
            var cells = line.Split('\t', StringSplitOptions.None);
            var validTabHeader = cells.Length is 4 or 5 &&
                cells[0].Equals("Name", StringComparison.Ordinal) &&
                cells[1].Equals("Id", StringComparison.Ordinal) &&
                cells[2].Equals("Version", StringComparison.Ordinal) &&
                cells[3].Equals("Available", StringComparison.Ordinal) &&
                (cells.Length == 4 || cells[4].Equals("Source", StringComparison.Ordinal));
            columns = validTabHeader ? new(0, 0, 0, 0, cells.Length == 5, true, 0) : default;
            return validTabHeader;
        }

        var id = line.IndexOf("Id", StringComparison.Ordinal);
        var version = line.IndexOf("Version", StringComparison.Ordinal);
        var available = line.IndexOf("Available", StringComparison.Ordinal);
        var source = line.IndexOf("Source", StringComparison.Ordinal);
        var valid = UpdateHeaderPattern().IsMatch(line) && id > 4 && version > id && available > version;
        columns = valid ? new(id, version, available, source, source > available, false, 0) : default;
        return valid;
    }

    private static bool TryReadPackageRow(string line, UpdateTableColumns columns, out UpdatePackageRow row)
    {
        row = default;
        if (columns.UsesTabs) return TryReadTabPackageRow(line, columns.HasSource, out row);
        if (line.Contains('\t') || line.Any(character => char.IsControl(character) || character == '\u007f')) return false;

        int[] starts = columns.HasSource ? [0, columns.Id, columns.Version, columns.Available, columns.Source] : [0, columns.Id, columns.Version, columns.Available];
        var indexes = new int[starts.Length];
        if (line.All(character => character <= '\u007f'))
        {
            if (line.Length > columns.TableWidth) return false;
            for (var i = 0; i < starts.Length; i++) indexes[i] = Math.Min(starts[i], line.Length);
        }
        else
        {
            if (!TryMapDisplayColumns(line, starts, indexes, out var displayWidth) || displayWidth > columns.TableWidth) return false;
        }

        for (var i = 1; i < indexes.Length; i++)
        {
            // TableOutput left-aligns every value and always emits at least one
            // ASCII padding space before the next column.
            if (indexes[i] >= line.Length || indexes[i] == 0 || line[indexes[i] - 1] != ' ' || line[indexes[i]] == ' ')
                return false;
        }

        var name = ReadCell(line, indexes[0], indexes[1]);
        var id = ReadCell(line, indexes[1], indexes[2]);
        var installed = ReadCell(line, indexes[2], indexes[3]);
        var available = ReadCell(line, indexes[3], columns.HasSource ? indexes[4] : line.Length);
        var source = columns.HasSource ? ReadCell(line, indexes[4], line.Length) : string.Empty;
        return TryCreatePackageRow(name, id, installed, available, source, columns.HasSource, out row);
    }

    private static bool TryReadTabPackageRow(string line, bool hasSource, out UpdatePackageRow row)
    {
        row = default;
        var cells = line.Split('\t', StringSplitOptions.None);
        if (cells.Length != (hasSource ? 5 : 4)) return false;
        for (var i = 0; i < cells.Length; i++) cells[i] = cells[i].Trim(' ');
        return TryCreatePackageRow(cells[0], cells[1], cells[2], cells[3], hasSource ? cells[4] : string.Empty, hasSource, out row);
    }

    private static bool TryCreatePackageRow(
        string name,
        string id,
        string installed,
        string available,
        string source,
        bool hasSource,
        out UpdatePackageRow row)
    {
        row = default;
        if (name.Length == 0 || id.Length == 0 || installed.Length == 0 || available.Length == 0 ||
            name.Any(character => char.IsControl(character) || character == '\u007f') ||
            !PackageIdPattern().IsMatch(id) ||
            (hasSource && !source.Equals("winget", StringComparison.OrdinalIgnoreCase)))
            return false;
        row = new(id, installed, available);
        return true;
    }

    private static string ReadCell(string line, int start, int end) => line[start..end].TrimEnd(' ');

    private static bool TryMapDisplayColumns(string line, int[] displayColumns, int[] indexes, out int displayWidth)
    {
        displayWidth = 0;
        var target = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(line);
        while (enumerator.MoveNext())
        {
            var elementIndex = enumerator.ElementIndex;
            while (target < displayColumns.Length && displayColumns[target] == displayWidth)
            {
                indexes[target] = elementIndex;
                target++;
            }

            var elementWidth = IsWideOrFullWidth(Rune.GetRuneAt(enumerator.GetTextElement(), 0)) ? 2 : 1;
            if (target < displayColumns.Length && displayColumns[target] > displayWidth && displayColumns[target] < displayWidth + elementWidth)
                return false;
            displayWidth += elementWidth;
        }

        while (target < displayColumns.Length)
        {
            if (displayColumns[target] < displayWidth) return false;
            indexes[target] = line.Length;
            target++;
        }
        return true;
    }

    // WinGet TableOutput uses the first code point in each grapheme and gives
    // Unicode East Asian Wide/Fullwidth values two display columns. Keep that
    // distinction explicit so UTF-16 indexes never stand in for terminal width.
    private static bool IsWideOrFullWidth(Rune rune)
    {
        var value = rune.Value;
        var low = 0;
        var high = WideOrFullWidthRanges.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var range = WideOrFullWidthRanges[middle];
            if (value < range.Start) high = middle - 1;
            else if (value > range.End) low = middle + 1;
            else return true;
        }
        return false;
    }

    private readonly record struct UpdateTableColumns(int Id, int Version, int Available, int Source, bool HasSource, bool UsesTabs, int TableWidth);
    private readonly record struct UpdatePackageRow(string Id, string InstalledVersion, string AvailableVersion);

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
