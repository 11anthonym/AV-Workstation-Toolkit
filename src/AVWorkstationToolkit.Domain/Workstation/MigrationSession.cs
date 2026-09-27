using System.Text.Json;

namespace AVWorkstationToolkit.Domain.Workstation;

public enum MigrationSourceKind { Inventory, Profile }

/// <summary>Where a checklist came from: an imported workstation inventory or an applied workstation template.</summary>
public sealed record MigrationSource(
    MigrationSourceKind Kind,
    string Label,
    DateTimeOffset? CapturedAtUtc = null,
    string ProfileId = "",
    int ProfileVersion = 0);

public sealed record MigrationSessionItem(
    string ItemId,
    DesiredApplicationSpec Application,
    bool Included,
    DateTimeOffset? ConfirmedAtUtc = null,
    InstallAttempt? LastAttempt = null);

/// <summary>A manual checklist task carried from a workstation template. It is descriptive text and never executes.</summary>
public sealed record ChecklistTask(string Id, string Text, DateTimeOffset? DoneAtUtc = null);

/// <summary>
/// A migration or provisioning checklist that survives restarts. It stores descriptive specifications and the
/// technician's choices only; identity is re-resolved against the local catalog every time it is reconciled.
/// </summary>
public sealed record MigrationSession(
    string SessionId,
    MigrationSource Source,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<MigrationSessionItem> Items,
    IReadOnlyList<ChecklistTask> Tasks,
    int RemovedCount = 0,
    int SkippedComponentCount = 0)
{
    public const int MaximumItems = 5000;

    /// <summary>
    /// Starts a checklist from an imported inventory; the imported list is the migration. User-facing applications start
    /// selected unless the source technician deselected them before export. Supporting components are listed but start
    /// outside the migration, and hidden system components and updates remain in the inventory file as evidence only,
    /// unless the source technician selected one.
    /// </summary>
    public static MigrationSession FromInventory(InventoryDocument inventory, string sessionId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var candidates = inventory.Applications
            .Where(item => item.Relevance is MigrationRelevance.Application or MigrationRelevance.SupportComponent || item.SelectedForMigration)
            .ToArray();
        var items = candidates.Select((item, index) => new MigrationSessionItem(
            ItemId(index), item.ToDesired(), item.SelectedForMigration)).ToArray();
        var label = inventory.Machine.ComputerName.Length > 0 ? inventory.Machine.ComputerName : "Imported workstation";
        return new MigrationSession(sessionId, new MigrationSource(MigrationSourceKind.Inventory, label, inventory.CapturedAtUtc),
            now, now, items, [], 0, inventory.Applications.Count - candidates.Length);
    }

    /// <summary>Starts a checklist from a workstation template. Required applications start included; optional ones start excluded.</summary>
    public static MigrationSession FromProfile(DeploymentProfile profile, ApplicationIdentityCatalog identities, string sessionId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(identities);
        var items = profile.Applications.Select((application, index) =>
            new MigrationSessionItem(ItemId(index), ToSpec(application, identities), application.Required)).ToArray();
        return new MigrationSession(sessionId,
            new MigrationSource(MigrationSourceKind.Profile, profile.Name, null, profile.ProfileId, profile.ProfileVersion),
            now, now, items, profile.Checks.Select(check => new ChecklistTask(check.Id, check.Text)).ToArray());
    }

    public IReadOnlyList<DesiredApplication> Resolve(ApplicationIdentityCatalog identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        return Items.Select(item => new DesiredApplication(
            item.ItemId,
            item.Application,
            identities.Resolve(item.Application.CatalogId, item.Application.WinGetId, item.Application.DisplayName),
            item.Included,
            item.ConfirmedAtUtc,
            item.LastAttempt)).ToArray();
    }

    public MigrationSession SetIncluded(string itemId, bool included, DateTimeOffset now) =>
        Update(itemId, now, item => item with { Included = included });

    /// <summary>Removes the item from this checklist for good; unlike excluding it, this can't be undone.</summary>
    public MigrationSession Remove(string itemId, DateTimeOffset now)
    {
        if (Items.All(item => item.ItemId != itemId)) throw new KeyNotFoundException("The checklist item no longer exists.");
        return this with { Items = Items.Where(item => item.ItemId != itemId).ToArray(), RemovedCount = RemovedCount + 1, UpdatedAtUtc = now };
    }

    public MigrationSession Confirm(string itemId, bool confirmed, DateTimeOffset now) =>
        Update(itemId, now, item => item with { ConfirmedAtUtc = confirmed ? now : null });

    public MigrationSession RecordAttempt(string itemId, InstallAttempt attempt, DateTimeOffset now) =>
        Update(itemId, now, item => item with { LastAttempt = attempt });

    public MigrationSession SetTaskDone(string taskId, bool done, DateTimeOffset now)
    {
        if (Tasks.All(task => task.Id != taskId)) throw new KeyNotFoundException("The checklist task no longer exists.");
        return this with
        {
            Tasks = Tasks.Select(task => task.Id == taskId ? task with { DoneAtUtc = done ? now : null } : task).ToArray(),
            UpdatedAtUtc = now
        };
    }

    /// <summary>What adopting a newer revision of this checklist's profile would add and remove.</summary>
    public ProfileRevisionDiff CompareToProfile(DeploymentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (Source.Kind != MigrationSourceKind.Profile || !string.Equals(Source.ProfileId, profile.ProfileId, StringComparison.Ordinal))
            throw new InvalidOperationException("This checklist didn't come from that workstation template.");
        return DeploymentProfileRevision.Compare(profile.ProfileId, Source.ProfileVersion, profile,
            Items.Select(item => (ProfileKeys.For(item.Application.CatalogId, item.Application.DisplayName), item.Application.DisplayName)).ToArray(),
            Tasks.Select(task => (task.Id, task.Text)).ToArray());
    }

    /// <summary>
    /// Moves this checklist to another revision of the same profile. Applications and checks that remain keep their
    /// progress and choices; new ones are added and ones the revision dropped are removed.
    /// </summary>
    public MigrationSession AdoptProfileRevision(DeploymentProfile profile, ApplicationIdentityCatalog identities, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(identities);
        var diff = CompareToProfile(profile);
        var existing = Items.ToDictionary(item => ProfileKeys.For(item.Application.CatalogId, item.Application.DisplayName), StringComparer.Ordinal);
        var next = 0;
        string NextId()
        {
            string id;
            do id = ItemId(next++); while (Items.Any(item => item.ItemId == id));
            return id;
        }
        var items = profile.Applications.Select(application => existing.TryGetValue(application.Key, out var kept)
            ? kept with { Application = ToSpec(application, identities) with { DisplayName = kept.Application.DisplayName.Length > 0 ? kept.Application.DisplayName : application.DisplayName } }
            : new MigrationSessionItem(NextId(), ToSpec(application, identities), application.Required)).ToArray();
        var tasks = profile.Checks.Select(check => Tasks.FirstOrDefault(task => task.Id == check.Id) is { } kept
            ? kept with { Text = check.Text }
            : new ChecklistTask(check.Id, check.Text)).ToArray();
        return this with
        {
            Source = Source with { Label = profile.Name, ProfileVersion = profile.ProfileVersion },
            Items = items,
            Tasks = tasks,
            RemovedCount = RemovedCount + diff.RemovedApplications.Count,
            UpdatedAtUtc = now
        };
    }

    private MigrationSession Update(string itemId, DateTimeOffset now, Func<MigrationSessionItem, MigrationSessionItem> change)
    {
        if (Items.All(item => item.ItemId != itemId)) throw new KeyNotFoundException("The checklist item no longer exists.");
        return this with { Items = Items.Select(item => item.ItemId == itemId ? change(item) : item).ToArray(), UpdatedAtUtc = now };
    }

    private static string ItemId(int index) => $"item-{index + 1:D4}";

    private static DesiredApplicationSpec ToSpec(ProfileApplication application, ApplicationIdentityCatalog identities)
    {
        var known = identities.Find(application.CatalogId);
        return new DesiredApplicationSpec(
            application.DisplayName.Length > 0 ? application.DisplayName : known?.Name ?? application.CatalogId,
            string.Empty,
            application.Publisher.Length > 0 ? application.Publisher : known?.Vendor ?? string.Empty,
            string.Empty,
            application.CatalogId,
            application.WinGetId,
            string.Empty,
            [],
            MigrationRelevance.Application,
            application.Required,
            application.Notes);
    }
}

public static class MigrationSessionCodec
{
    public const int SchemaVersion = 1;
    public const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly IReadOnlyDictionary<string, MigrationSourceKind> Kinds = new Dictionary<string, MigrationSourceKind>(StringComparer.Ordinal)
    {
        ["inventory"] = MigrationSourceKind.Inventory,
        ["profile"] = MigrationSourceKind.Profile
    };

    public static byte[] Serialize(MigrationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Items.Count > MigrationSession.MaximumItems)
            throw new WorkstationDocumentException($"A checklist can hold at most {MigrationSession.MaximumItems} applications.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = WorkstationDocumentReader.Encoder }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("documentType", WorkstationDocumentTypes.Session);
            writer.WriteString("sessionId", session.SessionId);
            writer.WriteString("createdAtUtc", WorkstationDocumentReader.Format(session.CreatedAtUtc));
            writer.WriteString("updatedAtUtc", WorkstationDocumentReader.Format(session.UpdatedAtUtc));
            writer.WriteStartObject("source");
            writer.WriteString("kind", WorkstationInventoryDocumentCodec.Token(Kinds, session.Source.Kind));
            writer.WriteString("label", ApplicationNames.Clean(session.Source.Label));
            if (session.Source.CapturedAtUtc is { } captured) writer.WriteString("capturedAtUtc", WorkstationDocumentReader.Format(captured));
            WorkstationInventoryDocumentCodec.WriteOptional(writer, "profileId", session.Source.ProfileId);
            if (session.Source.ProfileVersion > 0) writer.WriteNumber("profileVersion", session.Source.ProfileVersion);
            writer.WriteEndObject();
            writer.WriteNumber("removedCount", session.RemovedCount);
            writer.WriteNumber("skippedComponentCount", session.SkippedComponentCount);
            writer.WriteStartArray("items");
            foreach (var item in session.Items)
            {
                writer.WriteStartObject();
                writer.WriteString("itemId", item.ItemId);
                writer.WriteBoolean("included", item.Included);
                if (item.ConfirmedAtUtc is { } confirmed) writer.WriteString("confirmedAtUtc", WorkstationDocumentReader.Format(confirmed));
                if (item.LastAttempt is { } attempt)
                {
                    writer.WriteStartObject("lastAttempt");
                    writer.WriteString("atUtc", WorkstationDocumentReader.Format(attempt.AtUtc));
                    writer.WriteBoolean("succeeded", attempt.Succeeded);
                    WorkstationInventoryDocumentCodec.WriteOptional(writer, "message", attempt.Message);
                    writer.WriteEndObject();
                }
                var application = item.Application;
                writer.WriteStartObject("application");
                writer.WriteString("displayName", ApplicationNames.Clean(application.DisplayName));
                WorkstationInventoryDocumentCodec.WriteOptional(writer, "version", application.Version);
                WorkstationInventoryDocumentCodec.WriteOptional(writer, "publisher", application.Publisher);
                WorkstationInventoryDocumentCodec.WriteOptional(writer, "architecture", application.Architecture);
                WorkstationInventoryDocumentCodec.WriteOptional(writer, "catalogId", application.CatalogId);
                WorkstationInventoryDocumentCodec.WriteOptional(writer, "wingetId", application.WinGetId);
                WorkstationInventoryDocumentCodec.WriteOptional(writer, "msiUpgradeCode", application.MsiUpgradeCode);
                if (application.UninstallKeys.Count > 0)
                {
                    writer.WriteStartArray("uninstallKeys");
                    foreach (var key in application.UninstallKeys) writer.WriteStringValue(ApplicationNames.Clean(key));
                    writer.WriteEndArray();
                }
                writer.WriteString("relevance", WorkstationInventoryDocumentCodec.Token(WorkstationInventoryDocumentCodec.Relevances, application.Relevance));
                writer.WriteBoolean("required", application.Required);
                WorkstationInventoryDocumentCodec.WriteOptional(writer, "notes", application.Notes);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("checks");
            foreach (var task in session.Tasks)
            {
                writer.WriteStartObject();
                writer.WriteString("id", task.Id);
                writer.WriteString("text", ApplicationNames.Clean(task.Text));
                if (task.DoneAtUtc is { } done) writer.WriteString("doneAtUtc", WorkstationDocumentReader.Format(done));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static MigrationSession Parse(ReadOnlySpan<byte> payload)
    {
        using var document = WorkstationDocumentReader.Open(payload, MaximumBytes, WorkstationDocumentTypes.Session, SchemaVersion);
        var root = document.RootElement;
        const string context = "The saved migration";
        WorkstationDocumentReader.RequireOnly(root, context, "schemaVersion", "documentType", "sessionId", "createdAtUtc", "updatedAtUtc",
            "source", "removedCount", "skippedComponentCount", "items", "checks");
        if (!root.TryGetProperty("source", out var sourceElement)) throw new WorkstationDocumentException($"{context} is missing 'source'.");
        WorkstationDocumentReader.RequireOnly(sourceElement, "The migration source", "kind", "label", "capturedAtUtc", "profileId", "profileVersion");
        var source = new MigrationSource(
            WorkstationDocumentReader.Token<MigrationSourceKind>(sourceElement, "kind", "The migration source", Kinds),
            WorkstationDocumentReader.Text(sourceElement, "label", "The migration source"),
            WorkstationDocumentReader.Timestamp(sourceElement, "capturedAtUtc", "The migration source"),
            WorkstationDocumentReader.Text(sourceElement, "profileId", "The migration source", maximumLength: 64),
            WorkstationDocumentReader.Integer(sourceElement, "profileVersion", "The migration source", 0, DeploymentProfileCodec.MaximumVersion, 0));
        var items = WorkstationDocumentReader.Array(root, "items", context, MigrationSession.MaximumItems, required: true)
            .Select((element, index) => ReadItem(element, $"Saved migration item {index + 1}"))
            .ToArray();
        if (items.Select(item => item.ItemId).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new WorkstationDocumentException($"{context} repeats an item ID.");
        var tasks = WorkstationDocumentReader.Array(root, "checks", context, DeploymentProfileCodec.MaximumChecks)
            .Select((element, index) =>
            {
                var itemContext = $"Saved migration check {index + 1}";
                WorkstationDocumentReader.RequireOnly(element, itemContext, "id", "text", "doneAtUtc");
                return new ChecklistTask(
                    WorkstationDocumentReader.Text(element, "id", itemContext, required: true, maximumLength: 64),
                    WorkstationDocumentReader.Text(element, "text", itemContext, required: true, maximumLength: 300),
                    WorkstationDocumentReader.Timestamp(element, "doneAtUtc", itemContext));
            }).ToArray();
        return new MigrationSession(
            WorkstationDocumentReader.Text(root, "sessionId", context, required: true, maximumLength: 64),
            source,
            WorkstationDocumentReader.Timestamp(root, "createdAtUtc", context, required: true)!.Value,
            WorkstationDocumentReader.Timestamp(root, "updatedAtUtc", context, required: true)!.Value,
            items,
            tasks,
            WorkstationDocumentReader.Integer(root, "removedCount", context, 0, 1_000_000, 0),
            WorkstationDocumentReader.Integer(root, "skippedComponentCount", context, 0, 1_000_000, 0));
    }

    private static MigrationSessionItem ReadItem(JsonElement element, string context)
    {
        WorkstationDocumentReader.RequireOnly(element, context, "itemId", "included", "confirmedAtUtc", "lastAttempt", "application");
        InstallAttempt? attempt = null;
        if (element.TryGetProperty("lastAttempt", out var attemptElement) && attemptElement.ValueKind != JsonValueKind.Null)
        {
            var attemptContext = $"{context} last attempt";
            WorkstationDocumentReader.RequireOnly(attemptElement, attemptContext, "atUtc", "succeeded", "message");
            attempt = new InstallAttempt(
                WorkstationDocumentReader.Timestamp(attemptElement, "atUtc", attemptContext, required: true)!.Value,
                WorkstationDocumentReader.Boolean(attemptElement, "succeeded", attemptContext),
                WorkstationDocumentReader.Text(attemptElement, "message", attemptContext));
        }
        if (!element.TryGetProperty("application", out var applicationElement))
            throw new WorkstationDocumentException($"{context} is missing 'application'.");
        var applicationContext = $"{context} application";
        WorkstationDocumentReader.RequireOnly(applicationElement, applicationContext, "displayName", "version", "publisher", "architecture",
            "catalogId", "wingetId", "msiUpgradeCode", "uninstallKeys", "relevance", "required", "notes");
        var application = new DesiredApplicationSpec(
            WorkstationDocumentReader.Text(applicationElement, "displayName", applicationContext, required: true),
            WorkstationDocumentReader.Text(applicationElement, "version", applicationContext, maximumLength: 128),
            WorkstationDocumentReader.Text(applicationElement, "publisher", applicationContext),
            WorkstationDocumentReader.Text(applicationElement, "architecture", applicationContext, maximumLength: 64),
            WorkstationDocumentReader.PackageId(applicationElement, "catalogId", applicationContext),
            WorkstationDocumentReader.PackageId(applicationElement, "wingetId", applicationContext),
            WorkstationDocumentReader.Guid(applicationElement, "msiUpgradeCode", applicationContext),
            WorkstationDocumentReader.TextArray(applicationElement, "uninstallKeys", applicationContext, WorkstationInventoryDocumentCodec.MaximumRegistrationsPerApplication),
            WorkstationDocumentReader.Token(applicationElement, "relevance", applicationContext, WorkstationInventoryDocumentCodec.Relevances, MigrationRelevance.Application),
            WorkstationDocumentReader.Boolean(applicationElement, "required", applicationContext, defaultValue: true),
            WorkstationDocumentReader.Text(applicationElement, "notes", applicationContext));
        return new MigrationSessionItem(
            WorkstationDocumentReader.Text(element, "itemId", context, required: true, maximumLength: 32),
            application,
            WorkstationDocumentReader.Boolean(element, "included", context, defaultValue: true),
            WorkstationDocumentReader.Timestamp(element, "confirmedAtUtc", context),
            attempt);
    }
}
