using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;

namespace AVWorkstationToolkit.CatalogPublisher;

public sealed record ReferenceCatalogPublishOptions(
    string RepositoryRoot,
    string OutputRoot,
    string CatalogVersion,
    long Revision,
    string MinimumAppVersion,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    string SigningKeyId,
    string PrivateKeyPath,
    Uri PublicBaseUri,
    string? PreviousCatalogPath = null,
    IReadOnlyDictionary<string, string>? AdditionalTrustedPublicKeys = null,
    bool AcknowledgeRisk = false);

public sealed record ReferenceCatalogChangeAnalysis(
    int ManufacturersAdded,
    int ManufacturersRemoved,
    int FamiliesAdded,
    int FamiliesRemoved,
    int ExactModelsAdded,
    int ExactModelsRemoved,
    int AliasesAdded,
    int AliasesRemoved,
    int ReferenceSoftwareAdded,
    int ReferenceSoftwareRemoved,
    int RelationsAdded,
    int RelationsRemoved,
    int UnresolvedToVerified,
    IReadOnlyList<string> BroadenedRelationIds,
    IReadOnlyList<string> NarrowedRelationIds,
    IReadOnlyList<string> Warnings)
{
    public bool RequiresAcknowledgement => BroadenedRelationIds.Count + NarrowedRelationIds.Count > 0 || ManufacturersRemoved + FamiliesRemoved + ExactModelsRemoved + AliasesRemoved + ReferenceSoftwareRemoved + RelationsRemoved > 0;
}

public sealed record ReferenceCatalogPublishResult(
    string BundlePath,
    string ChannelMetadataPath,
    string ChannelSignaturePath,
    string ChangeSummaryPath,
    ReferenceCatalogBundleManifest Manifest,
    ReferenceCatalogChangeAnalysis Analysis);

/// <summary>Private-side deterministic catalog packager. It emits descriptive catalog bytes and has no remote publishing capability.</summary>
public sealed class ReferenceCatalogPublisher
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] PayloadOrder =
    [
        ReferenceCatalogBundleNames.Hardware,
        ReferenceCatalogBundleNames.Compatibility,
        ReferenceCatalogBundleNames.Changes
    ];
    private static readonly string[] AuthorityFiles =
    [
        "managed-applications.json",
        "external-applications.json",
        "process-launch-policy.json"
    ];
    private static readonly HashSet<string> ForbiddenAuthorityFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authority", "PackageId", "WinGetId", "DeliveryMode", "DeploymentPolicy", "Provider", "CredentialTarget",
        "Executable", "Installer", "Command", "Arguments", "WorkerAction", "ProcessStartInfo"
    };

    public ReferenceCatalogPublishResult Publish(ReferenceCatalogPublishOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var repositoryRoot = RequireDirectory(options.RepositoryRoot, "repository root");
        var manifestsRoot = RequireDirectory(Path.Combine(repositoryRoot, "manifests"), "manifest root");
        var outputRoot = RequireNewOutputRoot(options.OutputRoot, repositoryRoot, manifestsRoot);
        var privateKeyPath = RequireExternalPrivateKey(options.PrivateKeyPath, repositoryRoot);
        ValidateOptions(options);

        var authorityBefore = SnapshotAuthority(manifestsRoot);
        var hardwareBytes = ReadManifest(Path.Combine(manifestsRoot, ReferenceCatalogBundleNames.Hardware));
        var compatibilityBytes = ReadManifest(Path.Combine(manifestsRoot, ReferenceCatalogBundleNames.Compatibility));
        RejectAuthorityFields(hardwareBytes, ReferenceCatalogBundleNames.Hardware);
        RejectAuthorityFields(compatibilityBytes, ReferenceCatalogBundleNames.Compatibility);
        var hardware = new HardwareIdentityCatalogParser().Parse(StrictUtf8.GetString(hardwareBytes));
        var compatibility = new CompatibilityCatalogParser().Parse(StrictUtf8.GetString(compatibilityBytes));

        using var signingKey = LoadPrivateKey(privateKeyPath);
        var publicKey = signingKey.ExportSubjectPublicKeyInfoPem();
        var trustedKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [options.SigningKeyId] = publicKey
        };
        if (options.AdditionalTrustedPublicKeys is not null)
            foreach (var (keyId, pem) in options.AdditionalTrustedPublicKeys)
                if (!trustedKeys.TryAdd(keyId, pem)) throw new CatalogValidationException($"Trusted catalog key ID '{keyId}' is duplicated.");

        var applicationVersion = File.ReadAllText(Path.Combine(repositoryRoot, "VERSION"), StrictUtf8).Trim();
        var verifier = new ReferenceCatalogBundleVerifier(new(applicationVersion, trustedKeys));
        VerifiedReferenceCatalogBundle? previous = null;
        if (!string.IsNullOrWhiteSpace(options.PreviousCatalogPath))
        {
            previous = verifier.VerifyFile(options.PreviousCatalogPath);
            if (options.Revision <= previous.Manifest.Revision)
                throw new CatalogValidationException("Published revision must be greater than the previous signed catalog revision.");
        }

        var analysis = Analyze(previous, hardware, compatibility);
        if (analysis.RequiresAcknowledgement && !options.AcknowledgeRisk)
            throw new CatalogValidationException("Catalog removals or relation-scope broadening require explicit publisher acknowledgement.");

        var changes = CreateChangeSummary(previous is null, analysis);
        var changeBytes = WriteChanges(changes);
        var counts = Counts(hardware, compatibility);
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [ReferenceCatalogBundleNames.Hardware] = hardwareBytes,
            [ReferenceCatalogBundleNames.Compatibility] = compatibilityBytes,
            [ReferenceCatalogBundleNames.Changes] = changeBytes
        };
        var manifest = new ReferenceCatalogBundleManifest(
            ReferenceCatalogBundleNames.CatalogId, options.CatalogVersion, options.Revision, 1, 1, options.CreatedUtc,
            previous?.Manifest.Revision ?? 0, options.MinimumAppVersion, options.SigningKeyId, counts,
            payloads.ToDictionary(item => item.Key, item => new ReferenceCatalogFileHash(Convert.ToHexString(SHA256.HashData(item.Value))), StringComparer.Ordinal));
        var manifestBytes = WriteManifest(manifest);
        var manifestSignature = Sign(signingKey, manifestBytes);

        var outputParent = Path.GetDirectoryName(outputRoot) ?? throw new IOException("Catalog feed output parent is unavailable.");
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
            var bundleName = $"AVWT-Catalog-{options.CatalogVersion}.avwtcatalog";
            var stagedBundle = Path.Combine(catalogDirectory, bundleName);
            WriteBundle(stagedBundle, payloads, manifestBytes, manifestSignature);
            WriteNewFile(Path.Combine(catalogDirectory, ReferenceCatalogBundleNames.Changes), changeBytes);

            var verifiedBundle = verifier.VerifyFile(stagedBundle);
            if (verifiedBundle.Manifest.Revision != manifest.Revision ||
                !string.Equals(verifiedBundle.Manifest.CatalogVersion, manifest.CatalogVersion, StringComparison.Ordinal) ||
                verifiedBundle.Manifest.PreviousRevision != manifest.PreviousRevision ||
                verifiedBundle.Manifest.Counts != manifest.Counts || verifiedBundle.Hardware.Models.Count != hardware.Models.Count ||
                verifiedBundle.Compatibility.DeviceSoftwareRelations.Count != compatibility.DeviceSoftwareRelations.Count)
                throw new CatalogValidationException("Generated reference catalog failed bundle round-trip validation.");

            var bundleBytes = File.ReadAllBytes(stagedBundle);
            var bundleHash = Convert.ToHexString(SHA256.HashData(bundleBytes));
            var bundleUri = new Uri(options.PublicBaseUri, $"catalogs/{options.Revision}/{bundleName}");
            var channelBytes = WriteChannel(manifest, options.ExpiresUtc, bundleUri, bundleHash);
            var channelSignature = Sign(signingKey, channelBytes);
            var verifiedChannel = new ReferenceCatalogChannelVerifier(trustedKeys).Verify(channelBytes, channelSignature, DateTimeOffset.UtcNow);
            if (verifiedChannel.Revision != manifest.Revision || verifiedChannel.PreviousRevision != manifest.PreviousRevision ||
                verifiedChannel.BundleUri != bundleUri || !string.Equals(verifiedChannel.BundleSha256, bundleHash, StringComparison.Ordinal))
                throw new CatalogValidationException("Generated reference catalog failed channel round-trip validation.");

            WriteNewFile(Path.Combine(stableDirectory, "catalog-channel.json"), channelBytes);
            WriteNewFile(Path.Combine(stableDirectory, "catalog-channel.sig"), channelSignature);
            EnsureAuthorityUnchanged(authorityBefore, manifestsRoot);
            Directory.Move(stagingRoot, outputRoot);

            return new(
                Path.Combine(outputRoot, "catalogs", options.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), bundleName),
                Path.Combine(outputRoot, "stable", "catalog-channel.json"),
                Path.Combine(outputRoot, "stable", "catalog-channel.sig"),
                Path.Combine(outputRoot, "catalogs", options.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), ReferenceCatalogBundleNames.Changes),
                manifest,
                analysis);
        }
        catch
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
            throw;
        }
    }

    private static ReferenceCatalogChangeAnalysis Analyze(VerifiedReferenceCatalogBundle? previous, HardwareIdentityCatalog hardware, SoftwareCompatibilityCatalog compatibility)
    {
        if (previous is null)
            return new(
                Manufacturers(hardware).Count, 0, hardware.Families.Count, 0, hardware.Models.Count, 0, AliasKeys(hardware).Count, 0,
                compatibility.Products.Count, 0, compatibility.DeviceSoftwareRelations.Count, 0, 0, [], [], []);

        var previousManufacturers = Manufacturers(previous.Hardware);
        var currentManufacturers = Manufacturers(hardware);
        var previousFamilies = previous.Hardware.Families.Select(item => item.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentFamilies = hardware.Families.Select(item => item.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previousModels = previous.Hardware.Models.Select(item => item.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentModels = hardware.Models.Select(item => item.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previousAliases = AliasKeys(previous.Hardware);
        var currentAliases = AliasKeys(hardware);
        var previousProducts = previous.Compatibility.Products.Select(item => item.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentProducts = compatibility.Products.Select(item => item.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previousRelations = previous.Compatibility.DeviceSoftwareRelations.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var currentRelations = compatibility.DeviceSoftwareRelations.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var broadened = currentRelations
            .Where(item => previousRelations.TryGetValue(item.Key, out var prior) && IsBroader(prior, item.Value))
            .Select(item => item.Key).Order(StringComparer.Ordinal).ToArray();
        var narrowed = currentRelations
            .Where(item => previousRelations.TryGetValue(item.Key, out var prior) && IsNarrower(prior, item.Value))
            .Select(item => item.Key).Order(StringComparer.Ordinal).ToArray();
        var unresolvedToVerified = hardware.Models.Count(model => model.CoverageState == HardwareCoverageState.VerifiedSoftwareRelationships &&
            previous.Hardware.Models.Any(old => old.Id == model.Id && old.CoverageState == HardwareCoverageState.Unresolved));
        var warnings = new List<string>();
        AddRemovalWarning(warnings, "manufacturers", ExceptCount(previousManufacturers, currentManufacturers));
        AddRemovalWarning(warnings, "families", ExceptCount(previousFamilies, currentFamilies));
        AddRemovalWarning(warnings, "models", ExceptCount(previousModels, currentModels));
        AddRemovalWarning(warnings, "aliases", ExceptCount(previousAliases, currentAliases));
        AddRemovalWarning(warnings, "reference software products", ExceptCount(previousProducts, currentProducts));
        AddRemovalWarning(warnings, "relations", previousRelations.Keys.Except(currentRelations.Keys, StringComparer.OrdinalIgnoreCase).Count());
        if (broadened.Length > 0) warnings.Add($"{broadened.Length} existing relation scope(s) broadened.");
        if (narrowed.Length > 0) warnings.Add($"{narrowed.Length} existing relation scope(s) narrowed.");

        return new(
            ExceptCount(currentManufacturers, previousManufacturers), ExceptCount(previousManufacturers, currentManufacturers),
            ExceptCount(currentFamilies, previousFamilies), ExceptCount(previousFamilies, currentFamilies),
            ExceptCount(currentModels, previousModels), ExceptCount(previousModels, currentModels),
            ExceptCount(currentAliases, previousAliases), ExceptCount(previousAliases, currentAliases),
            ExceptCount(currentProducts, previousProducts), ExceptCount(previousProducts, currentProducts),
            currentRelations.Keys.Except(previousRelations.Keys, StringComparer.OrdinalIgnoreCase).Count(),
            previousRelations.Keys.Except(currentRelations.Keys, StringComparer.OrdinalIgnoreCase).Count(),
            unresolvedToVerified, broadened, narrowed, warnings);
    }

    private static bool IsBroader(DeviceSoftwareRelation previous, DeviceSoftwareRelation current)
    {
        if (!string.Equals(previous.DeviceFamilyId, current.DeviceFamilyId, StringComparison.OrdinalIgnoreCase) || previous.ProductId != current.ProductId ||
            previous.Purpose != current.Purpose || previous.ReleaseFamilyId != current.ReleaseFamilyId || previous.Applicability != current.Applicability)
            return true;
        return current.ExactModelIds.Except(previous.ExactModelIds, StringComparer.OrdinalIgnoreCase).Any() ||
            current.DeviceAliases.Except(previous.DeviceAliases, StringComparer.OrdinalIgnoreCase).Any();
    }

    private static bool IsNarrower(DeviceSoftwareRelation previous, DeviceSoftwareRelation current)
    {
        if (!string.Equals(previous.DeviceFamilyId, current.DeviceFamilyId, StringComparison.OrdinalIgnoreCase) || previous.ProductId != current.ProductId ||
            previous.Purpose != current.Purpose || previous.ReleaseFamilyId != current.ReleaseFamilyId || previous.Applicability != current.Applicability)
            return true;
        return previous.ExactModelIds.Except(current.ExactModelIds, StringComparer.OrdinalIgnoreCase).Any() ||
            previous.DeviceAliases.Except(current.DeviceAliases, StringComparer.OrdinalIgnoreCase).Any();
    }

    private static ReferenceCatalogChangeSummary CreateChangeSummary(bool bootstrap, ReferenceCatalogChangeAnalysis analysis)
    {
        var summary = bootstrap
            ? $"Initial complete reference catalog: {analysis.ManufacturersAdded} manufacturers, {analysis.FamiliesAdded} families, {analysis.ExactModelsAdded} exact models, and {analysis.RelationsAdded} relations."
            : $"Catalog changes: +{analysis.ManufacturersAdded}/-{analysis.ManufacturersRemoved} manufacturers, +{analysis.FamiliesAdded}/-{analysis.FamiliesRemoved} families, +{analysis.ExactModelsAdded}/-{analysis.ExactModelsRemoved} models, +{analysis.RelationsAdded}/-{analysis.RelationsRemoved} relations; {analysis.UnresolvedToVerified} unresolved-to-verified transitions.";
        if (analysis.Warnings.Count > 0) summary += $" Publisher warnings acknowledged: {string.Join(" ", analysis.Warnings)}";
        if (summary.Length > 1024) throw new CatalogValidationException("Generated reference catalog change summary is too long.");
        return new(analysis.ManufacturersAdded, analysis.FamiliesAdded, analysis.ExactModelsAdded, analysis.AliasesAdded,
            analysis.ReferenceSoftwareAdded, analysis.RelationsAdded, analysis.UnresolvedToVerified, summary);
    }

    private static byte[] WriteChanges(ReferenceCatalogChangeSummary changes) => WriteJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteNumber(nameof(changes.ManufacturersAdded), changes.ManufacturersAdded);
        writer.WriteNumber(nameof(changes.FamiliesAdded), changes.FamiliesAdded);
        writer.WriteNumber(nameof(changes.ExactModelsAdded), changes.ExactModelsAdded);
        writer.WriteNumber(nameof(changes.AliasesAdded), changes.AliasesAdded);
        writer.WriteNumber(nameof(changes.ReferenceSoftwareAdded), changes.ReferenceSoftwareAdded);
        writer.WriteNumber(nameof(changes.RelationsAdded), changes.RelationsAdded);
        writer.WriteNumber(nameof(changes.UnresolvedToVerified), changes.UnresolvedToVerified);
        writer.WriteString(nameof(changes.Summary), changes.Summary);
        writer.WriteEndObject();
    });

    private static byte[] WriteManifest(ReferenceCatalogBundleManifest manifest) => WriteJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString(nameof(manifest.CatalogId), manifest.CatalogId);
        writer.WriteString(nameof(manifest.CatalogVersion), manifest.CatalogVersion);
        writer.WriteNumber(nameof(manifest.Revision), manifest.Revision);
        writer.WriteNumber(nameof(manifest.SchemaVersion), manifest.SchemaVersion);
        writer.WriteNumber(nameof(manifest.CompatibilityEpoch), manifest.CompatibilityEpoch);
        writer.WriteString(nameof(manifest.CreatedUtc), manifest.CreatedUtc);
        writer.WriteNumber(nameof(manifest.PreviousRevision), manifest.PreviousRevision);
        writer.WriteString(nameof(manifest.MinimumAppVersion), manifest.MinimumAppVersion);
        writer.WriteString(nameof(manifest.SigningKeyId), manifest.SigningKeyId);
        writer.WritePropertyName(nameof(manifest.Counts));
        writer.WriteStartObject();
        writer.WriteNumber(nameof(manifest.Counts.Manufacturers), manifest.Counts.Manufacturers);
        writer.WriteNumber(nameof(manifest.Counts.Families), manifest.Counts.Families);
        writer.WriteNumber(nameof(manifest.Counts.ExactModels), manifest.Counts.ExactModels);
        writer.WriteNumber(nameof(manifest.Counts.ReferenceSoftwareProducts), manifest.Counts.ReferenceSoftwareProducts);
        writer.WriteNumber(nameof(manifest.Counts.Relations), manifest.Counts.Relations);
        writer.WriteEndObject();
        writer.WritePropertyName(nameof(manifest.Files));
        writer.WriteStartObject();
        foreach (var name in PayloadOrder)
        {
            writer.WritePropertyName(name);
            writer.WriteStartObject();
            writer.WriteString(nameof(ReferenceCatalogFileHash.Sha256), manifest.Files[name].Sha256);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    private static byte[] WriteChannel(ReferenceCatalogBundleManifest manifest, DateTimeOffset expiresUtc, Uri bundleUri, string bundleHash) => WriteJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString(nameof(manifest.CatalogId), manifest.CatalogId);
        writer.WriteNumber(nameof(manifest.SchemaVersion), manifest.SchemaVersion);
        writer.WriteString(nameof(manifest.CatalogVersion), manifest.CatalogVersion);
        writer.WriteNumber(nameof(manifest.Revision), manifest.Revision);
        writer.WriteNumber(nameof(manifest.PreviousRevision), manifest.PreviousRevision);
        writer.WriteString(nameof(manifest.MinimumAppVersion), manifest.MinimumAppVersion);
        writer.WriteString(nameof(manifest.CreatedUtc), manifest.CreatedUtc);
        writer.WriteString("ExpiresUtc", expiresUtc);
        writer.WriteString(nameof(manifest.SigningKeyId), manifest.SigningKeyId);
        writer.WriteString("BundleUri", bundleUri.AbsoluteUri);
        writer.WriteString("BundleSha256", bundleHash);
        writer.WriteEndObject();
    });

    private static byte[] WriteJson(Action<Utf8JsonWriter> write)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory, new JsonWriterOptions { Indented = true })) write(writer);
        return memory.ToArray();
    }

    private static void WriteBundle(string path, IReadOnlyDictionary<string, byte[]> payloads, byte[] manifest, byte[] signature)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in PayloadOrder) WriteEntry(archive, name, payloads[name]);
        WriteEntry(archive, ReferenceCatalogBundleNames.Manifest, manifest);
        WriteEntry(archive, ReferenceCatalogBundleNames.Signature, signature);
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var output = entry.Open();
        output.Write(bytes);
    }

    private static byte[] Sign(ECDsa key, byte[] bytes) =>
        key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private static ECDsa LoadPrivateKey(string path)
    {
        var text = File.ReadAllText(path, StrictUtf8);
        try
        {
            var key = ECDsa.Create();
            key.ImportFromPem(text);
            if (key.KeySize != 256) throw new CatalogValidationException("Reference catalog signing key must be ECDSA P-256.");
            return key;
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            throw new CatalogValidationException($"Reference catalog private key is invalid: {exception.Message}");
        }
    }

    private static byte[] ReadManifest(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > ReferenceCatalogBundleVerifier.MaximumEntryBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new CatalogValidationException($"Publisher input manifest '{info.Name}' is missing, empty, oversized, or a reparse point.");
        var bytes = File.ReadAllBytes(info.FullName);
        _ = StrictUtf8.GetString(bytes);
        return bytes;
    }

    private static void RejectAuthorityFields(byte[] bytes, string name)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 });
        Inspect(document.RootElement);
        return;

        void Inspect(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject())
                {
                    if (ForbiddenAuthorityFields.Contains(property.Name))
                        throw new CatalogValidationException($"Descriptive publisher input '{name}' contains prohibited operational field '{property.Name}'.");
                    Inspect(property.Value);
                }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) Inspect(item);
        }
    }

    private static Dictionary<string, byte[]> SnapshotAuthority(string manifestsRoot) => AuthorityFiles.ToDictionary(
        name => name,
        name => SHA256.HashData(File.ReadAllBytes(Path.Combine(manifestsRoot, name))),
        StringComparer.Ordinal);

    private static void EnsureAuthorityUnchanged(IReadOnlyDictionary<string, byte[]> before, string manifestsRoot)
    {
        foreach (var (name, hash) in before)
        {
            var current = SHA256.HashData(File.ReadAllBytes(Path.Combine(manifestsRoot, name)));
            if (!CryptographicOperations.FixedTimeEquals(hash, current))
                throw new CatalogValidationException($"Operational authority manifest '{name}' changed during catalog publishing.");
        }
    }

    private static ReferenceCatalogCounts Counts(HardwareIdentityCatalog hardware, SoftwareCompatibilityCatalog compatibility) => new(
        Manufacturers(hardware).Count, hardware.Families.Count, hardware.Models.Count, compatibility.Products.Count, compatibility.DeviceSoftwareRelations.Count);

    private static HashSet<string> Manufacturers(HardwareIdentityCatalog catalog) =>
        catalog.Families.Select(item => item.Manufacturer).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> AliasKeys(HardwareIdentityCatalog catalog) => catalog.Families
        .SelectMany(family => family.Aliases.Select(alias => $"F:{family.Id.Value}:{alias.Trim()}"))
        .Concat(catalog.Models.SelectMany(model => model.Aliases.Select(alias => $"M:{model.Id.Value}:{alias.Trim()}")))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static int ExceptCount(HashSet<string> left, HashSet<string> right) => left.Except(right, StringComparer.OrdinalIgnoreCase).Count();

    private static void AddRemovalWarning(ICollection<string> warnings, string subject, int count)
    {
        if (count > 0) warnings.Add($"{count} {subject} removed.");
    }

    private static void ValidateOptions(ReferenceCatalogPublishOptions options)
    {
        if (options.Revision <= 0) throw new CatalogValidationException("Catalog revision must be positive.");
        if (!Version.TryParse(options.CatalogVersion, out _) || options.CatalogVersion.Length > 32 ||
            !Version.TryParse(options.MinimumAppVersion, out _) || options.MinimumAppVersion.Length > 32)
            throw new CatalogValidationException("Catalog and minimum application versions must be bounded numeric versions.");
        if (string.IsNullOrWhiteSpace(options.SigningKeyId) || options.SigningKeyId.Length > 128 || options.SigningKeyId.Any(char.IsControl))
            throw new CatalogValidationException("Catalog signing key ID is invalid.");
        if (options.CreatedUtc == default || options.CreatedUtc.Offset != TimeSpan.Zero || options.ExpiresUtc == default || options.ExpiresUtc.Offset != TimeSpan.Zero ||
            options.ExpiresUtc <= options.CreatedUtc || options.ExpiresUtc - options.CreatedUtc > TimeSpan.FromDays(31))
            throw new CatalogValidationException("Catalog channel timestamps must be UTC and expire within 31 days.");
        if (!options.PublicBaseUri.IsAbsoluteUri || options.PublicBaseUri.Scheme != Uri.UriSchemeHttps || !options.PublicBaseUri.IsDefaultPort ||
            !string.IsNullOrEmpty(options.PublicBaseUri.UserInfo) || string.IsNullOrWhiteSpace(options.PublicBaseUri.IdnHost) || !options.PublicBaseUri.AbsolutePath.EndsWith('/'))
            throw new CatalogValidationException("Catalog public base URI must be an absolute default-port HTTPS directory URI.");
    }

    private static string RequireDirectory(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) throw new IOException($"Catalog publisher {description} must be absolute.");
        var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Catalog publisher {description} is unavailable or is a reparse point.");
        return full;
    }

    private static string RequireNewOutputRoot(string value, string repositoryRoot, string manifestsRoot)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) throw new IOException("Catalog publisher output root must be absolute.");
        var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Directory.Exists(full) || File.Exists(full)) throw new IOException("Catalog publisher output root already exists; immutable feed output is never overwritten.");
        if (IsContained(full, manifestsRoot) || string.Equals(full, repositoryRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Catalog publisher output cannot replace repository or manifest source paths.");
        return full;
    }

    private static string RequireExternalPrivateKey(string value, string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) throw new IOException("Catalog publisher private-key path must be absolute.");
        var full = Path.GetFullPath(value);
        var info = new FileInfo(full);
        if (!info.Exists || info.Length <= 0 || info.Length > 64 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Catalog publisher private key is missing, empty, oversized, or a reparse point.");
        if (IsContained(full, repositoryRoot)) throw new IOException("Catalog publisher private key must remain outside the repository.");
        return full;
    }

    private static bool IsContained(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !Path.IsPathFullyQualified(relative);
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Catalog publisher output cannot use a reparse point.");
    }

    private static void WriteNewFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
