using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Domain.Policies;

namespace AVWorkstationToolkit.Application.Actions;

public enum ManagedRequestAction
{
    Install,
    Update
}

public enum ActionRequestFailure
{
    MalformedJson,
    Oversized,
    MissingField,
    UnknownField,
    DuplicateField,
    WrongType,
    UnsupportedSchema,
    InvalidRequestId,
    UnsupportedAction,
    InvalidPackageId,
    TooManyPackages,
    PackageNotInPlan,
    ActionMismatch,
    PackageNotEligible
}

public sealed class ActionRequestValidationException : Exception
{
    public ActionRequestValidationException(ActionRequestFailure failure, string message, Exception? innerException = null)
        : base(message, innerException) => Failure = failure;

    public ActionRequestFailure Failure { get; }
}

/// <summary>
/// The complete action-request schema. It intentionally has no command, executable,
/// argument, URI, environment, working-directory, or installer fields.
/// </summary>
public sealed record ActionRequest
{
    public ActionRequest(
        int schemaVersion,
        string requestId,
        ManagedRequestAction action,
        IEnumerable<string> packageIds,
        bool riskAcknowledged,
        bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(packageIds);
        SchemaVersion = schemaVersion;
        RequestId = requestId;
        Action = action;
        PackageIds = Array.AsReadOnly(packageIds.ToArray());
        RiskAcknowledged = riskAcknowledged;
        DryRun = dryRun;
    }

    public int SchemaVersion { get; }
    public string RequestId { get; }
    public ManagedRequestAction Action { get; }
    public IReadOnlyList<string> PackageIds { get; }
    public bool RiskAcknowledged { get; }
    public bool DryRun { get; }
}

public sealed record ActionRequestArtifactNames(
    string RequestFileName,
    string ProgressFileName,
    string ResultFileName,
    string CancelFileName,
    string WingetLogFileName)
{
    public static ActionRequestArtifactNames FromRequestId(string requestId)
    {
        ActionRequestRules.ValidateRequestId(requestId);
        return new(
            $"{requestId}.json",
            $"{requestId}.progress.jsonl",
            $"{requestId}.result.json",
            $"{requestId}.cancel",
            $"{requestId}.winget.log");
    }
}

public sealed record AuthorizedActionRequest
{
    internal AuthorizedActionRequest(ActionRequest request, IReadOnlyList<PackageState> packages)
    {
        Request = request;
        Packages = Array.AsReadOnly(packages.ToArray());
    }

    public ActionRequest Request { get; }
    public IReadOnlyList<PackageState> Packages { get; }
}

public static partial class ActionRequestRules
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumPackageCount = 100;
    public const int MaximumPayloadBytes = 65_536;

    private static readonly string[] RequiredProperties =
    [
        "SchemaVersion",
        "RequestId",
        "Action",
        "PackageIds",
        "RiskAcknowledged",
        "DryRun"
    ];

    public static IReadOnlyList<string> Properties => RequiredProperties;

    [GeneratedRegex("^request-[0-9]{8}-[0-9]{6}-[a-f0-9]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex RequestIdPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9+_.-]{1,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageIdPattern();

    public static void ValidateRequestId(string requestId)
    {
        if (requestId is null || !RequestIdPattern().IsMatch(requestId))
        {
            throw new ActionRequestValidationException(ActionRequestFailure.InvalidRequestId, "RequestId is not a valid action-request identifier.");
        }
    }

    public static void ValidatePackageIds(IReadOnlyList<string> packageIds)
    {
        ArgumentNullException.ThrowIfNull(packageIds);
        if (packageIds.Count == 0)
        {
            throw new ActionRequestValidationException(ActionRequestFailure.InvalidPackageId, "At least one package ID is required.");
        }
        if (packageIds.Count > MaximumPackageCount)
        {
            throw new ActionRequestValidationException(ActionRequestFailure.TooManyPackages, $"Action requests are limited to {MaximumPackageCount} package IDs.");
        }

        foreach (var packageId in packageIds)
        {
            if (packageId is null || !PackageIdPattern().IsMatch(packageId))
            {
                throw new ActionRequestValidationException(ActionRequestFailure.InvalidPackageId, "A package ID is empty or does not use the approved package-ID syntax.");
            }
        }
    }

    public static void Validate(ActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SchemaVersion != CurrentSchemaVersion)
        {
            throw new ActionRequestValidationException(ActionRequestFailure.UnsupportedSchema, "The action-request schema version is not supported.");
        }
        if (!Enum.IsDefined(request.Action))
        {
            throw new ActionRequestValidationException(ActionRequestFailure.UnsupportedAction, "The action-request operation is not supported.");
        }
        ValidateRequestId(request.RequestId);
        ValidatePackageIds(request.PackageIds);
    }
}

public sealed class ActionRequestFactory
{
    private readonly TimeProvider timeProvider;
    private readonly Func<string> nonceFactory;

    public ActionRequestFactory(TimeProvider? timeProvider = null, Func<string>? nonceFactory = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.nonceFactory = nonceFactory ?? CreateNonce;
    }

    public ActionRequest Create(
        ManagedRequestAction action,
        IEnumerable<string> packageIds,
        bool riskAcknowledged,
        bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(packageIds);
        if (!Enum.IsDefined(action))
        {
            throw new ActionRequestValidationException(ActionRequestFailure.UnsupportedAction, "The action-request operation is not supported.");
        }

        var ids = packageIds.ToArray();
        ActionRequestRules.ValidatePackageIds(ids);
        var nonce = nonceFactory();
        if (!Regex.IsMatch(nonce, "^[a-f0-9]{8}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException("The request nonce provider returned an invalid value.");
        }

        var timestamp = timeProvider.GetLocalNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var request = new ActionRequest(
            ActionRequestRules.CurrentSchemaVersion,
            $"request-{timestamp}-{nonce}",
            action,
            ids,
            riskAcknowledged,
            dryRun);
        ActionRequestRules.Validate(request);
        return request;
    }

    private static string CreateNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
}

public sealed class ActionRequestCodec
{
    private static readonly HashSet<string> ApprovedProperties = new(ActionRequestRules.Properties, StringComparer.Ordinal);

    public byte[] Serialize(ActionRequest request)
    {
        ActionRequestRules.Validate(request);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default, Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("SchemaVersion", request.SchemaVersion);
            writer.WriteString("RequestId", request.RequestId);
            writer.WriteString("Action", request.Action.ToString());
            writer.WriteStartArray("PackageIds");
            foreach (var packageId in request.PackageIds) writer.WriteStringValue(packageId);
            writer.WriteEndArray();
            writer.WriteBoolean("RiskAcknowledged", request.RiskAcknowledged);
            writer.WriteBoolean("DryRun", request.DryRun);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public ActionRequest Parse(ReadOnlySpan<byte> payload, string? expectedRequestId = null)
    {
        if (payload.Length > ActionRequestRules.MaximumPayloadBytes)
        {
            throw new ActionRequestValidationException(ActionRequestFailure.Oversized, "The action request exceeds the maximum permitted size.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
        }
        catch (JsonException exception)
        {
            throw new ActionRequestValidationException(ActionRequestFailure.MalformedJson, "The action request is not valid JSON.", exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw WrongType("The action request root must be a JSON object.");
            }

            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!ApprovedProperties.Contains(property.Name))
                {
                    throw new ActionRequestValidationException(ActionRequestFailure.UnknownField, $"Unknown action-request property: {property.Name}");
                }
                if (!properties.TryAdd(property.Name, property.Value))
                {
                    throw new ActionRequestValidationException(ActionRequestFailure.DuplicateField, $"Duplicate action-request property: {property.Name}");
                }
            }

            foreach (var required in ActionRequestRules.Properties)
            {
                if (!properties.ContainsKey(required))
                {
                    throw new ActionRequestValidationException(ActionRequestFailure.MissingField, $"Missing required action-request property: {required}");
                }
            }

            var schema = RequireInt32(properties["SchemaVersion"], "SchemaVersion");
            var requestId = RequireString(properties["RequestId"], "RequestId");
            var actionText = RequireString(properties["Action"], "Action");
            if (!Enum.TryParse<ManagedRequestAction>(actionText, true, out var action) || !Enum.IsDefined(action))
            {
                throw new ActionRequestValidationException(ActionRequestFailure.UnsupportedAction, "The action-request operation is not supported.");
            }
            if (properties["PackageIds"].ValueKind != JsonValueKind.Array)
            {
                throw WrongType("PackageIds must be a JSON array.");
            }
            var ids = new List<string>();
            foreach (var item in properties["PackageIds"].EnumerateArray())
            {
                ids.Add(RequireString(item, "PackageIds item"));
            }
            var riskAcknowledged = RequireBoolean(properties["RiskAcknowledged"], "RiskAcknowledged");
            var dryRun = RequireBoolean(properties["DryRun"], "DryRun");

            var request = new ActionRequest(schema, requestId, action, ids, riskAcknowledged, dryRun);
            ActionRequestRules.Validate(request);
            if (expectedRequestId is not null && !string.Equals(request.RequestId, expectedRequestId, StringComparison.Ordinal))
            {
                throw new ActionRequestValidationException(ActionRequestFailure.InvalidRequestId, "RequestId does not match the request filename.");
            }
            return request;
        }
    }

    private static int RequireInt32(JsonElement value, string propertyName)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw WrongType($"{propertyName} must be a JSON integer.");
        }
        return result;
    }

    private static string RequireString(JsonElement value, string propertyName)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw WrongType($"{propertyName} must be a JSON string.");
        }
        return value.GetString()!;
    }

    private static bool RequireBoolean(JsonElement value, string propertyName)
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw WrongType($"{propertyName} must be a JSON Boolean.");
        }
        return value.GetBoolean();
    }

    private static ActionRequestValidationException WrongType(string message) =>
        new(ActionRequestFailure.WrongType, message);
}

/// <summary>
/// Applies existing plan and selection policy to an encoded request. This service
/// cannot grant authority: it only accepts exact managed plan actions already
/// produced by the planner.
/// </summary>
public sealed class ActionRequestAuthorizationService
{
    private readonly SelectionPolicy selectionPolicy;

    public ActionRequestAuthorizationService(SelectionPolicy? selectionPolicy = null) =>
        this.selectionPolicy = selectionPolicy ?? new SelectionPolicy();

    public AuthorizedActionRequest Authorize(ActionRequest request, WorkstationPlan plan)
    {
        ActionRequestRules.Validate(request);
        ArgumentNullException.ThrowIfNull(plan);

        var ids = request.PackageIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var packages = new List<PackageState>(ids.Length);
        var expectedAction = request.Action == ManagedRequestAction.Install ? PackageAction.Install : PackageAction.Update;

        foreach (var id in ids)
        {
            var matches = plan.Packages.Where(item => string.Equals(item.Package.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
            {
                throw new ActionRequestValidationException(ActionRequestFailure.PackageNotInPlan, $"Package ID is not present exactly once in the validated plan: {id}");
            }

            var package = matches[0];
            if (package.Action != expectedAction)
            {
                throw new ActionRequestValidationException(ActionRequestFailure.ActionMismatch, $"Package is not eligible for {request.Action}: {package.Package.Id}");
            }

            var decision = selectionPolicy.Evaluate(package, plan.Reboot, request.RiskAcknowledged);
            if (!decision.IsAllowed)
            {
                throw new ActionRequestValidationException(ActionRequestFailure.PackageNotEligible, $"{package.Package.Id}: {decision.ReasonCode}");
            }
            packages.Add(package);
        }

        return new AuthorizedActionRequest(request, packages);
    }
}
