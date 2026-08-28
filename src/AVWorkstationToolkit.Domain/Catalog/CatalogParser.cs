using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AVWorkstationToolkit.Domain.Versions;

namespace AVWorkstationToolkit.Domain.Catalog;

public sealed record ManagedPackageInput(
    string Profile,
    string? Name,
    string Id,
    string? Vendor,
    string? Risk,
    string Note,
    string? Deployment,
    string? Maintenance);

public sealed class CatalogParser
{
    private readonly DateOnly verificationAsOf;
    private static readonly Regex PackageIdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9+_.-]{1,127}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    public CatalogParser(DateOnly verificationAsOf) => this.verificationAsOf = verificationAsOf;

    public PackageCatalog ParseExternalCatalog(string json, CatalogAuthority maximumAuthority = CatalogAuthority.OperationalExternal)
    {
        ArgumentNullException.ThrowIfNull(json);
        ExternalCatalogDocument document;
        try { document = JsonSerializer.Deserialize<ExternalCatalogDocument>(json, JsonOptions) ?? throw new CatalogValidationException("External application catalog is empty."); }
        catch (JsonException exception) { throw new CatalogValidationException($"External application catalog JSON is invalid: {exception.Message}"); }
        if (document.SchemaVersion is < 1 or > 3) throw new CatalogValidationException("External application catalog SchemaVersion must be the integer 1, 2, or 3.");
        if (document.Packages is null) throw new CatalogValidationException("External application catalog Packages must be an array.");
        if (maximumAuthority is not (CatalogAuthority.OperationalExternal or CatalogAuthority.AwarenessOnly))
            throw new CatalogValidationException("External catalogs cannot grant managed WinGet authority.");
        var packages = document.Packages.Select((item, index) =>
        {
            var package = NormalizeExternal(item, index, document.SchemaVersion);
            return maximumAuthority == CatalogAuthority.AwarenessOnly ? package with { Authority = CatalogAuthority.AwarenessOnly } : package;
        }).ToArray();
        ValidateRelationships(packages);
        return new PackageCatalog(packages);
    }

    public PackageCatalog NormalizeManagedCatalog(IEnumerable<ManagedPackageInput> input, string forbiddenPattern)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(forbiddenPattern)) throw new CatalogValidationException("Application catalog ForbiddenPattern must not be empty.");
        Regex forbidden;
        try { forbidden = new Regex(forbiddenPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException exception) { throw new CatalogValidationException($"Application catalog ForbiddenPattern is invalid: {exception.Message}"); }
        var packages = input.Select((item, index) => NormalizeManaged(item, index, forbidden)).ToArray();
        return new PackageCatalog(packages);
    }

    private static PackageDefinition NormalizeManaged(ManagedPackageInput raw, int index, Regex forbidden)
    {
        var id = Text(raw.Id, $"Catalog entry {index}.Id", 128);
        var name = Text(string.IsNullOrEmpty(raw.Name) ? raw.Id : raw.Name, $"Catalog entry {index}.Name", 256);
        var vendor = Text(raw.Vendor ?? string.Empty, $"Catalog entry {index}.Vendor", 128, true);
        var note = Text(raw.Note, $"Catalog entry {index}.Note", 1024);
        if (!PackageIdPattern.IsMatch(id)) throw new CatalogValidationException($"Catalog entry {index} has invalid package ID '{id}'.");
        if (forbidden.IsMatch(string.Join(' ', name, id, vendor, note))) throw new CatalogValidationException($"Catalog entry {index} matches the forbidden product policy.");
        var deployment = CatalogTokens.Parse<DeploymentPolicy>(raw.Deployment ?? "Allowlisted", $"Catalog entry {index}.Deployment");
        var maintenance = CatalogTokens.Parse<MaintenancePolicy>(raw.Maintenance ?? "Allowlisted", $"Catalog entry {index}.Maintenance");
        var profile = CatalogTokens.Parse<PackageProfile>(raw.Profile, $"Catalog entry {index}.Profile");
        IReadOnlyList<ApplicationType> types = profile == PackageProfile.Developer ? [ApplicationType.Development] : [ApplicationType.FieldUtility];
        IReadOnlyList<PackageRole> roles = profile switch
        {
            PackageProfile.Developer => [PackageRole.Development],
            PackageProfile.Field => [PackageRole.FieldService],
            _ => [PackageRole.AVEngineer]
        };
        var priority = profile switch
        {
            PackageProfile.Developer => PackagePriority.Dev,
            PackageProfile.Standard or PackageProfile.Field => PackagePriority.Utility,
            _ => PackagePriority.P2
        };
        return new PackageDefinition(
            id, name, vendor, name, note, ProviderKind.WinGet, CatalogAuthority.ManagedWinGet,
            profile, priority,
            CatalogTokens.Parse<PackageRisk>(raw.Risk ?? "None", $"Catalog entry {index}.Risk"), deployment, maintenance,
            DeploymentClass.Managed, CatalogMaintenancePolicy.Managed, VersionRule.Latest,
            VersionCouplingMode.Independent, string.Empty, Lifecycle.Current, types, roles, [],
            [LicensingModel.UnknownCost], ["PUBLIC-DL"], DistributionPolicy.PackageManagerOnly,
            [InstallationForm.WinGet], [SupportedOperatingSystem.Windows], DeliveryMode.None,
            ReleaseMode.None, DetectionMode.WinGet, DetectionVersionPolicy.None, string.Empty, string.Empty,
            string.Empty, string.Empty, [], false, false, false, null, null,
            raw.Risk == "Driver", raw.Risk == "Service", raw.Risk == "Listener", false,
            string.Empty, [priority.ToToken(), "UNKNOWN-COST", "PUBLIC-DL", "VerificationRequired"]);
    }

    private PackageDefinition NormalizeExternal(ExternalPackageRaw raw, int index, int schemaVersion)
    {
        var id = Text(raw.Id, $"External catalog entry {index}.Id", 128);
        var name = Text(raw.Name, $"External catalog entry {index}.Name", 256);
        var note = Text(raw.Note, $"External catalog entry {index}.Note", 1024);
        if (!PackageIdPattern.IsMatch(id)) throw new CatalogValidationException($"External catalog entry {index} has an invalid package identifier.");
        if (schemaVersion >= 3 && raw.Metadata is null) throw new CatalogValidationException($"External catalog entry {index} requires Metadata.");
        var metadata = raw.Metadata ?? new MetadataRaw();
        var deployment = CatalogTokens.Parse<DeploymentPolicy>(raw.Deployment ?? "ManualHold", $"External catalog entry {index}.Deployment");
        var maintenance = CatalogTokens.Parse<MaintenancePolicy>(raw.Maintenance ?? "Hold", $"External catalog entry {index}.Maintenance");
        if (deployment != DeploymentPolicy.ManualHold || maintenance != MaintenancePolicy.Hold)
            throw new CatalogValidationException($"External package {id} must remain on manual deployment and maintenance hold.");
        var vendor = Text(metadata.Vendor, $"External catalog entry {index} Metadata.Vendor", 128);
        var productFamily = Text(metadata.ProductFamily ?? string.Empty, $"External catalog entry {index} Metadata.ProductFamily", 128, true);
        var types = ParseArray<ApplicationType>(metadata.ApplicationType, $"External catalog entry {index} Metadata.ApplicationType");
        var roles = ParseArray<PackageRole>(metadata.Roles ?? [], $"External catalog entry {index} Metadata.Roles", true);
        var priority = CatalogTokens.Parse<PackagePriority>(metadata.Priority ?? "P2", $"External catalog entry {index} Metadata.Priority");
        var deploymentClass = CatalogTokens.Parse<DeploymentClass>(metadata.DeploymentClass ?? (schemaVersion >= 3 ? "AwarenessOnly" : "ManualHandoff"), $"External catalog entry {index} Metadata.DeploymentClass");
        var catalogMaintenance = CatalogTokens.Parse<CatalogMaintenancePolicy>(metadata.MaintenancePolicy ?? "Manual", $"External catalog entry {index} Metadata.MaintenancePolicy");
        var versionRule = CatalogTokens.Parse<VersionRule>(metadata.VersionRule ?? "Unknown", $"External catalog entry {index} Metadata.VersionRule");
        var coupling = metadata.VersionCoupling;
        var couplingMode = CatalogTokens.Parse<VersionCouplingMode>(coupling?.Mode ?? "Independent", $"External catalog entry {index} Metadata.VersionCoupling.Mode");
        var couplingTarget = Text(coupling?.PackageId ?? string.Empty, $"External catalog entry {index} Metadata.VersionCoupling.PackageId", 128, true);
        if (couplingTarget.Length > 0 && !PackageIdPattern.IsMatch(couplingTarget)) throw new CatalogValidationException($"External catalog entry {index} Metadata.VersionCoupling.PackageId is invalid.");
        var lifecycle = CatalogTokens.Parse<Lifecycle>(metadata.CurrentOrLegacy ?? "Unknown", $"External catalog entry {index} Metadata.CurrentOrLegacy");
        var licenses = ParseArray<LicensingModel>(metadata.LicensingModel ?? ["UNKNOWN-COST"], $"External catalog entry {index} Metadata.LicensingModel");
        var downloadAccess = ValidateTokenArray(metadata.DownloadAccess ?? ["UNKNOWN-ACCESS"], AllowedDownloadAccess, $"External catalog entry {index} Metadata.DownloadAccess");
        var distribution = CatalogTokens.Parse<DistributionPolicy>(metadata.DistributionPolicy ?? "Unknown", $"External catalog entry {index} Metadata.DistributionPolicy");
        var installationForms = ParseArray<InstallationForm>(metadata.InstallationForms ?? [], $"External catalog entry {index} Metadata.InstallationForms", true);
        var supportedOs = ParseArray<SupportedOperatingSystem>(metadata.SupportedOS ?? ["Unknown"], $"External catalog entry {index} Metadata.SupportedOS");
        var workflows = ValidateTokenArray(metadata.WorkflowCategories ?? [], AllowedWorkflowCategories, $"External catalog entry {index} Metadata.WorkflowCategories", true);
        ValidateToken(metadata.DownloadDifficulty ?? "HARD", AllowedDownloadDifficulty, $"External catalog entry {index} Metadata.DownloadDifficulty");
        ValidateTokenArray(metadata.Architecture ?? ["Unknown"], AllowedArchitecture, $"External catalog entry {index} Metadata.Architecture");
        ValidateToken(metadata.SideBySideSupported ?? "Unknown", AllowedTriState, $"External catalog entry {index} Metadata.SideBySideSupported");
        ValidateTokenArray(metadata.ValidationMethod ?? ["Unknown"], AllowedValidationMethods, $"External catalog entry {index} Metadata.ValidationMethod");
        ValidateVerification(metadata.Verification, index, verificationAsOf);
        ValidateProvenance(metadata.Provenance, metadata.OfficialProductUri, metadata.OfficialDownloadUri, index);
        var officialProductUri = HttpsUri(metadata.OfficialProductUri, $"External catalog entry {index} Metadata.OfficialProductUri", schemaVersion < 3);
        _ = officialProductUri;
        if (!string.IsNullOrEmpty(metadata.OfficialDownloadUri)) _ = HttpsUri(metadata.OfficialDownloadUri, $"External catalog entry {index} Metadata.OfficialDownloadUri", true);

        var defaultDetectionMode = raw.Detection?.RegistryDisplayNamePattern is not null ? "Registry" : schemaVersion >= 3 ? "None" : "Registry";
        var detectionMode = CatalogTokens.Parse<DetectionMode>(raw.Detection?.Mode ?? defaultDetectionMode, $"External catalog entry {index} Detection.Mode");
        if (detectionMode == DetectionMode.WinGet) throw new CatalogValidationException($"External catalog entry {index} cannot use WinGet detection.");
        var detectionPolicy = DetectionVersionPolicy.None;
        if (detectionMode == DetectionMode.Registry)
        {
            if (raw.Detection?.RegistryDisplayNamePattern is null || raw.Detection.VersionPolicy is null) throw new CatalogValidationException($"External catalog entry {index} requires registry detection fields.");
            ValidateRegex(raw.Detection.RegistryDisplayNamePattern, 1024, null, $"External catalog entry {index} Detection.RegistryDisplayNamePattern");
            if (!string.IsNullOrEmpty(raw.Detection.RegistryVersionPattern)) ValidateRegex(raw.Detection.RegistryVersionPattern, 1024, "Version", $"External catalog entry {index} Detection.RegistryVersionPattern");
            detectionPolicy = CatalogTokens.Parse<DetectionVersionPolicy>(raw.Detection.VersionPolicy, $"External catalog entry {index} Detection.VersionPolicy");
            if (detectionPolicy == DetectionVersionPolicy.None) throw new CatalogValidationException($"External catalog entry {index} registry detection requires AtLeast or SameMajorMinor version policy.");
        }
        else if (raw.Detection is not null && (raw.Detection.RegistryDisplayNamePattern is not null || raw.Detection.RegistryVersionPattern is not null || raw.Detection.VersionPolicy is not null))
        {
            throw new CatalogValidationException($"External catalog entry {index} Detection.None cannot contain registry matching fields.");
        }

        var releaseMode = CatalogTokens.Parse<ReleaseMode>(raw.Release?.Mode ?? "InventoryOnly", $"External catalog entry {index} Release.Mode");
        if (releaseMode == ReleaseMode.None) throw new CatalogValidationException($"External catalog entry {index} requires an external release mode.");
        if (raw.Release is not null && raw.Release.Channel is null) throw new CatalogValidationException($"External catalog entry {index} requires Release.Channel.");
        var releaseChannel = Text(raw.Release?.Channel ?? (schemaVersion >= 3 ? "Catalog awareness" : string.Empty), $"External catalog entry {index} Release.Channel", 64);
        var knownVersion = raw.KnownVersion ?? string.Empty;
        if (releaseMode == ReleaseMode.VendorPage)
        {
            if (raw.Release?.Uri is null || raw.Release.VersionPattern is null || knownVersion.Length == 0) throw new CatalogValidationException($"External catalog entry {index} requires complete vendor release fields.");
            _ = VersionValue.Parse(knownVersion);
            _ = HttpsUri(raw.Release.Uri, $"External catalog entry {index} Release.Uri");
            ValidateRegex(raw.Release.VersionPattern, 1024, "Version", $"External catalog entry {index} Release.VersionPattern");
        }
        else
        {
            if (raw.Release is not null && (raw.Release.Uri is not null || raw.Release.VersionPattern is not null)) throw new CatalogValidationException($"External catalog entry {index} non-page release contains unsupported fields.");
            if (knownVersion.Length > 0) _ = VersionValue.Parse(knownVersion);
        }

        var deliveryMode = CatalogTokens.Parse<DeliveryMode>(raw.Delivery?.Mode ?? "Awareness", $"External catalog entry {index} Delivery.Mode");
        if (raw.Delivery is not null && raw.Delivery.Mode is null) throw new CatalogValidationException($"External catalog entry {index} requires Delivery.Mode.");
        if (deliveryMode == DeliveryMode.None) throw new CatalogValidationException($"External catalog entry {index} requires an external delivery mode.");
        ValidateDelivery(raw.Delivery, deliveryMode, index);
        var parentProviderId = Text(metadata.ParentProviderId ?? string.Empty, $"External catalog entry {index} Metadata.ParentProviderId", 128, true);
        if (parentProviderId.Length > 0 && !PackageIdPattern.IsMatch(parentProviderId)) throw new CatalogValidationException($"External catalog entry {index} Metadata.ParentProviderId is invalid.");
        var authority = deliveryMode == DeliveryMode.Awareness || deploymentClass is DeploymentClass.AwarenessOnly or DeploymentClass.WebOnly or DeploymentClass.ServerOnly or DeploymentClass.Embedded
            ? CatalogAuthority.AwarenessOnly : CatalogAuthority.OperationalExternal;
        var requirements = metadata.Requirements;
        var impact = metadata.SystemImpact;
        return new PackageDefinition(
            id, name, vendor, productFamily, note, ProviderKind.External, authority,
            CatalogTokens.Parse<PackageProfile>(raw.Profile ?? "Optional", $"External catalog entry {index}.Profile"), priority,
            CatalogTokens.Parse<PackageRisk>(raw.Risk ?? "None", $"External catalog entry {index}.Risk"),
            deployment, maintenance,
            deploymentClass, catalogMaintenance, versionRule, couplingMode, couplingTarget, lifecycle, types, roles,
            workflows, licenses, downloadAccess, distribution, installationForms, supportedOs, deliveryMode, releaseMode,
            detectionMode, detectionPolicy, releaseChannel, knownVersion, parentProviderId,
            raw.Delivery?.ProductId ?? string.Empty, raw.Delivery?.AllowedProductIds ?? [],
            requirements?.RequiresVendorAccount, requirements?.RequiresDealerAccount, requirements?.RequiresTraining,
            requirements?.RequiresLicense, requirements?.RequiresSubscription, impact?.InstallsDriver, impact?.InstallsService,
            impact?.OpensListener, impact?.FirmwareUtility, metadata.Notes ?? string.Empty,
            BuildTags(priority, licenses, downloadAccess));
    }

    private static void ValidateRelationships(IReadOnlyList<PackageDefinition> packages)
    {
        var lookup = packages.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            if (package.ParentProviderId.Length > 0)
            {
                if (package.ParentProviderId.Equals(package.Id, StringComparison.OrdinalIgnoreCase)) throw new CatalogValidationException($"External package {package.Id} cannot be its own parent provider.");
                if (!lookup.TryGetValue(package.ParentProviderId, out var parent)) throw new CatalogValidationException($"External package {package.Id} references an unknown parent provider.");
                if (parent.DeliveryMode != DeliveryMode.AuthenticatedSftp || package.DeliveryMode != DeliveryMode.ParentProvider) throw new CatalogValidationException($"External package {package.Id} has an invalid parent provider relationship.");
                if (!parent.AllowedProductIds.Contains(package.DeliveryProductId, StringComparer.Ordinal)) throw new CatalogValidationException($"External package {package.Id} references a product outside its parent provider allowlist.");
                if (package.ReleaseMode == ReleaseMode.ParentCatalog && package.VersionRule != VersionRule.ParentCatalog) throw new CatalogValidationException($"External package {package.Id} ParentCatalog release requires the ParentCatalog version rule.");
            }
            else if (package.DeliveryMode == DeliveryMode.ParentProvider || package.ReleaseMode == ReleaseMode.ParentCatalog)
            {
                throw new CatalogValidationException($"External package {package.Id} requires Metadata.ParentProviderId.");
            }
            if (package.VersionCouplingTargetId.Length > 0 &&
                (package.VersionCouplingTargetId.Equals(package.Id, StringComparison.OrdinalIgnoreCase) || !lookup.ContainsKey(package.VersionCouplingTargetId)))
            {
                throw new CatalogValidationException($"External package {package.Id} has an invalid version-coupling target.");
            }
        }
    }

    private static void ValidateDelivery(DeliveryRaw? raw, DeliveryMode mode, int index)
    {
        if (raw is null && mode != DeliveryMode.Awareness) throw new CatalogValidationException($"External catalog entry {index} requires Delivery.");
        if (raw is null) return;
        var populated = new HashSet<string>(StringComparer.Ordinal);
        if (raw.Uri is not null) populated.Add(nameof(raw.Uri));
        if (raw.DownloadUriPattern is not null) populated.Add(nameof(raw.DownloadUriPattern));
        if (raw.AllowedHosts is not null) populated.Add(nameof(raw.AllowedHosts));
        if (raw.PublisherPattern is not null) populated.Add(nameof(raw.PublisherPattern));
        if (raw.MaxBytes is not null) populated.Add(nameof(raw.MaxBytes));
        if (raw.Host is not null) populated.Add(nameof(raw.Host));
        if (raw.Port is not null) populated.Add(nameof(raw.Port));
        if (raw.CatalogUri is not null) populated.Add(nameof(raw.CatalogUri));
        if (raw.RemoteRoot is not null) populated.Add(nameof(raw.RemoteRoot));
        if (raw.AllowedProductIds is not null) populated.Add(nameof(raw.AllowedProductIds));
        if (raw.ProductId is not null) populated.Add(nameof(raw.ProductId));
        if (raw.RelativePath is not null) populated.Add(nameof(raw.RelativePath));
        if (raw.Sha256 is not null) populated.Add(nameof(raw.Sha256));
        if (raw.PublisherSubject is not null) populated.Add(nameof(raw.PublisherSubject));
        var allowed = mode switch
        {
            DeliveryMode.VendorPage => new HashSet<string>([nameof(raw.Uri)], StringComparer.Ordinal),
            DeliveryMode.DirectDownload => new HashSet<string>([nameof(raw.Uri), nameof(raw.DownloadUriPattern), nameof(raw.AllowedHosts), nameof(raw.PublisherPattern), nameof(raw.MaxBytes)], StringComparer.Ordinal),
            DeliveryMode.AuthenticatedSftp => new HashSet<string>([nameof(raw.Host), nameof(raw.Port), nameof(raw.CatalogUri), nameof(raw.RemoteRoot), nameof(raw.AllowedProductIds), nameof(raw.PublisherPattern), nameof(raw.MaxBytes)], StringComparer.Ordinal),
            DeliveryMode.ParentProvider => new HashSet<string>([nameof(raw.ProductId)], StringComparer.Ordinal),
            DeliveryMode.Bundled => new HashSet<string>([nameof(raw.Uri), nameof(raw.RelativePath), nameof(raw.Sha256), nameof(raw.PublisherSubject)], StringComparer.Ordinal),
            _ => new HashSet<string>(StringComparer.Ordinal)
        };
        if (populated.Except(allowed, StringComparer.Ordinal).Any()) throw new CatalogValidationException($"External catalog entry {index} delivery contains unsupported fields for {mode}.");
        if (!string.IsNullOrEmpty(raw.Uri)) _ = HttpsUri(raw.Uri, $"External catalog entry {index} Delivery.Uri");
        switch (mode)
        {
            case DeliveryMode.VendorPage when string.IsNullOrEmpty(raw.Uri):
                throw new CatalogValidationException($"External catalog entry {index} vendor delivery requires Delivery.Uri.");
            case DeliveryMode.DirectDownload:
                if (raw.Uri is null || raw.DownloadUriPattern is null || raw.AllowedHosts is null || raw.PublisherPattern is null || raw.MaxBytes is null) throw new CatalogValidationException($"External catalog entry {index} requires complete direct-download policy.");
                ValidateRegex(raw.DownloadUriPattern, 2048, "Uri", $"External catalog entry {index} Delivery.DownloadUriPattern");
                ValidateRegex(raw.DownloadUriPattern, 2048, "Version", $"External catalog entry {index} Delivery.DownloadUriPattern");
                ValidateHosts(raw.AllowedHosts, index);
                ValidateRegex(raw.PublisherPattern, 512, null, $"External catalog entry {index} Delivery.PublisherPattern");
                ValidateMaxBytes(raw.MaxBytes.Value, index);
                break;
            case DeliveryMode.AuthenticatedSftp:
                if (raw.Host is null || raw.Port is null || raw.CatalogUri is null || raw.RemoteRoot is null || raw.AllowedProductIds is null || raw.PublisherPattern is null || raw.MaxBytes is null) throw new CatalogValidationException($"External catalog entry {index} requires complete authenticated SFTP policy.");
                ValidateHosts([raw.Host], index);
                if (raw.Port is < 1 or > 65535) throw new CatalogValidationException($"External catalog entry {index} contains an invalid SFTP port.");
                _ = HttpsUri(raw.CatalogUri, $"External catalog entry {index} Delivery.CatalogUri");
                if (!Regex.IsMatch(raw.RemoteRoot, @"^/[A-Za-z0-9._/-]+$") || raw.RemoteRoot.Split('/').Contains("..")) throw new CatalogValidationException($"External catalog entry {index} contains an invalid SFTP remote root.");
                if (raw.AllowedProductIds.Count is < 1 or > 64 || raw.AllowedProductIds.Distinct(StringComparer.Ordinal).Count() != raw.AllowedProductIds.Count || raw.AllowedProductIds.Any(value => !Regex.IsMatch(value, @"^\d{1,8}$"))) throw new CatalogValidationException($"External catalog entry {index} contains an invalid SFTP product allowlist.");
                ValidateRegex(raw.PublisherPattern, 512, null, $"External catalog entry {index} Delivery.PublisherPattern");
                ValidateMaxBytes(raw.MaxBytes.Value, index);
                break;
            case DeliveryMode.ParentProvider when raw.ProductId is null || !Regex.IsMatch(raw.ProductId, @"^\d{1,8}$"):
                throw new CatalogValidationException($"External catalog entry {index} ParentProvider delivery requires a numeric ProductId.");
            case DeliveryMode.Bundled:
                if (raw.RelativePath is null || raw.RelativePath.Length > 512 || raw.Sha256 is null ||
                    !Regex.IsMatch(raw.RelativePath, @"^[A-Za-z0-9._/-]+$") || raw.RelativePath.StartsWith('/') ||
                    raw.RelativePath.Contains('\\') || raw.RelativePath.Contains(':') ||
                    raw.RelativePath.Split('/').Any(value => value is "" or "." or "..") ||
                    !Regex.IsMatch(raw.Sha256, "^[A-Fa-f0-9]{64}$"))
                    throw new CatalogValidationException($"External catalog entry {index} has an unsafe bundled payload policy.");
                break;
        }
    }

    private static void ValidateHosts(IReadOnlyList<string> hosts, int index)
    {
        if (hosts.Count is < 1 or > 16 || hosts.Any(host => !Regex.IsMatch(host, @"^(?=.{1,253}$)(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(?:\.(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?))*$"))) throw new CatalogValidationException($"External catalog entry {index} contains an invalid allowed host.");
    }

    private static void ValidateMaxBytes(long value, int index)
    {
        if (value is < 1_048_576 or > 4_294_967_296) throw new CatalogValidationException($"External catalog entry {index} MaxBytes must be from 1 MiB through 4 GiB.");
    }

    private static string HttpsUri(string? value, string field, bool allowEmpty = false)
    {
        if (string.IsNullOrEmpty(value))
        {
            if (allowEmpty) return string.Empty;
            throw new CatalogValidationException($"{field} must be a bounded absolute HTTPS URI.");
        }
        if (value.Length > 2048 || value.Any(character => character <= ' ' || character == '\u007f') || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.DnsSafeHost) || uri.UserInfo.Length > 0) throw new CatalogValidationException($"{field} must be an absolute HTTPS URI without embedded credentials.");
        return uri.AbsoluteUri;
    }

    private static void ValidateRegex(string value, int maximumLength, string? requiredGroup, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(character => char.IsControl(character) && character is not '\t' and not '\r' and not '\n')) throw new CatalogValidationException($"{field} must be a bounded regular expression.");
        Regex regex;
        try { regex = new Regex(value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException exception) { throw new CatalogValidationException($"{field} is invalid: {exception.Message}"); }
        if (requiredGroup is not null && !regex.GetGroupNames().Contains(requiredGroup, StringComparer.Ordinal)) throw new CatalogValidationException($"{field} must contain a named {requiredGroup} capture group.");
    }

    private static string Text(string? value, string field, int maximumLength, bool allowEmpty = false)
    {
        value ??= string.Empty;
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Length > maximumLength || value != value.Trim() || value.Any(char.IsControl)) throw new CatalogValidationException($"{field} contains invalid text.");
        return value;
    }

    private static IReadOnlyList<T> ParseArray<T>(IReadOnlyList<string>? values, string field, bool allowEmpty = false) where T : struct, Enum
    {
        if (values is null || (!allowEmpty && values.Count == 0) || values.Count > 32) throw new CatalogValidationException($"{field} must contain an approved value set.");
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count) throw new CatalogValidationException($"{field} contains a duplicate value.");
        return values.Select(value => CatalogTokens.Parse<T>(value, field)).ToArray();
    }

    private static IReadOnlyList<string> ValidateTokenArray(IReadOnlyList<string> values, IReadOnlySet<string> allowed, string field, bool allowEmpty = false)
    {
        if ((!allowEmpty && values.Count == 0) || values.Count > 32) throw new CatalogValidationException($"{field} must contain an approved value set.");
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count || values.Any(value => !allowed.Contains(value))) throw new CatalogValidationException($"{field} contains an unsupported or duplicate value.");
        return values.ToArray();
    }

    private static void ValidateToken(string value, IReadOnlySet<string> allowed, string field)
    {
        if (!allowed.Contains(value)) throw new CatalogValidationException($"{field} contains unsupported value '{value}'.");
    }

    private static void ValidateVerification(VerificationRaw? verification, int index, DateOnly verificationAsOf)
    {
        if (verification is null) return;
        if (!string.IsNullOrEmpty(verification.VerifiedOn) &&
            (!DateOnly.TryParseExact(verification.VerifiedOn, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date) || date > verificationAsOf))
            throw new CatalogValidationException($"External catalog entry {index} Metadata.Verification.VerifiedOn is invalid or in the future.");
        ValidateTokenArray(verification.ReviewTriggers ?? [], AllowedReviewTriggers, $"External catalog entry {index} Metadata.Verification.ReviewTriggers", true);
        if (verification.Quarantined == true && string.IsNullOrWhiteSpace(verification.QuarantineReason))
            throw new CatalogValidationException($"External catalog entry {index} quarantined metadata requires a reason.");
        if (verification.QuarantineReason is not null) _ = Text(verification.QuarantineReason, $"External catalog entry {index} Metadata.Verification.QuarantineReason", 512, true);
    }

    private static void ValidateProvenance(ProvenanceRaw? provenance, string? productUri, string? downloadUri, int index)
    {
        if (provenance is null) return;
        var domain = Text(provenance.AuthoritativeDomain ?? string.Empty, $"External catalog entry {index} Metadata.Provenance.AuthoritativeDomain", 253, true).ToLowerInvariant();
        if (domain.Length > 0 && !Regex.IsMatch(domain, @"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$"))
            throw new CatalogValidationException($"External catalog entry {index} Metadata.Provenance.AuthoritativeDomain is invalid.");
        ValidateToken(provenance.SignatureValidation ?? "Unknown", AllowedSignatureValidation, $"External catalog entry {index} Metadata.Provenance.SignatureValidation");
        ValidateToken(provenance.VendorHashAvailability ?? "Unknown", AllowedHashAvailability, $"External catalog entry {index} Metadata.Provenance.VendorHashAvailability");
        ValidateToken(provenance.DownloadStrategy ?? "Unknown", AllowedDownloadStrategy, $"External catalog entry {index} Metadata.Provenance.DownloadStrategy");
        if (provenance.ExpectedPublisher is not null) _ = Text(provenance.ExpectedPublisher, $"External catalog entry {index} Metadata.Provenance.ExpectedPublisher", 256, true);
        foreach (var value in new[] { productUri, downloadUri }.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var host = new Uri(value!).DnsSafeHost;
            if (domain.Length > 0 && !host.Equals(domain, StringComparison.OrdinalIgnoreCase) && !host.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase))
                throw new CatalogValidationException($"External catalog entry {index} official source is outside Metadata.Provenance.AuthoritativeDomain.");
        }
    }

    private static IReadOnlyList<string> BuildTags(PackagePriority priority, IReadOnlyList<LicensingModel> licensing, IReadOnlyList<string> access) =>
        new[] { priority.ToToken() }.Concat(licensing.Select(value => value.ToToken())).Concat(access).Distinct(StringComparer.Ordinal).ToArray();

    private static readonly HashSet<string> AllowedDownloadAccess = new(StringComparer.Ordinal)
    {
        "PUBLIC-DL", "PUBLIC-PAGE", "EMAIL-FORM", "ACCOUNT", "REGISTERED", "DEALER", "TRAINING", "PORTAL", "CONTACT", "LICENSE-PORTAL", "LEGACY-ARCHIVE", "NO-DL", "UNKNOWN-ACCESS"
    };
    private static readonly HashSet<string> AllowedWorkflowCategories = new(StringComparer.Ordinal)
    {
        "NetworkCaptureTiming", "DiscoveryReachability", "ProtocolSocketTesting", "SerialConsole", "RemoteFileTransfer", "UsbConferencing", "VideoEdidSignal", "AudioMeasurementAoIP", "AVoIP", "WindowsDiagnostics", "FilesFirmwareComparison", "ControlApis", "ManufacturerPack", "LegacyService"
    };
    private static readonly HashSet<string> AllowedDownloadDifficulty = new(["EASY", "MODERATE", "RESTRICTED", "HARD"], StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedArchitecture = new(["x86", "x64", "Arm64", "Web", "Embedded", "Server", "Unknown"], StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedTriState = new(["Yes", "No", "Unknown"], StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedValidationMethods = new(["Registry", "WinGet", "OfficialVersionPage", "ParentProviderCatalog", "ManualInventory", "WebPresence", "EmbeddedInterface", "Unknown"], StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedReviewTriggers = new(["DomainChange", "PublisherChange", "ProductDiscontinued", "DownloadStrategyChange", "SignaturePolicyChange"], StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedSignatureValidation = new(["Required", "Optional", "NotApplicable", "Unknown"], StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedHashAvailability = new(["Available", "Unavailable", "Unknown"], StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedDownloadStrategy = new(["PackageManager", "VendorPage", "DirectVendor", "AuthenticatedVendor", "ParentProvider", "Bundled", "None", "Unknown"], StringComparer.Ordinal);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ExternalCatalogDocument { public int SchemaVersion { get; init; } public List<ExternalPackageRaw>? Packages { get; init; } }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ExternalPackageRaw
    {
        public string? Profile { get; init; }
        public string? Name { get; init; }
        public string? Id { get; init; }
        public string? Risk { get; init; }
        public string? Note { get; init; }
        public string? Deployment { get; init; }
        public string? Maintenance { get; init; }
        public string? KnownVersion { get; init; }
        public DetectionRaw? Detection { get; init; }
        public ReleaseRaw? Release { get; init; }
        public DeliveryRaw? Delivery { get; init; }
        public MetadataRaw? Metadata { get; init; }
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class DetectionRaw { public string? Mode { get; init; } public string? RegistryDisplayNamePattern { get; init; } public string? RegistryVersionPattern { get; init; } public string? VersionPolicy { get; init; } }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ReleaseRaw { public string? Mode { get; init; } public string? Uri { get; init; } public string? VersionPattern { get; init; } public string? Channel { get; init; } }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class DeliveryRaw
    {
        public string? Mode { get; init; }
        public string? Uri { get; init; }
        public string? DownloadUriPattern { get; init; }
        public List<string>? AllowedHosts { get; init; }
        public string? PublisherPattern { get; init; }
        public long? MaxBytes { get; init; }
        public string? Host { get; init; }
        public int? Port { get; init; }
        public string? CatalogUri { get; init; }
        public string? RemoteRoot { get; init; }
        public List<string>? AllowedProductIds { get; init; }
        public string? ProductId { get; init; }
        public string? RelativePath { get; init; }
        public string? Sha256 { get; init; }
        public string? PublisherSubject { get; init; }
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class MetadataRaw
    {
        public string? Vendor { get; init; }
        public string? ProductFamily { get; init; }
        public List<string>? ApplicationType { get; init; }
        public string? ParentProviderId { get; init; }
        public string? Priority { get; init; }
        public List<string>? Roles { get; init; }
        public string? DeploymentClass { get; init; }
        public string? MaintenancePolicy { get; init; }
        public string? VersionRule { get; init; }
        public VersionCouplingRaw? VersionCoupling { get; init; }
        public string? CurrentOrLegacy { get; init; }
        public List<string>? LicensingModel { get; init; }
        public List<string>? DownloadAccess { get; init; }
        public string? DownloadDifficulty { get; init; }
        public RequirementsRaw? Requirements { get; init; }
        public SystemImpactRaw? SystemImpact { get; init; }
        public List<string>? Architecture { get; init; }
        public List<string>? SupportedOS { get; init; }
        public string? SideBySideSupported { get; init; }
        public string? OfficialDownloadUri { get; init; }
        public string? OfficialProductUri { get; init; }
        public List<string>? ValidationMethod { get; init; }
        public string? Notes { get; init; }
        public string? DistributionPolicy { get; init; }
        public List<string>? WorkflowCategories { get; init; }
        public List<string>? InstallationForms { get; init; }
        public VerificationRaw? Verification { get; init; }
        public ProvenanceRaw? Provenance { get; init; }
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class VersionCouplingRaw { public string? Mode { get; init; } public string? PackageId { get; init; } public string? Notes { get; init; } }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class RequirementsRaw { public bool? RequiresVendorAccount { get; init; } public bool? RequiresDealerAccount { get; init; } public bool? RequiresTraining { get; init; } public bool? RequiresLicense { get; init; } public bool? RequiresSubscription { get; init; } }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class SystemImpactRaw { public bool? InstallsDriver { get; init; } public bool? InstallsService { get; init; } public bool? OpensListener { get; init; } public bool? FirmwareUtility { get; init; } }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class VerificationRaw { public string? VerifiedOn { get; init; } public List<string>? ReviewTriggers { get; init; } public bool? Quarantined { get; init; } public string? QuarantineReason { get; init; } }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ProvenanceRaw { public string? AuthoritativeDomain { get; init; } public string? ExpectedPublisher { get; init; } public string? SignatureValidation { get; init; } public string? VendorHashAvailability { get; init; } public string? DownloadStrategy { get; init; } }
}
