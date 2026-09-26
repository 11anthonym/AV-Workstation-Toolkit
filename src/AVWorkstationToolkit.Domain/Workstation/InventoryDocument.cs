using System.Text.Json;

namespace AVWorkstationToolkit.Domain.Workstation;

/// <summary>
/// What a migration or deployment profile asks for. It is descriptive data only: identity is re-resolved against the
/// local catalog, and installation capability always comes from this workstation's trusted catalog and policy.
/// </summary>
public sealed record DesiredApplicationSpec(
    string DisplayName,
    string Version,
    string Publisher,
    string Architecture,
    string CatalogId,
    string WinGetId,
    string MsiUpgradeCode,
    IReadOnlyList<string> UninstallKeys,
    MigrationRelevance Relevance,
    bool Required = true,
    string Notes = "");

/// <summary>One application read from a portable inventory file, with the raw evidence the source workstation recorded.</summary>
public sealed record InventoryDocumentApplication(
    string DisplayName,
    string DisplayVersion,
    string Publisher,
    InstallScope Scope,
    string Architecture,
    MigrationRelevance Relevance,
    string CatalogId,
    string MsiUpgradeCode,
    IReadOnlyList<UninstallRegistration> Registrations,
    WinGetPackageEvidence? WinGet,
    WinGetCorrelation WinGetCorrelation,
    string RelevanceReason = "",
    bool? Migrate = null)
{
    /// <summary>Whether the migration starts with this item selected: the source technician's choice, or else user-facing applications only.</summary>
    public bool SelectedForMigration => Migrate ?? Relevance == MigrationRelevance.Application;

    public DesiredApplicationSpec ToDesired() => new(
        DisplayName,
        DisplayVersion,
        Publisher,
        Architecture,
        CatalogId,
        WinGet?.Id ?? string.Empty,
        MsiUpgradeCode,
        Registrations.Select(item => item.KeyName).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        Relevance);
}

public sealed record InventoryDocument(
    string Generator,
    DateTimeOffset CapturedAtUtc,
    WorkstationMachine Machine,
    InventorySourceQuality Sources,
    IReadOnlyList<InventoryDocumentApplication> Applications);

/// <summary>
/// The portable, versioned workstation inventory. The file records what a workstation had and the identities that
/// enriched it; importing it never changes what AV Workstation Toolkit is allowed to install.
/// </summary>
public static class WorkstationInventoryDocumentCodec
{
    public const int SchemaVersion = 1;
    public const int MaximumBytes = 8 * 1024 * 1024;
    public const int MaximumApplications = 5000;
    public const int MaximumRegistrationsPerApplication = 32;

    internal static readonly IReadOnlyDictionary<string, InstallScope> Scopes = new Dictionary<string, InstallScope>(StringComparer.Ordinal)
    {
        ["unknown"] = InstallScope.Unknown, ["machine"] = InstallScope.Machine, ["user"] = InstallScope.User
    };
    internal static readonly IReadOnlyDictionary<string, MigrationRelevance> Relevances = new Dictionary<string, MigrationRelevance>(StringComparer.Ordinal)
    {
        ["application"] = MigrationRelevance.Application, ["supportComponent"] = MigrationRelevance.SupportComponent,
        ["systemComponent"] = MigrationRelevance.SystemComponent, ["update"] = MigrationRelevance.Update
    };
    private static readonly IReadOnlyDictionary<string, UninstallHive> Views = new Dictionary<string, UninstallHive>(StringComparer.Ordinal)
    {
        ["HKLM64"] = UninstallHive.Machine64, ["HKLM32"] = UninstallHive.Machine32, ["HKCU"] = UninstallHive.User
    };
    private static readonly IReadOnlyDictionary<string, WinGetCorrelation> Correlations = new Dictionary<string, WinGetCorrelation>(StringComparer.Ordinal)
    {
        ["exportOnly"] = WinGetCorrelation.ExportOnly, ["catalogIdentity"] = WinGetCorrelation.CatalogIdentity,
        ["registeredName"] = WinGetCorrelation.RegisteredName, ["registeredVersion"] = WinGetCorrelation.RegisteredVersion
    };
    private static readonly IReadOnlyDictionary<string, EvidenceQuality> Qualities = new Dictionary<string, EvidenceQuality>(StringComparer.Ordinal)
    {
        ["complete"] = EvidenceQuality.Complete, ["partial"] = EvidenceQuality.Partial, ["unavailable"] = EvidenceQuality.Unavailable
    };

    /// <summary>
    /// Writes every observation. <paramref name="migrate"/> records the source technician's choice for an item; it is
    /// written only where it differs from the default, which includes user-facing applications and leaves out the rest.
    /// </summary>
    public static byte[] Serialize(WorkstationInventory inventory, string generator, Func<ObservedApplication, bool>? migrate = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        if (inventory.Applications.Count > MaximumApplications)
            throw new WorkstationDocumentException($"The inventory has more than {MaximumApplications} applications.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = WorkstationDocumentReader.Encoder }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("documentType", WorkstationDocumentTypes.Inventory);
            writer.WriteString("generator", ApplicationNames.Clean(generator));
            writer.WriteString("capturedAtUtc", WorkstationDocumentReader.Format(inventory.CapturedAtUtc));
            writer.WriteStartObject("machine");
            writer.WriteString("computerName", ApplicationNames.Clean(inventory.Machine.ComputerName));
            writer.WriteString("windowsEdition", ApplicationNames.Clean(inventory.Machine.WindowsEdition));
            writer.WriteString("windowsVersion", ApplicationNames.Clean(inventory.Machine.WindowsVersion));
            writer.WriteString("osBuild", ApplicationNames.Clean(inventory.Machine.OsBuild));
            writer.WriteString("architecture", ApplicationNames.Clean(inventory.Machine.Architecture));
            writer.WriteEndObject();
            writer.WriteStartObject("sources");
            writer.WriteString("registry", Token(Qualities, inventory.Sources.Registry));
            writer.WriteString("winget", Token(Qualities, inventory.Sources.WinGet));
            writer.WriteEndObject();
            writer.WriteStartArray("applications");
            foreach (var application in inventory.Applications)
            {
                writer.WriteStartObject();
                writer.WriteString("displayName", ApplicationNames.Clean(application.DisplayName));
                WriteOptional(writer, "displayVersion", application.DisplayVersion);
                WriteOptional(writer, "publisher", application.Publisher);
                writer.WriteString("scope", Token(Scopes, application.Scope));
                WriteOptional(writer, "architecture", application.Architecture);
                writer.WriteString("relevance", Token(Relevances, application.Relevance));
                WriteOptional(writer, "relevanceReason", application.RelevanceReason);
                if (migrate?.Invoke(application) is { } selected && selected != (application.Relevance == MigrationRelevance.Application))
                    writer.WriteBoolean("migrate", selected);
                WriteOptional(writer, "catalogId", application.CatalogId);
                WriteOptional(writer, "msiUpgradeCode", application.MsiUpgradeCode);
                writer.WriteStartArray("registrations");
                foreach (var registration in application.Registrations.Take(MaximumRegistrationsPerApplication))
                {
                    writer.WriteStartObject();
                    writer.WriteString("view", registration.View);
                    writer.WriteString("key", ApplicationNames.Clean(registration.KeyName));
                    writer.WriteString("displayName", ApplicationNames.Clean(registration.DisplayName));
                    WriteOptional(writer, "displayVersion", registration.DisplayVersion);
                    WriteOptional(writer, "publisher", registration.Publisher);
                    if (registration.SystemComponent) writer.WriteBoolean("systemComponent", true);
                    if (registration.WindowsInstaller) writer.WriteBoolean("windowsInstaller", true);
                    WriteOptional(writer, "parentKey", registration.ParentKeyName);
                    WriteOptional(writer, "releaseType", registration.ReleaseType);
                    WriteOptional(writer, "msiUpgradeCode", registration.MsiUpgradeCode);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                if (application.WinGet is { } winGet)
                {
                    writer.WriteStartObject("winget");
                    writer.WriteString("id", winGet.Id);
                    WriteOptional(writer, "version", winGet.Version);
                    writer.WriteString("correlation", Token(Correlations,
                        application.WinGetCorrelation == WinGetCorrelation.None ? WinGetCorrelation.ExportOnly : application.WinGetCorrelation));
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static InventoryDocument Parse(ReadOnlySpan<byte> payload)
    {
        using var document = WorkstationDocumentReader.Open(payload, MaximumBytes, WorkstationDocumentTypes.Inventory, SchemaVersion);
        var root = document.RootElement;
        const string context = "The inventory";
        WorkstationDocumentReader.RequireOnly(root, context, "schemaVersion", "documentType", "generator", "capturedAtUtc", "machine", "sources", "applications");
        var capturedAt = WorkstationDocumentReader.Timestamp(root, "capturedAtUtc", context, required: true)!.Value;
        var machine = root.TryGetProperty("machine", out var machineElement) && machineElement.ValueKind != JsonValueKind.Null
            ? ReadMachine(machineElement) : WorkstationMachine.Unknown;
        var sources = new InventorySourceQuality(EvidenceQuality.Unavailable, EvidenceQuality.Unavailable, string.Empty);
        if (root.TryGetProperty("sources", out var sourcesElement) && sourcesElement.ValueKind != JsonValueKind.Null)
        {
            WorkstationDocumentReader.RequireOnly(sourcesElement, "The inventory sources", "registry", "winget");
            sources = new InventorySourceQuality(
                WorkstationDocumentReader.Token(sourcesElement, "registry", "The inventory sources", Qualities, EvidenceQuality.Unavailable),
                WorkstationDocumentReader.Token(sourcesElement, "winget", "The inventory sources", Qualities, EvidenceQuality.Unavailable),
                string.Empty);
        }
        var applications = WorkstationDocumentReader.Array(root, "applications", context, MaximumApplications, required: true)
            .Select((element, index) => ReadApplication(element, $"Inventory application {index + 1}"))
            .ToArray();
        return new InventoryDocument(
            WorkstationDocumentReader.Text(root, "generator", context), capturedAt, machine, sources, applications);
    }

    private static WorkstationMachine ReadMachine(JsonElement element)
    {
        const string context = "The inventory machine";
        WorkstationDocumentReader.RequireOnly(element, context, "computerName", "windowsEdition", "windowsVersion", "osBuild", "architecture");
        return new WorkstationMachine(
            WorkstationDocumentReader.Text(element, "computerName", context, maximumLength: 64),
            WorkstationDocumentReader.Text(element, "windowsEdition", context, maximumLength: 128),
            WorkstationDocumentReader.Text(element, "windowsVersion", context, maximumLength: 64),
            WorkstationDocumentReader.Text(element, "osBuild", context, maximumLength: 64),
            WorkstationDocumentReader.Text(element, "architecture", context, maximumLength: 32));
    }

    private static InventoryDocumentApplication ReadApplication(JsonElement element, string context)
    {
        WorkstationDocumentReader.RequireOnly(element, context, "displayName", "displayVersion", "publisher", "scope", "architecture",
            "relevance", "relevanceReason", "migrate", "catalogId", "msiUpgradeCode", "registrations", "winget");
        var registrations = WorkstationDocumentReader.Array(element, "registrations", context, MaximumRegistrationsPerApplication)
            .Select((item, index) => ReadRegistration(item, $"{context} registration {index + 1}"))
            .ToArray();
        WinGetPackageEvidence? winGet = null;
        var correlation = WinGetCorrelation.None;
        if (element.TryGetProperty("winget", out var winGetElement) && winGetElement.ValueKind != JsonValueKind.Null)
        {
            var winGetContext = $"{context} WinGet evidence";
            WorkstationDocumentReader.RequireOnly(winGetElement, winGetContext, "id", "version", "correlation");
            var id = WorkstationDocumentReader.PackageId(winGetElement, "id", winGetContext);
            if (id.Length == 0) throw new WorkstationDocumentException($"{winGetContext} is missing 'id'.");
            winGet = new WinGetPackageEvidence(id, WorkstationDocumentReader.Text(winGetElement, "version", winGetContext, maximumLength: 128));
            correlation = WorkstationDocumentReader.Token(winGetElement, "correlation", winGetContext, Correlations, WinGetCorrelation.ExportOnly);
        }
        return new InventoryDocumentApplication(
            WorkstationDocumentReader.Text(element, "displayName", context, required: true),
            WorkstationDocumentReader.Text(element, "displayVersion", context, maximumLength: 128),
            WorkstationDocumentReader.Text(element, "publisher", context),
            WorkstationDocumentReader.Token(element, "scope", context, Scopes, InstallScope.Unknown),
            WorkstationDocumentReader.Text(element, "architecture", context, maximumLength: 64),
            WorkstationDocumentReader.Token(element, "relevance", context, Relevances, MigrationRelevance.Application),
            WorkstationDocumentReader.PackageId(element, "catalogId", context),
            WorkstationDocumentReader.Guid(element, "msiUpgradeCode", context),
            registrations,
            winGet,
            correlation,
            WorkstationDocumentReader.Text(element, "relevanceReason", context, maximumLength: 200),
            element.TryGetProperty("migrate", out var migrateElement) && migrateElement.ValueKind != JsonValueKind.Null
                ? WorkstationDocumentReader.Boolean(element, "migrate", context)
                : null);
    }

    private static UninstallRegistration ReadRegistration(JsonElement element, string context)
    {
        WorkstationDocumentReader.RequireOnly(element, context, "view", "key", "displayName", "displayVersion", "publisher",
            "systemComponent", "windowsInstaller", "parentKey", "releaseType", "msiUpgradeCode");
        return new UninstallRegistration(
            WorkstationDocumentReader.Token<UninstallHive>(element, "view", context, Views),
            WorkstationDocumentReader.Text(element, "key", context, maximumLength: 256),
            WorkstationDocumentReader.Text(element, "displayName", context, required: true),
            WorkstationDocumentReader.Text(element, "displayVersion", context, maximumLength: 128),
            WorkstationDocumentReader.Text(element, "publisher", context),
            WorkstationDocumentReader.Boolean(element, "systemComponent", context),
            WorkstationDocumentReader.Boolean(element, "windowsInstaller", context),
            WorkstationDocumentReader.Text(element, "parentKey", context, maximumLength: 256),
            WorkstationDocumentReader.Text(element, "releaseType", context, maximumLength: 64),
            WorkstationDocumentReader.Guid(element, "msiUpgradeCode", context));
    }

    internal static string Token<T>(IReadOnlyDictionary<string, T> tokens, T value) where T : struct, Enum =>
        tokens.First(pair => EqualityComparer<T>.Default.Equals(pair.Value, value)).Key;

    internal static void WriteOptional(Utf8JsonWriter writer, string name, string value)
    {
        var text = ApplicationNames.Clean(value);
        if (text.Length > 0) writer.WriteString(name, text);
    }
}
