using System.Text;
using AVWorkstationToolkit.Application.Diagnostics;

namespace AVWorkstationToolkit.Infrastructure.Windows.Diagnostics;

public sealed class DiagnosticsExportService(string explicitDataRoot, TimeProvider? timeProvider = null) : IDiagnosticsExportService
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false, true);
    private readonly string dataRoot = RequireRoot(explicitDataRoot);
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public string Export(string sanitizedDiagnostics)
    {
        var text = DiagnosticsRedactor.Sanitize(sanitizedDiagnostics);
        if (text.Length is 0 or > 1_000_000) throw new InvalidDataException("The diagnostics export is empty or exceeds its size limit.");
        var logs = Path.Combine(dataRoot, "logs");
        var exports = Path.Combine(logs, "diagnostics");
        Directory.CreateDirectory(exports);
        RejectReparse(dataRoot);
        RejectReparse(logs);
        RejectReparse(exports);
        var stamp = timeProvider.GetUtcNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var path = Path.Combine(exports, $"diagnostics-{stamp}.txt");
        if (File.Exists(path)) throw new IOException("A diagnostics export with the same timestamp already exists.");
        var temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, Utf8WithoutBom))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
            return path;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string RequireRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) throw new IOException("The diagnostics data root must be absolute.");
        var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
        var volume = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(full, volume, StringComparison.OrdinalIgnoreCase)) throw new IOException("The diagnostics data root cannot be a filesystem root.");
        Directory.CreateDirectory(full);
        RejectReparse(full);
        return full;
    }

    private static void RejectReparse(string path)
    {
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The diagnostics export path is unavailable or uses a reparse point.");
    }
}
