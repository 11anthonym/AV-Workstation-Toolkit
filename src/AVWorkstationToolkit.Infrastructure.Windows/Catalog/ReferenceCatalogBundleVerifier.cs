using System.IO.Compression;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using AVWorkstationToolkit.Application.Compatibility;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Infrastructure.Windows.Catalog;

public sealed record ReferenceCatalogTrustPolicy(
    string ApplicationVersion,
    IReadOnlyDictionary<string, string> TrustedPublicKeys)
{
    public bool IsConfigured => TrustedPublicKeys.Count > 0;
}

public sealed record VerifiedReferenceCatalogBundle(
    ReferenceCatalogBundleManifest Manifest,
    ReferenceCatalogChangeSummary Changes,
    HardwareIdentityCatalog Hardware,
    SoftwareCompatibilityCatalog Compatibility,
    IReadOnlyDictionary<string, byte[]> Files);

public sealed class ReferenceCatalogBundleVerifier
{
    public const long MaximumBundleBytes = 4 * 1024 * 1024;
    public const long MaximumPayloadBytes = 3 * 1024 * 1024;
    public const long MaximumEntryBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ReferenceCatalogTrustPolicy policy;

    public ReferenceCatalogBundleVerifier(ReferenceCatalogTrustPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        this.policy = new(policy.ApplicationVersion,
            policy.TrustedPublicKeys.ToFrozenDictionary(StringComparer.Ordinal));
    }

    public VerifiedReferenceCatalogBundle VerifyFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(".avwtcatalog", StringComparison.OrdinalIgnoreCase))
            throw new CatalogValidationException("Reference catalog imports require the .avwtcatalog extension.");
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length <= 0 || info.Length > MaximumBundleBytes)
            throw new CatalogValidationException("Reference catalog bundle size is invalid.");
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new CatalogValidationException("Reference catalog bundle cannot be a reparse point.");
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, FileOptions.SequentialScan);
        return VerifyArchive(stream);
    }

    internal VerifiedReferenceCatalogBundle VerifyArchive(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!policy.IsConfigured) throw new CatalogValidationException("No trusted reference-catalog signing key is configured.");
        var files = CatalogBundleReader.ReadExact(
            stream,
            ReferenceCatalogBundleNames.BundleFiles,
            MaximumBundleBytes,
            MaximumPayloadBytes,
            MaximumEntryBytes,
            "Reference catalog");

        var manifestBytes = files[ReferenceCatalogBundleNames.Manifest];
        var manifest = new ReferenceCatalogManifestParser().ParseManifest(Decode(manifestBytes, "manifest"));
        CatalogSignatureVerifier.VerifyP256(
            policy.TrustedPublicKeys,
            manifest.SigningKeyId,
            manifestBytes,
            files[ReferenceCatalogBundleNames.Signature],
            "Reference catalog");
        foreach (var payloadName in ReferenceCatalogBundleNames.PayloadFiles)
        {
            var actual = Convert.ToHexString(SHA256.HashData(files[payloadName]));
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(manifest.Files[payloadName].Sha256)))
                throw new CatalogValidationException($"Reference catalog payload hash failed for '{payloadName}'.");
        }

        var hardware = new HardwareIdentityCatalogParser().Parse(Decode(files[ReferenceCatalogBundleNames.Hardware], "hardware identity catalog"));
        var compatibility = new CompatibilityCatalogParser().Parse(Decode(files[ReferenceCatalogBundleNames.Compatibility], "software compatibility catalog"));
        var changes = new ReferenceCatalogManifestParser().ParseChanges(Decode(files[ReferenceCatalogBundleNames.Changes], "change summary"));
        _ = new CompatibilityCatalogQueryService(compatibility, new UnresolvedInstalledVersionEvidenceProvider(), hardware);
        ValidateCounts(manifest.Counts, hardware, compatibility);
        // Compatibility is deliberately checked last. Reaching the specialized exception therefore
        // means the signed snapshot is cryptographically and structurally valid, not corrupt.
        EnsureCompatibleApplication(manifest.MinimumAppVersion, manifest.Revision);
        return new(manifest, changes, hardware, compatibility,
            files.ToDictionary(item => item.Key, item => item.Value.ToArray(), StringComparer.Ordinal));
    }

    public VerifiedReferenceCatalogBundle VerifyDirectory(string directory)
    {
        var root = RequireContainedDirectory(directory);
        var actualFiles = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);
        if (!actualFiles.SetEquals(ReferenceCatalogBundleNames.BundleFiles) || Directory.EnumerateDirectories(root).Any())
            throw new CatalogValidationException("Stored reference catalog contains unexpected files or directories.");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in ReferenceCatalogBundleNames.BundleFiles)
        {
            var path = Path.Combine(root, name);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumEntryBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new CatalogValidationException($"Stored reference catalog entry '{name}' is invalid.");
            files[name] = File.ReadAllBytes(path);
        }
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in files)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                using var destination = entry.Open();
                destination.Write(bytes);
            }
        memory.Position = 0;
        return VerifyArchive(memory);
    }

    private void EnsureCompatibleApplication(string minimumVersion, long revision)
    {
        if (!Version.TryParse(policy.ApplicationVersion, out var application) || !Version.TryParse(minimumVersion, out var minimum))
            throw new CatalogValidationException("Reference catalog application-version policy is invalid.");
        if (application < minimum) throw new ReferenceCatalogRequiresNewerApplicationException(minimumVersion, revision);
    }

    private static void ValidateCounts(ReferenceCatalogCounts counts, HardwareIdentityCatalog hardware, SoftwareCompatibilityCatalog compatibility)
    {
        var manufacturers = hardware.Families.Select(item => item.Manufacturer).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (counts.Manufacturers != manufacturers || counts.Families != hardware.Families.Count || counts.ExactModels != hardware.Models.Count ||
            counts.ReferenceSoftwareProducts != compatibility.Products.Count || counts.Relations != compatibility.DeviceSoftwareRelations.Count)
            throw new CatalogValidationException("Reference catalog manifest counts do not match validated content.");
    }

    private static string Decode(byte[] bytes, string description)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException exception) { throw new CatalogValidationException($"Reference catalog {description} is not valid UTF-8: {exception.Message}"); }
    }

    private static string RequireContainedDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new CatalogValidationException("Stored reference catalog directory is unavailable or is a reparse point.");
        return root;
    }
}

public sealed class ReferenceCatalogRequiresNewerApplicationException(string minimumVersion, long revision = 0)
    : Exception($"Reference catalog requires AV Workstation Toolkit {minimumVersion} or newer.")
{
    public string MinimumVersion { get; } = minimumVersion;
    public long Revision { get; } = revision;
}
