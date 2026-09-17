using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.WinGet;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class WinGetUpdateParserTests
{
    private const string Header = "Name                  Id           Version  Available Source\n---------------------------------------------------------------\n";
    private const string Row = "Vendor Tool           Vendor.Tool  1.2.3    1.3.0     winget";

    [TestMethod]
    [DataRow("1.29.290-redirected.txt", "Chocolatey.Chocolatey,GitHub.cli,Google.Chrome,Microsoft.VCRedist.2015+.x86")]
    [DataRow("1.29.290-firewire-spaced-version.txt", "Microsoft.Firewire1394LegacyDriver,Vendor.WidthSentinel")]
    [DataRow("with-source.txt", "Other.App,Vendor.Tool")]
    [DataRow("explicit-only.txt", "Chocolatey.Chocolatey")]
    [DataRow("no-eligible-updates.txt", "")]
    [DataRow("blocked-pin-table.txt", "Vendor.Tool")]
    public async Task CurrentWinGetFixturesParseThroughTheProductionInventoryProvider(string fixture, string expectedIds)
    {
        var output = Fixture(fixture);
        var updates = WinGetInventoryParsers.ParseAvailableUpdates(output);
        CollectionAssert.AreEqual(expectedIds.Split(',', StringSplitOptions.RemoveEmptyEntries), updates.Select(update => update.Id).ToArray());

        var runner = new FixedRunner(output);
        var result = await new WinGetAvailableUpdateInventory(runner).ReadAsync();
        Assert.AreEqual(WinGetReadOnlyOperation.AvailableUpdates, runner.Operation);
        Assert.AreEqual(ProviderQuality.Complete, result.Quality);
        Assert.AreEqual(ProviderFailureKind.None, result.Failure);
        CollectionAssert.AreEqual(updates.ToArray(), result.Updates.ToArray());
        if (fixture == "1.29.290-redirected.txt")
            Assert.AreEqual(new AvailableUpdateRecord("Microsoft.VCRedist.2015+.x86", "14.29.30037.0", "14.51.36247.0"), updates[^1]);
        if (fixture == "1.29.290-firewire-spaced-version.txt")
        {
            var fireWire = updates.Single(update => update.Id == "Microsoft.Firewire1394LegacyDriver");
            Assert.AreEqual("< 0.0.1", fireWire.InstalledVersion);
            Assert.AreEqual("0.0.1", fireWire.AvailableVersion);
            Assert.AreEqual(fireWire, result.Updates.Single(update => update.Id == "Microsoft.Firewire1394LegacyDriver"));
        }
    }

    [TestMethod]
    public void RedirectedBomCrSpinnerFramesAndUnicodeNamesRemainSupported()
    {
        var output = "\uFEFF  -\r  \\\r  |\r  /\r" + (Header + Row + "\n1 upgrade available.").Replace('\n', '\r');
        Assert.HasCount(1, WinGetInventoryParsers.ParseAvailableUpdates(output));
        var unicode = Header + "工具套件              Vendor.Tool  < 1.0    >= 2.0    winget";
        Assert.AreEqual(new AvailableUpdateRecord("Vendor.Tool", "< 1.0", ">= 2.0"), WinGetInventoryParsers.ParseAvailableUpdates(unicode).Single());

        foreach (var nameCell in new[]
        {
            "Käse Tool".PadRight(22),
            "A\u0308 Tool".PadRight(23), // One grapheme, two UTF-16 code units, one display column.
            "🦆 Tool".PadRight(22) // One surrogate pair and two display columns.
        })
        {
            var nonAscii = Header + nameCell + "Vendor.Tool  < 1.0    >= 2.0    winget";
            Assert.AreEqual(new AvailableUpdateRecord("Vendor.Tool", "< 1.0", ">= 2.0"), WinGetInventoryParsers.ParseAvailableUpdates(nonAscii).Single());
        }
    }

    [TestMethod]
    public void HeaderSpansPreserveOpaqueSpacedVersionsWithAndWithoutSource()
    {
        var withSource = Header + "Vendor Tool           Vendor.Tool  < 1.0    >= 2.0    winget";
        var withoutSource = Header.Replace(" Source", string.Empty, StringComparison.Ordinal)
            .Replace(new string('-', 63), new string('-', 56), StringComparison.Ordinal) +
            "Vendor Tool           Vendor.Tool  < 1.0    >= 2.0";

        Assert.AreEqual(new AvailableUpdateRecord("Vendor.Tool", "< 1.0", ">= 2.0"), WinGetInventoryParsers.ParseAvailableUpdates(withSource).Single());
        Assert.AreEqual(new AvailableUpdateRecord("Vendor.Tool", "< 1.0", ">= 2.0"), WinGetInventoryParsers.ParseAvailableUpdates(withoutSource).Single());
    }

    [TestMethod]
    public void LongAsciiNamesAndIdsUseTheDetectedColumnSpans()
    {
        var name = new string('N', 180);
        var id = "Vendor." + new string('I', 100);
        var header = "Name".PadRight(name.Length + 1) + "Id".PadRight(id.Length + 1) + "Version".PadRight(20) + "Available";
        var row = name + ' ' + id + ' ' + "version with spaces".PadRight(20) + "2.0";
        var output = header + '\n' + new string('-', header.Length) + '\n' + row;

        Assert.AreEqual(new AvailableUpdateRecord(id, "version with spaces", "2.0"), WinGetInventoryParsers.ParseAvailableUpdates(output).Single());
    }

    [TestMethod]
    public void TabDelimitedCompatibilityPreservesSpacedVersionCells()
    {
        const string withSource = "Name\tId\tVersion\tAvailable\tSource\n--------------------------------\nVendor Tool\tVendor.Tool\t< 1.0\t>= 2.0\twinget";
        const string withoutSource = "Name\tId\tVersion\tAvailable\n-------------------------\nVendor Tool\tVendor.Tool\t< 1.0\t>= 2.0";

        Assert.AreEqual(new AvailableUpdateRecord("Vendor.Tool", "< 1.0", ">= 2.0"), WinGetInventoryParsers.ParseAvailableUpdates(withSource).Single());
        Assert.AreEqual(new AvailableUpdateRecord("Vendor.Tool", "< 1.0", ">= 2.0"), WinGetInventoryParsers.ParseAvailableUpdates(withoutSource).Single());
    }

    [TestMethod]
    public void NoSourceTrailingDataAndAmbiguousTabCellsFailClosed()
    {
        const string noSourceHeader = "Name                  Id           Version  Available\n------------------------------------------------------\n";
        Assert.Throws<InvalidDataException>(() => WinGetInventoryParsers.ParseAvailableUpdates(
            noSourceHeader + "Vendor Tool           Vendor.Tool  1.2.3    1.3.0 unexpected trailing data"));
        Assert.Throws<InvalidDataException>(() => WinGetInventoryParsers.ParseAvailableUpdates(
            "Name\tId\tVersion\tAvailable\n-------------------------\nVendor Tool\tVendor.Tool\t< 1.0\t2.0\tambiguous"));
    }

    [TestMethod]
    [DataRow("Vendor Tool           Vendor/Tool  1.2.3    1.3.0     winget")]
    [DataRow("Vendor Tool           Vendor.Tool  1.2.3")]
    [DataRow("Vendor Tool           Vendor.Tool  1.2.3    1.3.0     other-source")]
    [DataRow("Vendor Tool Vendor.Tool 1.2.3 1.3.0 winget")]
    [DataRow("Vendor Tool           Vendor.Tool  1.2.3    1.3.0     winget extra")]
    [DataRow("Vendor Tool          Vendor.Tool  1.2.3    1.3.0     winget")]
    [DataRow("Vendor Tool            Vendor.Tool  1.2.3    1.3.0     winget")]
    [DataRow("Vendor Tool                        1.2.3    1.3.0     winget")]
    [DataRow("Vendor Tool           Vendor.Tool           1.3.0     winget")]
    [DataRow("Vendor Tool           Vendor.Tool  1.2.3              winget")]
    [DataRow("Vendor Tool\tVendor.Tool\t1.2.3\t1.3.0\twinget\textra")]
    [DataRow("Vendor Tool           Vendor.Tool  1.2.3    1.3.0     winget\u2026")]
    [DataRow("Vendor Tool           Vendor.Tool  1.2.\u00013    1.3.0     winget")]
    [DataRow("1 package(s) have version numbers that cannot be determined. Unrecognized instructions.")]
    [DataRow("1 package(s) are pinned and need to be explicitly upgraded. Vendor.Tool 1.0 2.0 winget")]
    [DataRow("1 package(s) are pinned and need to be explicitly upgraded. 未知 Vendor.Tool 1.0 2.0 winget")]
    public async Task MalformedRowsAndUnrecognizedSummarySuffixesFailTheWholeInventory(string malformed)
    {
        var output = Header + Row + "\n" + malformed;
        Assert.Throws<InvalidDataException>(() => WinGetInventoryParsers.ParseAvailableUpdates(output));
        var result = await new WinGetAvailableUpdateInventory(new FixedRunner(output)).ReadAsync();
        Assert.AreEqual(ProviderQuality.Malformed, result.Quality);
        Assert.AreEqual(ProviderFailureKind.MalformedOutput, result.Failure);
        Assert.IsEmpty(result.Updates); // Never expose partial rows as complete evidence.
    }

    [TestMethod]
    public void UnknownLinesAfterASummaryOrNoUpdatesMarkerCannotBecomePackageRows()
    {
        foreach (var output in new[]
        {
            Header + Row + "\n1 upgrade available.\nUnexpected text Vendor.Tool 1.0 2.0 winget",
            "No applicable upgrade found.\nUnexpected text Vendor.Tool 1.0 2.0 winget",
            "No installed package found matching input criteria.\nThe following packages have an upgrade available, but require explicit targeting for upgrade:",
            "Unexpected text Vendor.Tool 1.0 2.0 winget\n" + Header + Row,
            Header + Row + "\n1 package(s) have version numbers that cannot be determined. Use --include-unknown to see all results.\nUnexpected text"
        })
            Assert.Throws<InvalidDataException>(() => WinGetInventoryParsers.ParseAvailableUpdates(output));
    }

    [TestMethod]
    public void InvalidHeadersVersionsAndConflictingDuplicateRowsRemainFailClosed()
    {
        Assert.Throws<InvalidDataException>(() => WinGetInventoryParsers.ParseAvailableUpdates(Header.Replace("-----", "bad--", StringComparison.Ordinal) + Row));
        Assert.Throws<InvalidDataException>(() => WinGetInventoryParsers.ParseAvailableUpdates(Header + Row.Replace("1.3.0", new string('1', 257), StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => WinGetInventoryParsers.ParseAvailableUpdates(Header + Row + "\n" + Row.Replace("1.3.0", "1.4.0", StringComparison.Ordinal)));
        Assert.HasCount(1, WinGetInventoryParsers.ParseAvailableUpdates(Header + Row + "\n" + Row.Replace("Vendor.Tool", "vendor.tool", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task MalformedOutputReportsLineAndSectionWithBoundedRedactedContext()
    {
        var malformed = Fixture("with-source.txt").Replace("Other.App", "Other/App", StringComparison.Ordinal);
        var result = await new WinGetAvailableUpdateInventory(new FixedRunner(malformed)).ReadAsync();
        StringAssert.Contains(result.Detail, "malformed package row");
        StringAssert.Contains(result.Detail, "Line 9; section: explicit targeting; table: 2");
        StringAssert.Contains(result.Detail, "Other/App");

        var marker = Guid.NewGuid().ToString("N");
        var sensitiveLine = $"password=\"first {marker}\" Authorization: Bearer {marker} https://operator:{marker}@example.invalid " +
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + " " + new string('x', 500);
        var redacted = await new WinGetAvailableUpdateInventory(new FixedRunner(Header + Row + "\n1 upgrade available.\n" + sensitiveLine)).ReadAsync();
        Assert.AreEqual(ProviderQuality.Malformed, redacted.Quality);
        Assert.DoesNotContain(marker, redacted.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, redacted.DiagnosticOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), redacted.Detail, StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(redacted.Detail, "[REDACTED]");
        StringAssert.Contains(redacted.Detail, "[excerpt shortened]");
        Assert.IsLessThan(450, redacted.Detail.Length);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(RepositoryRootLocator.Find(), "tests", "fixtures", "winget-current", name));

    private sealed class FixedRunner(string output) : IWinGetReadOnlyProcessRunner
    {
        public WinGetReadOnlyOperation Operation { get; private set; }
        public Task<WinGetProcessResult> RunAsync(WinGetReadOnlyOperation operation, CancellationToken cancellationToken = default)
        {
            Operation = operation;
            return Task.FromResult(new WinGetProcessResult(0, output, string.Empty, string.Empty, false, false, false, ProviderFailureKind.None));
        }
    }
}
