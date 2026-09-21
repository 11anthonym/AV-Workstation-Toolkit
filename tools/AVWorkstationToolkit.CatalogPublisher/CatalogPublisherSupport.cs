using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.CatalogPublisher;

internal static class CatalogPublisherSupport
{
    public static UTF8Encoding StrictUtf8 { get; } = new(false, true);

    public static byte[] Sign(ECDsa key, byte[] bytes) =>
        key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public static ECDsa LoadPrivateKey(string path)
    {
        var text = File.ReadAllText(path, StrictUtf8);
        try
        {
            var key = ECDsa.Create();
            key.ImportFromPem(text);
            if (key.KeySize != 256) throw new CatalogValidationException("Catalog signing key must be ECDSA P-256.");
            return key;
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            throw new CatalogValidationException($"Catalog private key is invalid: {exception.Message}");
        }
    }

    public static byte[] WriteJson(Action<Utf8JsonWriter> write)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory, new JsonWriterOptions { Indented = true })) write(writer);
        return memory.ToArray();
    }

    public static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var output = entry.Open();
        output.Write(bytes);
    }

    public static string RequireDirectory(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new IOException($"Catalog publisher {description} must be absolute.");
        var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Catalog publisher {description} is unavailable or is a reparse point.");
        return full;
    }

    public static string RequireNewOutputRoot(string value, string repositoryRoot, string manifestsRoot)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new IOException("Catalog publisher output root must be absolute.");
        var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Directory.Exists(full) || File.Exists(full))
            throw new IOException("Catalog publisher output root already exists; immutable feed output is never overwritten.");
        if (IsContained(full, manifestsRoot) || string.Equals(full, repositoryRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Catalog publisher output cannot replace repository or manifest source paths.");
        return full;
    }

    public static string RequireExternalPrivateKey(string value, string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new IOException("Catalog publisher private-key path must be absolute.");
        var full = Path.GetFullPath(value);
        var info = new FileInfo(full);
        if (!info.Exists || info.Length <= 0 || info.Length > 64 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Catalog publisher private key is missing, empty, oversized, or a reparse point.");
        if (IsContained(full, repositoryRoot))
            throw new IOException("Catalog publisher private key must remain outside the repository.");
        return full;
    }

    public static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Catalog publisher output cannot use a reparse point.");
    }

    public static void WriteNewFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static bool IsContained(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !Path.IsPathFullyQualified(relative);
    }
}
