using System.Text.Json;
using System.Text.Json.Serialization;
using AVWorkstationToolkit.Application.Vendors;

namespace AVWorkstationToolkit.Infrastructure.Windows.Vendors;

public sealed class VendorTrustedHostStore
{
    private const int MaximumBytes = 65_536;

    public VendorSftpHostTrust? Read(string explicitDataRoot, VendorEndpoint endpoint)
    {
        ValidateEndpoint(endpoint);
        var path = StorePath(explicitDataRoot);
        if (!File.Exists(path)) return null;
        RejectReparse(path);
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("The trusted SFTP host store exceeds its size limit.");
        var document = JsonSerializer.Deserialize(File.ReadAllText(path), VendorTrustedHostJsonContext.Default.VendorTrustedHostDocument)
            ?? throw new InvalidDataException("The trusted SFTP host store is empty.");
        if (document.SchemaVersion != 1) throw new InvalidDataException("The trusted SFTP host store has an unsupported schema.");
        var match = document.Hosts.SingleOrDefault(item => item.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase) && item.Port == endpoint.Port);
        return match is null ? null : new VendorSftpHostTrust(new VendorEndpoint(match.Host.ToLowerInvariant(), match.Port), ValidateFingerprint(match.Fingerprint));
    }

    public VendorSftpHostTrust Trust(string explicitDataRoot, VendorEndpoint endpoint, string fingerprint)
    {
        ValidateEndpoint(endpoint);
        fingerprint = ValidateFingerprint(fingerprint);
        var path = StorePath(explicitDataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RejectReparse(Path.GetDirectoryName(path)!);
        IReadOnlyList<VendorTrustedHostRecord> existing = [];
        if (File.Exists(path))
        {
            RejectReparse(path);
            if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("The trusted SFTP host store exceeds its size limit.");
            var prior = JsonSerializer.Deserialize(File.ReadAllText(path), VendorTrustedHostJsonContext.Default.VendorTrustedHostDocument)
                ?? throw new InvalidDataException("The trusted SFTP host store is empty.");
            if (prior.SchemaVersion != 1) throw new InvalidDataException("The trusted SFTP host store has an unsupported schema.");
            existing = prior.Hosts;
        }
        var hosts = existing.Where(item => !(item.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase) && item.Port == endpoint.Port)).ToList();
        hosts.Add(new(endpoint.Host.ToLowerInvariant(), endpoint.Port, fingerprint, DateTimeOffset.UtcNow));
        var document = new VendorTrustedHostDocument(1, hosts);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, VendorTrustedHostJsonContext.Default.VendorTrustedHostDocument));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new VendorSftpHostTrust(new VendorEndpoint(endpoint.Host.ToLowerInvariant(), endpoint.Port), fingerprint);
    }

    private static string StorePath(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot) || !Path.IsPathFullyQualified(dataRoot)) throw new IOException("The SFTP trust data root must be absolute.");
        var root = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar);
        var volume = Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar);
        if (root.Equals(volume, StringComparison.OrdinalIgnoreCase)) throw new IOException("The SFTP trust data root cannot be a filesystem root.");
        if (Directory.Exists(root)) RejectReparse(root);
        return Path.Combine(root, "trusted-sftp-hosts.json");
    }

    private static void ValidateEndpoint(VendorEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (Uri.CheckHostName(endpoint.Host) != UriHostNameType.Dns || endpoint.Port is < 1 or > 65_535) throw new InvalidDataException("The trusted SFTP endpoint is invalid.");
    }

    private static string ValidateFingerprint(string fingerprint)
    {
        if (fingerprint.Length != 50 || !fingerprint.StartsWith("SHA256:", StringComparison.Ordinal) || fingerprint[7..].Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '+' or '/')))
            throw new InvalidDataException("The trusted SFTP fingerprint is invalid.");
        return fingerprint;
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("The SFTP trust path cannot be a reparse point.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VendorTrustedHostDocument(int SchemaVersion, IReadOnlyList<VendorTrustedHostRecord> Hosts);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VendorTrustedHostRecord(string Host, int Port, string Fingerprint, DateTimeOffset TrustedAt);
[JsonSerializable(typeof(VendorTrustedHostDocument))]
internal sealed partial class VendorTrustedHostJsonContext : JsonSerializerContext;
