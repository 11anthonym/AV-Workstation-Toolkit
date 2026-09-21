using System.IO.Compression;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Infrastructure.Windows.Catalog;

internal static class CatalogBundleReader
{
    public static IReadOnlyDictionary<string, byte[]> ReadExact(
        Stream stream,
        IReadOnlySet<string> approvedNames,
        long maximumBundleBytes,
        long maximumPayloadBytes,
        long maximumEntryBytes,
        string description)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek && stream.Length - stream.Position > maximumBundleBytes)
            throw new CatalogValidationException($"{description} bundle size is invalid.");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count != approvedNames.Count)
            throw new CatalogValidationException($"{description} bundle contains an unexpected number of entries.");

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName != entry.Name || !approvedNames.Contains(entry.Name))
                throw new CatalogValidationException($"{description} entry '{entry.FullName}' is not permitted.");
            if (!files.TryAdd(entry.Name, ReadBounded(entry, ref total, maximumBundleBytes, maximumPayloadBytes, maximumEntryBytes, description)))
                throw new CatalogValidationException($"{description} repeats entry '{entry.Name}'.");
        }
        if (total > maximumPayloadBytes)
            throw new CatalogValidationException($"{description} uncompressed content is too large.");
        return files;
    }

    private static byte[] ReadBounded(
        ZipArchiveEntry entry,
        ref long total,
        long maximumBundleBytes,
        long maximumPayloadBytes,
        long maximumEntryBytes,
        string description)
    {
        if (entry.Length <= 0 || entry.Length > maximumEntryBytes || entry.CompressedLength > maximumBundleBytes)
            throw new CatalogValidationException($"{description} entry '{entry.Name}' has an invalid size.");
        using var input = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        var buffer = new byte[16_384];
        long entryTotal = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            entryTotal += read;
            total += read;
            if (entryTotal > maximumEntryBytes || total > maximumPayloadBytes)
                throw new CatalogValidationException($"{description} decompression limit was exceeded.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
