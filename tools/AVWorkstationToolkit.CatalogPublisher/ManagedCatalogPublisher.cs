using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;
using static AVWorkstationToolkit.CatalogPublisher.CatalogPublisherSupport;

namespace AVWorkstationToolkit.CatalogPublisher;

public sealed record ManagedCatalogPublishOptions(
    string RepositoryRoot,
    string OutputRoot,
    string CatalogVersion,
    long Revision,
    string MinimumAppVersion,
    DateTimeOffset CreatedUtc,
    string SigningKeyId,
    string PrivateKeyPath,
    Uri PublicBaseUri,
    string? PreviousCatalogPath = null);

public sealed record ManagedCatalogPublishResult(
    string BundlePath,
    string ChannelMetadataPath,
    string ChannelSignaturePath,
    ManagedCatalogBundleManifest Manifest);

/// <summary>Offline managed-catalog packager. It cannot publish, download, activate, or configure runtime trust.</summary>
public sealed class ManagedCatalogPublisher
{
    public ManagedCatalogPublishResult Publish(ManagedCatalogPublishOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var repositoryRoot = RequireDirectory(options.RepositoryRoot, "repository root");
        var manifestsRoot = RequireDirectory(Path.Combine(repositoryRoot, "manifests"), "manifest root");
        var outputRoot = RequireNewOutputRoot(options.OutputRoot, repositoryRoot, manifestsRoot);
        var privateKeyPath = RequireExternalPrivateKey(options.PrivateKeyPath, repositoryRoot);
        ValidateOptions(options);

        var payloadPath = Path.Combine(manifestsRoot, ManagedCatalogBundleNames.Payload);
        var payloadBytes = ReadPayload(payloadPath);
        RepositoryCatalogLoader.ManagedCatalogData data;
        try { data = RepositoryCatalogLoader.ParseManagedCatalog(StrictUtf8.GetString(payloadBytes)); }
        catch (InvalidDataException exception) { throw new CatalogValidationException($"Managed catalog publisher input is invalid: {exception.Message}"); }
        if (!string.Equals(data.ForbiddenPattern, ManagedCatalogBundleNames.RequiredForbiddenPattern, StringComparison.Ordinal))
            throw new CatalogValidationException("Managed catalog ForbiddenPattern does not match the required application security policy.");
        var catalog = new CatalogParser(DateOnly.FromDateTime(options.CreatedUtc.UtcDateTime))
            .NormalizeManagedCatalog(data.Packages, data.ForbiddenPattern);

        using var signingKey = LoadPrivateKey(privateKeyPath);
        var trustedKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [options.SigningKeyId] = signingKey.ExportSubjectPublicKeyInfoPem()
        };
        var applicationVersion = File.ReadAllText(Path.Combine(repositoryRoot, "VERSION"), StrictUtf8).Trim();
        var verifier = new ManagedCatalogVerifier(new(applicationVersion, trustedKeys));
        VerifiedManagedCatalogBundle? previous = null;
        if (!string.IsNullOrWhiteSpace(options.PreviousCatalogPath))
        {
            previous = verifier.VerifyFile(options.PreviousCatalogPath);
            if (options.Revision <= previous.Manifest.Revision)
                throw new CatalogValidationException("Published managed catalog revision must be greater than the previous signed revision.");
        }

        var manifest = new ManagedCatalogBundleManifest(
            ManagedCatalogBundleNames.CatalogId,
            options.CatalogVersion,
            options.Revision,
            previous?.Manifest.Revision ?? 0,
            1,
            options.CreatedUtc,
            options.MinimumAppVersion,
            options.SigningKeyId,
            catalog.Items.Count,
            new Dictionary<string, ManagedCatalogFileHash>(StringComparer.Ordinal)
            {
                [ManagedCatalogBundleNames.Payload] = new(Convert.ToHexString(SHA256.HashData(payloadBytes)))
            });
        var manifestBytes = WriteManifest(manifest);
        var manifestSignature = Sign(signingKey, manifestBytes);

        var outputParent = Path.GetDirectoryName(outputRoot) ?? throw new IOException("Managed catalog feed output parent is unavailable.");
        Directory.CreateDirectory(outputParent);
        RejectReparse(outputParent);
        var stagingRoot = Path.Combine(outputParent, $".{Path.GetFileName(outputRoot)}-{Guid.NewGuid():N}.staging");
        Directory.CreateDirectory(stagingRoot);
        try
        {
            var catalogDirectory = Path.Combine(stagingRoot, "catalogs", options.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var stableDirectory = Path.Combine(stagingRoot, "stable");
            Directory.CreateDirectory(catalogDirectory);
            Directory.CreateDirectory(stableDirectory);
            var bundleName = $"{ManagedCatalogBundleNames.BundleNamePrefix}{options.CatalogVersion}{ManagedCatalogBundleNames.BundleExtension}";
            var stagedBundle = Path.Combine(catalogDirectory, bundleName);
            WriteBundle(stagedBundle, payloadBytes, manifestBytes, manifestSignature);

            var verifiedBundle = verifier.VerifyFile(stagedBundle);
            if (!ManifestMatches(verifiedBundle.Manifest, manifest) || verifiedBundle.Catalog.Items.Count != catalog.Items.Count)
                throw new CatalogValidationException("Generated managed catalog failed bundle round-trip validation.");

            var bundleHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(stagedBundle)));
            var bundleUri = new Uri(options.PublicBaseUri, $"catalogs/{options.Revision}/{bundleName}");
            var channelBytes = WriteChannel(manifest, bundleUri, bundleHash);
            var channelSignature = Sign(signingKey, channelBytes);
            var stagedChannel = Path.Combine(stableDirectory, ManagedCatalogBundleNames.ChannelMetadata);
            var stagedChannelSignature = Path.Combine(stableDirectory, ManagedCatalogBundleNames.ChannelSignature);
            WriteNewFile(stagedChannel, channelBytes);
            WriteNewFile(stagedChannelSignature, channelSignature);

            var verifiedPublication = verifier.VerifyPublication(stagedChannel, stagedChannelSignature, stagedBundle, DateTimeOffset.UtcNow);
            if (!ManifestMatches(verifiedPublication.Bundle.Manifest, manifest) || verifiedPublication.Channel.BundleUri != bundleUri)
                throw new CatalogValidationException("Generated managed catalog failed publication round-trip validation.");

            Directory.Move(stagingRoot, outputRoot);
            return new(
                Path.Combine(outputRoot, "catalogs", options.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), bundleName),
                Path.Combine(outputRoot, "stable", ManagedCatalogBundleNames.ChannelMetadata),
                Path.Combine(outputRoot, "stable", ManagedCatalogBundleNames.ChannelSignature),
                manifest);
        }
        catch
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
            throw;
        }
    }

    private static byte[] WriteManifest(ManagedCatalogBundleManifest manifest) => WriteJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString(nameof(manifest.CatalogId), manifest.CatalogId);
        writer.WriteString(nameof(manifest.CatalogVersion), manifest.CatalogVersion);
        writer.WriteNumber(nameof(manifest.Revision), manifest.Revision);
        writer.WriteNumber(nameof(manifest.PreviousRevision), manifest.PreviousRevision);
        writer.WriteNumber(nameof(manifest.SchemaVersion), manifest.SchemaVersion);
        writer.WriteString(nameof(manifest.CreatedUtc), manifest.CreatedUtc);
        writer.WriteString(nameof(manifest.MinimumAppVersion), manifest.MinimumAppVersion);
        writer.WriteString(nameof(manifest.SigningKeyId), manifest.SigningKeyId);
        writer.WriteNumber(nameof(manifest.PackageCount), manifest.PackageCount);
        writer.WritePropertyName(nameof(manifest.Files));
        writer.WriteStartObject();
        writer.WritePropertyName(ManagedCatalogBundleNames.Payload);
        writer.WriteStartObject();
        writer.WriteString(nameof(ManagedCatalogFileHash.Sha256), manifest.Files[ManagedCatalogBundleNames.Payload].Sha256);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    private static byte[] WriteChannel(ManagedCatalogBundleManifest manifest, Uri bundleUri, string bundleHash) => WriteJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString(nameof(manifest.CatalogId), manifest.CatalogId);
        writer.WriteNumber(nameof(manifest.SchemaVersion), manifest.SchemaVersion);
        writer.WriteString(nameof(manifest.CatalogVersion), manifest.CatalogVersion);
        writer.WriteNumber(nameof(manifest.Revision), manifest.Revision);
        writer.WriteNumber(nameof(manifest.PreviousRevision), manifest.PreviousRevision);
        writer.WriteString(nameof(manifest.MinimumAppVersion), manifest.MinimumAppVersion);
        writer.WriteString(nameof(manifest.CreatedUtc), manifest.CreatedUtc);
        writer.WriteString(nameof(manifest.SigningKeyId), manifest.SigningKeyId);
        writer.WriteString("BundleUri", bundleUri.AbsoluteUri);
        writer.WriteString("BundleSha256", bundleHash);
        writer.WriteEndObject();
    });

    private static void WriteBundle(string path, byte[] payload, byte[] manifest, byte[] signature)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, ManagedCatalogBundleNames.Payload, payload);
        WriteEntry(archive, ManagedCatalogBundleNames.Manifest, manifest);
        WriteEntry(archive, ManagedCatalogBundleNames.Signature, signature);
    }

    private static byte[] ReadPayload(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > ManagedCatalogVerifier.MaximumEntryBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new CatalogValidationException("Managed catalog publisher input is missing, empty, oversized, or a reparse point.");
        var bytes = File.ReadAllBytes(info.FullName);
        try { _ = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException exception) { throw new CatalogValidationException($"Managed catalog publisher input is not valid UTF-8: {exception.Message}"); }
        return bytes;
    }

    private static bool ManifestMatches(ManagedCatalogBundleManifest actual, ManagedCatalogBundleManifest expected) =>
        actual.CatalogId == expected.CatalogId && actual.CatalogVersion == expected.CatalogVersion &&
        actual.Revision == expected.Revision && actual.PreviousRevision == expected.PreviousRevision &&
        actual.SchemaVersion == expected.SchemaVersion && actual.CreatedUtc == expected.CreatedUtc &&
        actual.MinimumAppVersion == expected.MinimumAppVersion && actual.SigningKeyId == expected.SigningKeyId &&
        actual.PackageCount == expected.PackageCount &&
        actual.Files[ManagedCatalogBundleNames.Payload].Sha256 == expected.Files[ManagedCatalogBundleNames.Payload].Sha256;

    private static void ValidateOptions(ManagedCatalogPublishOptions options)
    {
        if (options.Revision <= 0) throw new CatalogValidationException("Managed catalog revision must be positive.");
        if (!Version.TryParse(options.CatalogVersion, out _) || options.CatalogVersion.Length > 32 ||
            !Version.TryParse(options.MinimumAppVersion, out _) || options.MinimumAppVersion.Length > 32)
            throw new CatalogValidationException("Managed catalog and minimum application versions must be bounded numeric versions.");
        if (string.IsNullOrWhiteSpace(options.SigningKeyId) || options.SigningKeyId.Length > 128 || options.SigningKeyId.Any(char.IsControl))
            throw new CatalogValidationException("Managed catalog signing key ID is invalid.");
        if (options.CreatedUtc == default || options.CreatedUtc.Offset != TimeSpan.Zero)
            throw new CatalogValidationException("Managed catalog publication timestamp must be UTC.");
        if (!options.PublicBaseUri.IsAbsoluteUri || options.PublicBaseUri.Scheme != Uri.UriSchemeHttps || !options.PublicBaseUri.IsDefaultPort ||
            !string.IsNullOrEmpty(options.PublicBaseUri.UserInfo) || string.IsNullOrWhiteSpace(options.PublicBaseUri.IdnHost) ||
            !string.IsNullOrEmpty(options.PublicBaseUri.Query) || !string.IsNullOrEmpty(options.PublicBaseUri.Fragment) ||
            !options.PublicBaseUri.AbsolutePath.EndsWith('/'))
            throw new CatalogValidationException("Managed catalog public base URI must be an absolute default-port HTTPS directory URI.");
    }
}
