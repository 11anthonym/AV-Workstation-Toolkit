using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Infrastructure.Windows.Catalog;

public sealed record ManagedCatalogTrustPolicy(
    string ApplicationVersion,
    IReadOnlyDictionary<string, string> TrustedPublicKeys);

public sealed record VerifiedManagedCatalogBundle(
    ManagedCatalogBundleManifest Manifest,
    PackageCatalog Catalog,
    IReadOnlyDictionary<string, byte[]> Files);

public sealed record VerifiedManagedCatalogChannel(
    string CatalogVersion,
    long Revision,
    long PreviousRevision,
    string MinimumAppVersion,
    DateTimeOffset CreatedUtc,
    string SigningKeyId,
    Uri BundleUri,
    string BundleSha256);

public sealed record VerifiedManagedCatalogPublication(
    VerifiedManagedCatalogChannel Channel,
    VerifiedManagedCatalogBundle Bundle);

public sealed class ManagedCatalogRequiresNewerApplicationException(string minimumVersion, long revision = 0)
    : Exception(
        revision > 0
            ? $"Managed catalog revision {revision} requires AV Workstation Toolkit {minimumVersion} or later."
            : $"Managed catalog requires AV Workstation Toolkit {minimumVersion} or later.")
{
    public string MinimumVersion { get; } = minimumVersion;
    public long Revision { get; } = revision;
}

/// <summary>Independently verifies the signed managed catalog contract for publishing and runtime use.</summary>
public sealed class ManagedCatalogVerifier
{
    public const long MaximumBundleBytes = 1024 * 1024;
    public const long MaximumPayloadBytes = 768 * 1024;
    public const long MaximumEntryBytes = 512 * 1024;
    public const int MaximumChannelBytes = 32 * 1024;
    public const int MaximumSignatureBytes = 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions ChannelJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    private readonly string applicationVersion;
    private readonly IReadOnlyDictionary<string, string> trustedPublicKeys;

    public ManagedCatalogVerifier(ManagedCatalogTrustPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!Version.TryParse(policy.ApplicationVersion, out _))
            throw new ArgumentException("Managed catalog application version is invalid.", nameof(policy));
        ArgumentNullException.ThrowIfNull(policy.TrustedPublicKeys);
        trustedPublicKeys = policy.TrustedPublicKeys.ToFrozenDictionary(StringComparer.Ordinal);
        if (trustedPublicKeys.Count == 0)
            throw new ArgumentException("At least one trusted managed-catalog key is required.", nameof(policy));
        RejectReferenceAuthorityOverlap(trustedPublicKeys);
        applicationVersion = policy.ApplicationVersion;
    }

    public VerifiedManagedCatalogBundle VerifyFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(ManagedCatalogBundleNames.BundleExtension, StringComparison.OrdinalIgnoreCase))
            throw new CatalogValidationException($"Managed catalog bundles require the {ManagedCatalogBundleNames.BundleExtension} extension.");
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length <= 0 || info.Length > MaximumBundleBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new CatalogValidationException("Managed catalog bundle is missing, empty, oversized, or a reparse point.");
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, FileOptions.SequentialScan);
        return VerifyArchive(stream);
    }

    public VerifiedManagedCatalogPublication VerifyPublication(
        string channelMetadataPath,
        string channelSignaturePath,
        string bundlePath,
        DateTimeOffset now)
    {
        var metadata = ReadBoundedFile(channelMetadataPath, MaximumChannelBytes, "managed catalog channel metadata");
        var signature = ReadBoundedFile(channelSignaturePath, MaximumSignatureBytes, "managed catalog channel signature");
        var channel = VerifyChannel(metadata, signature, now);
        if (!Path.GetExtension(bundlePath).Equals(ManagedCatalogBundleNames.BundleExtension, StringComparison.OrdinalIgnoreCase))
            throw new CatalogValidationException($"Managed catalog bundles require the {ManagedCatalogBundleNames.BundleExtension} extension.");
        var bundleBytes = ReadBoundedFile(bundlePath, checked((int)MaximumBundleBytes), "managed catalog bundle");
        var actualHash = Convert.ToHexString(SHA256.HashData(bundleBytes));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actualHash), Encoding.ASCII.GetBytes(channel.BundleSha256)))
            throw new CatalogValidationException("Managed catalog channel bundle hash verification failed.");

        using var bundleStream = new MemoryStream(bundleBytes, writable: false);
        var bundle = VerifyArchive(bundleStream);
        if (bundle.Manifest.Revision != channel.Revision || bundle.Manifest.PreviousRevision != channel.PreviousRevision ||
            !string.Equals(bundle.Manifest.CatalogVersion, channel.CatalogVersion, StringComparison.Ordinal) ||
            !string.Equals(bundle.Manifest.MinimumAppVersion, channel.MinimumAppVersion, StringComparison.Ordinal) ||
            !string.Equals(bundle.Manifest.SigningKeyId, channel.SigningKeyId, StringComparison.Ordinal) ||
            bundle.Manifest.CreatedUtc != channel.CreatedUtc)
            throw new CatalogValidationException("Signed managed channel metadata does not match the signed managed catalog bundle.");
        return new(channel, bundle);
    }

    public VerifiedManagedCatalogChannel VerifyChannel(byte[] metadataBytes, byte[] signatureBytes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(metadataBytes);
        ArgumentNullException.ThrowIfNull(signatureBytes);
        if (metadataBytes.Length is <= 0 or > MaximumChannelBytes || signatureBytes.Length is <= 0 or > MaximumSignatureBytes)
            throw new CatalogValidationException("Managed catalog channel metadata or signature size is invalid.");

        string json;
        try { json = StrictUtf8.GetString(metadataBytes); }
        catch (DecoderFallbackException exception) { throw new CatalogValidationException($"Managed catalog channel is not valid UTF-8: {exception.Message}"); }

        ChannelDocument raw;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            RejectDuplicateProperties(document.RootElement, "channel");
            raw = JsonSerializer.Deserialize<ChannelDocument>(json, ChannelJsonOptions)
                ?? throw new CatalogValidationException("Managed catalog channel is empty.");
        }
        catch (JsonException exception)
        {
            throw new CatalogValidationException($"Managed catalog channel JSON is invalid: {exception.Message}");
        }

        if (!string.Equals(raw.CatalogId, ManagedCatalogBundleNames.CatalogId, StringComparison.Ordinal) || raw.SchemaVersion != 1 ||
            raw.Revision <= 0 || raw.PreviousRevision < 0 || raw.PreviousRevision >= raw.Revision ||
            !IsVersion(raw.CatalogVersion) || !IsVersion(raw.MinimumAppVersion) ||
            string.IsNullOrWhiteSpace(raw.SigningKeyId) || raw.SigningKeyId.Length > 128 || raw.SigningKeyId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(raw.BundleSha256) || raw.BundleSha256.Length != 64 || !raw.BundleSha256.All(Uri.IsHexDigit))
            throw new CatalogValidationException("Managed catalog channel metadata is invalid.");
        if (raw.CreatedUtc == default || raw.CreatedUtc.Offset != TimeSpan.Zero || raw.CreatedUtc > now.AddMinutes(5))
            throw new CatalogValidationException("Managed catalog channel publication timestamp is invalid.");
        if (!Uri.TryCreate(raw.BundleUri, UriKind.Absolute, out var bundleUri) || bundleUri.Scheme != Uri.UriSchemeHttps ||
            !bundleUri.IsDefaultPort || !string.IsNullOrEmpty(bundleUri.UserInfo) || string.IsNullOrWhiteSpace(bundleUri.IdnHost) ||
            !string.IsNullOrEmpty(bundleUri.Query) || !string.IsNullOrEmpty(bundleUri.Fragment))
            throw new CatalogValidationException("Managed catalog channel BundleUri is invalid.");
        var expectedSuffix = $"/catalogs/{raw.Revision}/{ManagedCatalogBundleNames.BundleNamePrefix}{raw.CatalogVersion}{ManagedCatalogBundleNames.BundleExtension}";
        if (!bundleUri.AbsolutePath.EndsWith(expectedSuffix, StringComparison.Ordinal))
            throw new CatalogValidationException("Managed catalog channel BundleUri does not identify its immutable revision artifact.");

        CatalogSignatureVerifier.VerifyP256(
            trustedPublicKeys,
            raw.SigningKeyId!,
            metadataBytes,
            signatureBytes,
            "Managed catalog channel");
        return new(raw.CatalogVersion!, raw.Revision, raw.PreviousRevision, raw.MinimumAppVersion!, raw.CreatedUtc,
            raw.SigningKeyId!, bundleUri, raw.BundleSha256!.ToUpperInvariant());
    }

    internal VerifiedManagedCatalogBundle VerifyArchive(Stream stream)
    {
        var files = CatalogBundleReader.ReadExact(
            stream,
            ManagedCatalogBundleNames.BundleFiles,
            MaximumBundleBytes,
            MaximumPayloadBytes,
            MaximumEntryBytes,
            "Managed catalog");
        var manifestBytes = files[ManagedCatalogBundleNames.Manifest];
        var manifest = new ManagedCatalogManifestParser().Parse(Decode(manifestBytes, "manifest"));
        CatalogSignatureVerifier.VerifyP256(
            trustedPublicKeys,
            manifest.SigningKeyId,
            manifestBytes,
            files[ManagedCatalogBundleNames.Signature],
            "Managed catalog");

        var payloadBytes = files[ManagedCatalogBundleNames.Payload];
        var actualHash = Convert.ToHexString(SHA256.HashData(payloadBytes));
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actualHash),
                Encoding.ASCII.GetBytes(manifest.Files[ManagedCatalogBundleNames.Payload].Sha256)))
            throw new CatalogValidationException("Managed catalog payload hash verification failed.");

        RepositoryCatalogLoader.ManagedCatalogData data;
        try { data = RepositoryCatalogLoader.ParseManagedCatalog(Decode(payloadBytes, "payload")); }
        catch (InvalidDataException exception) { throw new CatalogValidationException($"Managed catalog payload is invalid: {exception.Message}"); }
        if (!string.Equals(data.ForbiddenPattern, ManagedCatalogBundleNames.RequiredForbiddenPattern, StringComparison.Ordinal))
            throw new CatalogValidationException("Managed catalog ForbiddenPattern does not match the required application security policy.");
        var catalog = new CatalogParser(DateOnly.FromDateTime(manifest.CreatedUtc.UtcDateTime))
            .NormalizeManagedCatalog(data.Packages, data.ForbiddenPattern);
        if (catalog.Items.Count != manifest.PackageCount)
            throw new CatalogValidationException("Managed catalog PackageCount does not match the validated payload.");
        EnsureCompatibleApplication(manifest.MinimumAppVersion, manifest.Revision);
        return new(manifest, catalog, files.ToDictionary(item => item.Key, item => item.Value.ToArray(), StringComparer.Ordinal));
    }

    private void EnsureCompatibleApplication(string minimumVersion, long revision)
    {
        if (!Version.TryParse(applicationVersion, out var application) || !Version.TryParse(minimumVersion, out var minimum))
            throw new CatalogValidationException("Managed catalog application-version policy is invalid.");
        if (application < minimum)
            throw new ManagedCatalogRequiresNewerApplicationException(minimumVersion, revision);
    }

    private static void RejectReferenceAuthorityOverlap(IReadOnlyDictionary<string, string> managedKeys)
    {
        var referenceKeys = ProductionReferenceCatalogTrustAnchors.All;
        foreach (var (managedId, managedPem) in managedKeys)
        {
            if (referenceKeys.ContainsKey(managedId))
                throw new ArgumentException("Managed catalog trust cannot reuse a reference-catalog signing key ID.", nameof(managedKeys));
            var managedPublicKey = ExportPublicKey(managedPem, "managed");
            foreach (var referencePem in referenceKeys.Values)
                if (CryptographicOperations.FixedTimeEquals(managedPublicKey, ExportPublicKey(referencePem, "reference")))
                    throw new ArgumentException("Managed catalog trust cannot reuse a reference-catalog signing key.", nameof(managedKeys));
        }
    }

    private static byte[] ExportPublicKey(string pem, string description)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            if (key.KeySize != 256) throw new ArgumentException($"The {description} catalog public key must be ECDSA P-256.");
            return key.ExportSubjectPublicKeyInfo();
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            throw new ArgumentException($"The {description} catalog public key is invalid.", nameof(pem), exception);
        }
    }

    private static byte[] ReadBoundedFile(string path, int maximumBytes, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(Path.GetFullPath(path));
        if (!info.Exists || info.Length <= 0 || info.Length > maximumBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new CatalogValidationException($"The {description} is missing, empty, oversized, or a reparse point.");
        return File.ReadAllBytes(info.FullName);
    }

    private static string Decode(byte[] bytes, string description)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException exception) { throw new CatalogValidationException($"Managed catalog {description} is not valid UTF-8: {exception.Message}"); }
    }

    private static bool IsVersion(string? value) => value is not null && Version.TryParse(value, out _) && value.Length <= 32;

    private static void RejectDuplicateProperties(JsonElement element, string description)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new CatalogValidationException($"Managed catalog {description} repeats JSON property '{property.Name}'.");
                RejectDuplicateProperties(property.Value, description);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item, description);
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ChannelDocument
    {
        public string? CatalogId { get; init; }
        public int SchemaVersion { get; init; }
        public string? CatalogVersion { get; init; }
        public long Revision { get; init; }
        public long PreviousRevision { get; init; }
        public string? MinimumAppVersion { get; init; }
        public DateTimeOffset CreatedUtc { get; init; }
        public string? SigningKeyId { get; init; }
        public string? BundleUri { get; init; }
        public string? BundleSha256 { get; init; }
    }
}
