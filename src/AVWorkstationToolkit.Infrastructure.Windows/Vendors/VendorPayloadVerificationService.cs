using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AVWorkstationToolkit.Application.Vendors;
using AVWorkstationToolkit.Infrastructure.Windows.Authenticode;

namespace AVWorkstationToolkit.Infrastructure.Windows.Vendors;

public sealed class VendorPayloadVerificationService(VendorCachePathPolicy paths, IAuthenticodeSignatureInspector signatures)
{
    private const int MaximumMetadataBytes = 65_536;

    public VendorDownloadResult VerifyAndPromote(
        VendorDeliveryAuthorization authorization, string explicitDataRoot, string temporaryPath)
    {
        var path = paths.ValidateTemporaryPayload(explicitDataRoot, authorization, temporaryPath);
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > authorization.MaximumBytes) return Reject(path, "The downloaded payload size violates the catalogued limit.");
        var hash = Hash(path);
        if (!string.IsNullOrWhiteSpace(authorization.ExpectedSha256) && !hash.Equals(authorization.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            return Reject(path, "The downloaded payload SHA-256 hash does not match the catalogued hash.");
        var signature = signatures.Inspect(path);
        if (!signature.Valid || !Regex.IsMatch(signature.SignerSubject, authorization.PublisherPattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)))
            return Reject(path, "The downloaded payload failed Authenticode publisher validation.");

        var finalPath = path[..^".download".Length];
        File.Move(path, finalPath, overwrite: true);
        var metadata = new VendorCacheMetadata(1, authorization.PackageId, authorization.Version, Path.GetFileName(finalPath), hash,
            signature.SignerSubject, DateTimeOffset.UtcNow);
        WriteMetadata(finalPath + ".avworkstationtoolkit.json", metadata);
        return new(VendorPayloadState.Verified, finalPath, "Vendor payload signature, publisher, hash, and cache metadata are valid.");
    }

    public VendorDownloadResult ResolveCached(VendorDeliveryAuthorization authorization, string explicitDataRoot, string payloadPath)
    {
        string fullPayload;
        try { fullPayload = paths.ValidateCachedPayload(explicitDataRoot, authorization, payloadPath); }
        catch (IOException) { return new(VendorPayloadState.Rejected, string.Empty, "The cached payload path is invalid or unavailable."); }
        var temporary = fullPayload + ".download";
        if (File.Exists(temporary)) return new(VendorPayloadState.Rejected, string.Empty, "An incomplete vendor cache payload exists.");
        var metadataPath = fullPayload + ".avworkstationtoolkit.json";
        if (!File.Exists(fullPayload) || !File.Exists(metadataPath)) return new(VendorPayloadState.Rejected, string.Empty, "No verified vendor download is cached.");
        if (new FileInfo(metadataPath).Length > MaximumMetadataBytes) return new(VendorPayloadState.Rejected, string.Empty, "Vendor cache metadata exceeds its size limit.");
        VendorCacheMetadata? metadata;
        try { metadata = JsonSerializer.Deserialize(File.ReadAllText(metadataPath), VendorCacheJsonContext.Default.VendorCacheMetadata); }
        catch (JsonException) { return new(VendorPayloadState.Rejected, string.Empty, "Vendor cache metadata is malformed."); }
        if (metadata is null || metadata.SchemaVersion != 1 || metadata.PackageId != authorization.PackageId || metadata.Version != authorization.Version || metadata.FileName != Path.GetFileName(fullPayload))
            return new(VendorPayloadState.Rejected, string.Empty, "Vendor cache metadata does not match the package identity.");
        var hash = Hash(fullPayload);
        if (!hash.Equals(metadata.Sha256, StringComparison.OrdinalIgnoreCase)) return new(VendorPayloadState.Rejected, string.Empty, "The cached vendor payload hash is invalid.");
        var signature = signatures.Inspect(fullPayload);
        if (!signature.Valid || !Regex.IsMatch(signature.SignerSubject, authorization.PublisherPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)))
            return new(VendorPayloadState.Rejected, string.Empty, "The cached vendor payload signature or publisher is invalid.");
        return new(VendorPayloadState.Verified, fullPayload, "Cached vendor payload hash and Authenticode publisher are valid.");
    }

    private static VendorDownloadResult Reject(string path, string detail)
    {
        try { if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path); } catch { }
        return new(VendorPayloadState.Rejected, string.Empty, detail);
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131_072, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void WriteMetadata(string path, VendorCacheMetadata metadata)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(metadata, VendorCacheJsonContext.Default.VendorCacheMetadata));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VendorCacheMetadata(int SchemaVersion, string PackageId, string Version, string FileName, string Sha256, string PublisherSubject, DateTimeOffset VerifiedAt);

[JsonSerializable(typeof(VendorCacheMetadata))]
internal sealed partial class VendorCacheJsonContext : JsonSerializerContext;
