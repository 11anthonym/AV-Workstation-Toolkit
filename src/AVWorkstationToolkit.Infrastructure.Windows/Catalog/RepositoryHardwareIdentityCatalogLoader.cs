using System.Text;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Infrastructure.Windows.Catalog;

/// <summary>Loads the fixed, read-only hardware identity document from an application or repository root.</summary>
public sealed class RepositoryHardwareIdentityCatalogLoader
{
    public const string RelativePath = "manifests/hardware-identities.json";
    private const long MaximumDocumentBytes = 1_048_576;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public HardwareIdentityCatalog Load(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(fullRoot, RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The hardware identity catalog path escaped its application root.");
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("The production hardware identity catalog is unavailable.", path);
        if (info.Length <= 0 || info.Length > MaximumDocumentBytes)
            throw new IOException($"The production hardware identity catalog must be between 1 and {MaximumDocumentBytes} bytes.");
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The production hardware identity catalog cannot be a reparse point.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: true);
        return new HardwareIdentityCatalogParser().Parse(reader.ReadToEnd());
    }
}
