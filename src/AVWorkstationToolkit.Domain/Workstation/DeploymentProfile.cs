using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AVWorkstationToolkit.Domain.Workstation;

/// <summary>An application a deployment profile wants. A catalog ID is preferred; a display name describes anything else.</summary>
public sealed record ProfileApplication(
    string CatalogId,
    string DisplayName,
    string Publisher = "",
    string WinGetId = "",
    bool Required = true,
    string Notes = "")
{
    /// <summary>The identity used to compare profile revisions and to match session items.</summary>
    public string Key => ProfileKeys.For(CatalogId, DisplayName);
}

/// <summary>A descriptive manual task, such as "Verify remote connectivity". It is text only and never executes anything.</summary>
public sealed record ProfileCheck(string Id, string Text);

/// <summary>
/// An intentional, reusable workstation baseline such as a jump PC or a commissioning laptop. The stable
/// <see cref="ProfileId"/> and increasing <see cref="ProfileVersion"/> let a workstation compare what it was provisioned
/// with against a newer revision of the same profile.
/// </summary>
public sealed record DeploymentProfile(
    string ProfileId,
    string Name,
    int ProfileVersion,
    string Description,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<ProfileApplication> Applications,
    IReadOnlyList<ProfileCheck> Checks);

public static partial class ProfileKeys
{
    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SlugPattern();

    public static bool IsSlug(string value) => SlugPattern().IsMatch(value);

    public static string For(string catalogId, string displayName) => catalogId.Length > 0
        ? "catalog:" + catalogId.ToLowerInvariant()
        : "name:" + ApplicationNames.CompactKey(displayName);

    /// <summary>A stable lowercase slug: "Cenero Jump PC" becomes "cenero-jump-pc".</summary>
    public static string Slug(string value, string fallback = "profile")
    {
        var builder = new StringBuilder();
        foreach (var character in ApplicationNames.Clean(value).ToLowerInvariant())
        {
            if (character is (>= 'a' and <= 'z') or (>= '0' and <= '9')) builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
            if (builder.Length == 64) break;
        }
        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? fallback : slug;
    }
}

public sealed record ProfileRevisionDiff(
    string ProfileId,
    int FromVersion,
    int ToVersion,
    IReadOnlyList<string> AddedApplications,
    IReadOnlyList<string> RemovedApplications,
    IReadOnlyList<string> AddedChecks,
    IReadOnlyList<string> RemovedChecks)
{
    public bool HasChanges => AddedApplications.Count + RemovedApplications.Count + AddedChecks.Count + RemovedChecks.Count > 0;

    public string Describe()
    {
        var lines = new List<string> { $"Profile version {FromVersion} → {ToVersion}" };
        lines.AddRange(AddedApplications.Select(name => $"+ {name}"));
        lines.AddRange(RemovedApplications.Select(name => $"- {name}"));
        lines.AddRange(AddedChecks.Select(text => $"+ Check: {text}"));
        lines.AddRange(RemovedChecks.Select(text => $"- Check: {text}"));
        if (!HasChanges) lines.Add("No application or check changes.");
        return string.Join(Environment.NewLine, lines);
    }
}

public static class DeploymentProfileRevision
{
    public static ProfileRevisionDiff Compare(DeploymentProfile older, DeploymentProfile newer)
    {
        ArgumentNullException.ThrowIfNull(older);
        ArgumentNullException.ThrowIfNull(newer);
        return Compare(newer.ProfileId, older.ProfileVersion, newer,
            older.Applications.Select(item => (item.Key, Name(item))).ToArray(),
            older.Checks.Select(item => (item.Id, item.Text)).ToArray());
    }

    internal static ProfileRevisionDiff Compare(
        string profileId,
        int fromVersion,
        DeploymentProfile newer,
        IReadOnlyList<(string Key, string Name)> previousApplications,
        IReadOnlyList<(string Id, string Text)> previousChecks)
    {
        var oldKeys = previousApplications.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var newKeys = newer.Applications.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var oldChecks = previousChecks.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var newChecks = newer.Checks.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        return new ProfileRevisionDiff(
            profileId,
            fromVersion,
            newer.ProfileVersion,
            newer.Applications.Where(item => !oldKeys.Contains(item.Key)).Select(Name).ToArray(),
            previousApplications.Where(item => !newKeys.Contains(item.Key)).Select(item => item.Name).ToArray(),
            newer.Checks.Where(item => !oldChecks.Contains(item.Id)).Select(item => item.Text).ToArray(),
            previousChecks.Where(item => !newChecks.Contains(item.Id)).Select(item => item.Text).ToArray());
    }

    private static string Name(ProfileApplication application) =>
        application.DisplayName.Length > 0 ? application.DisplayName : application.CatalogId;
}

public static class DeploymentProfileCodec
{
    public const int SchemaVersion = 1;
    public const int MaximumBytes = 1024 * 1024;
    public const int MaximumApplications = 1000;
    public const int MaximumChecks = 200;
    public const int MaximumVersion = 1_000_000;

    public static DeploymentProfile Validate(DeploymentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var name = ApplicationNames.Clean(profile.Name);
        if (name.Length is 0 or > 120) throw new WorkstationDocumentException("A profile needs a name of 1 to 120 characters.");
        if (!ProfileKeys.IsSlug(profile.ProfileId)) throw new WorkstationDocumentException("The profile ID must be a lowercase slug such as 'jump-pc'.");
        if (profile.ProfileVersion is < 1 or > MaximumVersion) throw new WorkstationDocumentException($"The profile version must be from 1 to {MaximumVersion}.");
        if (profile.Applications.Count > MaximumApplications) throw new WorkstationDocumentException($"A profile can list at most {MaximumApplications} applications.");
        if (profile.Checks.Count > MaximumChecks) throw new WorkstationDocumentException($"A profile can list at most {MaximumChecks} manual checks.");
        foreach (var application in profile.Applications)
            if (application.CatalogId.Length == 0 && ApplicationNames.CompactKey(application.DisplayName).Length == 0)
                throw new WorkstationDocumentException("Each profile application needs a catalog ID or a display name.");
        var duplicate = profile.Applications.GroupBy(item => item.Key, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new WorkstationDocumentException($"The profile lists '{duplicate.First().DisplayName}{duplicate.First().CatalogId}' more than once.");
        foreach (var check in profile.Checks)
        {
            if (!ProfileKeys.IsSlug(check.Id)) throw new WorkstationDocumentException("Each manual check needs a lowercase slug ID.");
            var text = ApplicationNames.Clean(check.Text);
            if (text.Length is 0 or > 300) throw new WorkstationDocumentException("Each manual check needs text of 1 to 300 characters.");
        }
        if (profile.Checks.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != profile.Checks.Count)
            throw new WorkstationDocumentException("The profile repeats a manual check ID.");
        return profile with { Name = name, Description = ApplicationNames.Clean(profile.Description) };
    }

    public static byte[] Serialize(DeploymentProfile profile)
    {
        profile = Validate(profile);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = WorkstationDocumentReader.Encoder }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("documentType", WorkstationDocumentTypes.Profile);
            writer.WriteString("profileId", profile.ProfileId);
            writer.WriteString("name", profile.Name);
            writer.WriteNumber("profileVersion", profile.ProfileVersion);
            WorkstationInventoryDocumentCodec.WriteOptional(writer, "description", profile.Description);
            writer.WriteString("updatedAtUtc", WorkstationDocumentReader.Format(profile.UpdatedAtUtc));
            writer.WriteStartArray("applications");
            foreach (var application in profile.Applications)
            {
                writer.WriteStartObject();
                WorkstationInventoryDocumentCodec.WriteOptional(writer, "catalogId", application.CatalogId);
                WorkstationInventoryDocumentCodec.WriteOptional(writer, "displayName", application.DisplayName);
                WorkstationInventoryDocumentCodec.WriteOptional(writer, "publisher", application.Publisher);
                WorkstationInventoryDocumentCodec.WriteOptional(writer, "wingetId", application.WinGetId);
                writer.WriteBoolean("required", application.Required);
                WorkstationInventoryDocumentCodec.WriteOptional(writer, "notes", application.Notes);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("checks");
            foreach (var check in profile.Checks)
            {
                writer.WriteStartObject();
                writer.WriteString("id", check.Id);
                writer.WriteString("text", ApplicationNames.Clean(check.Text));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static DeploymentProfile Parse(ReadOnlySpan<byte> payload)
    {
        using var document = WorkstationDocumentReader.Open(payload, MaximumBytes, WorkstationDocumentTypes.Profile, SchemaVersion);
        var root = document.RootElement;
        const string context = "The profile";
        WorkstationDocumentReader.RequireOnly(root, context, "schemaVersion", "documentType", "profileId", "name", "profileVersion",
            "description", "updatedAtUtc", "applications", "checks");
        var applications = WorkstationDocumentReader.Array(root, "applications", context, MaximumApplications, required: true)
            .Select((element, index) =>
            {
                var itemContext = $"Profile application {index + 1}";
                WorkstationDocumentReader.RequireOnly(element, itemContext, "catalogId", "displayName", "publisher", "wingetId", "required", "notes");
                return new ProfileApplication(
                    WorkstationDocumentReader.PackageId(element, "catalogId", itemContext),
                    WorkstationDocumentReader.Text(element, "displayName", itemContext),
                    WorkstationDocumentReader.Text(element, "publisher", itemContext),
                    WorkstationDocumentReader.PackageId(element, "wingetId", itemContext),
                    WorkstationDocumentReader.Boolean(element, "required", itemContext, defaultValue: true),
                    WorkstationDocumentReader.Text(element, "notes", itemContext));
            }).ToArray();
        var checks = WorkstationDocumentReader.Array(root, "checks", context, MaximumChecks)
            .Select((element, index) =>
            {
                var itemContext = $"Profile check {index + 1}";
                WorkstationDocumentReader.RequireOnly(element, itemContext, "id", "text");
                return new ProfileCheck(
                    WorkstationDocumentReader.Text(element, "id", itemContext, required: true, maximumLength: 64),
                    WorkstationDocumentReader.Text(element, "text", itemContext, required: true, maximumLength: 300));
            }).ToArray();
        return Validate(new DeploymentProfile(
            WorkstationDocumentReader.Text(root, "profileId", context, required: true, maximumLength: 64),
            WorkstationDocumentReader.Text(root, "name", context, required: true, maximumLength: 120),
            WorkstationDocumentReader.Integer(root, "profileVersion", context, 1, MaximumVersion),
            WorkstationDocumentReader.Text(root, "description", context),
            WorkstationDocumentReader.Timestamp(root, "updatedAtUtc", context) ?? DateTimeOffset.UnixEpoch,
            applications,
            checks));
    }
}
