using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AVWorkstationToolkit.Application.Diagnostics;

namespace AVWorkstationToolkit.Application.Actions;

public static class ActionProtocolLimits
{
    public const int MaximumProgressBytes = 20 * 1024 * 1024;
    public const int MaximumProgressRecordBytes = 16 * 1024;
    public const int MaximumResultBytes = 2 * 1024 * 1024;
    public const int MaximumCancellationBytes = 4 * 1024;
    public const int MaximumMessageCharacters = 4_014;
    public const int MaximumStageCharacters = 128;
    public const int MaximumNameCharacters = 512;
    public const int MaximumComputerCharacters = 256;
    public const int MaximumArgumentCharacters = 2_048;
    public const int MaximumArgumentsPerPackage = 64;
}

public sealed class ActionProgressCodec
{
    private static readonly string[] Properties = ["Timestamp", "Level", "Stage", "PackageId", "Message"];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public byte[] Serialize(ActionProgressRecord record, ActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(request);
        ActionRequestRules.Validate(request);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("Timestamp", record.Timestamp.ToString("o", CultureInfo.InvariantCulture));
            writer.WriteString("Level", record.Level.ToString());
            writer.WriteString("Stage", record.Stage);
            writer.WriteString("PackageId", record.PackageId);
            writer.WriteString("Message", record.Message);
            writer.WriteEndObject();
        }

        var payload = stream.ToArray();
        _ = ParseRecord(payload, request);
        return payload;
    }

    public ActionProgressParseBatch ParseIncremental(
        ReadOnlySpan<byte> bytes,
        ActionRequest request,
        ActionProgressParseState? previousState = null,
        bool flush = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        ActionRequestRules.Validate(request);
        var state = previousState ?? ActionProgressParseState.Empty;
        if (state.BytesConsumed > ActionProtocolLimits.MaximumProgressBytes - bytes.Length)
            throw Oversized("The progress artifact exceeds the 20 MiB display limit.");

        var combinedLength = checked(state.PendingBytes.Length + bytes.Length);
        if (combinedLength > ActionProtocolLimits.MaximumProgressRecordBytes && !ContainsNewline(state.PendingBytes, bytes))
            throw Oversized("An incomplete progress record exceeds the per-record limit.");

        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(combinedLength, 1));
        try
        {
            state.PendingBytes.Span.CopyTo(rented);
            bytes.CopyTo(rented.AsSpan(state.PendingBytes.Length));
            var records = new List<ActionProgressRecord>();
            var issues = new List<ActionProgressIssue>();
            var start = 0;
            var recordNumber = state.CompleteRecordCount;
            for (var index = 0; index < combinedLength; index++)
            {
                if (rented[index] != (byte)'\n') continue;
                var line = rented.AsSpan(start, index - start);
                if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
                recordNumber++;
                ParseCompleteLine(line, request, recordNumber, state.CompleteRecordCount == 0 && start == 0, records, issues);
                start = index + 1;
            }

            var pending = rented.AsSpan(start, combinedLength - start);
            if (flush && pending.Length > 0)
            {
                recordNumber++;
                ParseCompleteLine(pending, request, recordNumber, state.CompleteRecordCount == 0 && start == 0, records, issues);
                pending = [];
            }
            if (pending.Length > ActionProtocolLimits.MaximumProgressRecordBytes)
                throw Oversized("An incomplete progress record exceeds the per-record limit.");

            var next = new ActionProgressParseState(
                checked(state.BytesConsumed + bytes.Length),
                recordNumber,
                pending);
            return new(records.AsReadOnly(), issues.AsReadOnly(), next);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static void ParseCompleteLine(
        ReadOnlySpan<byte> rawLine,
        ActionRequest request,
        long recordNumber,
        bool firstLine,
        List<ActionProgressRecord> records,
        List<ActionProgressIssue> issues)
    {
        if (rawLine.Length > ActionProtocolLimits.MaximumProgressRecordBytes)
        {
            issues.Add(new(recordNumber, ActionProtocolFailure.Oversized, "Progress record exceeds the per-record limit."));
            return;
        }
        if (firstLine && rawLine.StartsWith(Encoding.UTF8.Preamble)) rawLine = rawLine[Encoding.UTF8.Preamble.Length..];
        if (rawLine.IsEmpty || IsWhitespace(rawLine)) return;

        try
        {
            var text = StrictUtf8.GetString(rawLine);
            records.Add(ParseRecord(Encoding.UTF8.GetBytes(text), request));
        }
        catch (ActionProtocolValidationException exception)
        {
            issues.Add(new(recordNumber, exception.Failure, exception.Message));
        }
        catch (DecoderFallbackException)
        {
            issues.Add(new(recordNumber, ActionProtocolFailure.InvalidValue, "Progress record is not valid UTF-8."));
        }
    }

    public static ActionProgressRecord ParseRecord(ReadOnlySpan<byte> payload, ActionRequest request)
    {
        if (payload.Length > ActionProtocolLimits.MaximumProgressRecordBytes)
            throw Oversized("Progress record exceeds the per-record limit.");
        using var document = StrictJson.ParseObject(payload, "progress record");
        var values = StrictJson.RequireExactProperties(document.RootElement, Properties, "progress record");
        var timestampText = StrictJson.RequireString(values["Timestamp"], "Timestamp");
        if (!DateTimeOffset.TryParseExact(timestampText, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
            throw Invalid("Timestamp must use the round-trip date/time format.");
        var levelText = StrictJson.RequireString(values["Level"], "Level");
        if (!Enum.TryParse<ActionProgressLevel>(levelText, false, out var level) || !Enum.IsDefined(level))
            throw Invalid("Level is not a supported progress level.");
        var stage = Bounded(DiagnosticsRedactor.Sanitize(StrictJson.RequireString(values["Stage"], "Stage")), ActionProtocolLimits.MaximumStageCharacters, "Stage");
        var packageId = StrictJson.RequireString(values["PackageId"], "PackageId");
        if (packageId.Length > 0)
        {
            try { ActionRequestRules.ValidatePackageIds([packageId]); }
            catch (ActionRequestValidationException exception) { throw Invalid(exception.Message, exception); }
            if (!request.PackageIds.Contains(packageId, StringComparer.OrdinalIgnoreCase))
                throw new ActionProtocolValidationException(ActionProtocolFailure.PackageMismatch, "Progress PackageId is not part of the correlated request.");
        }
        var message = Bounded(DiagnosticsRedactor.Sanitize(StrictJson.RequireString(values["Message"], "Message")), ActionProtocolLimits.MaximumMessageCharacters, "Message");
        return new(timestamp, level, stage, packageId, message);
    }

    private static bool ContainsNewline(ReadOnlyMemory<byte> pending, ReadOnlySpan<byte> bytes) =>
        pending.Span.Contains((byte)'\n') || bytes.Contains((byte)'\n');

    private static bool IsWhitespace(ReadOnlySpan<byte> value)
    {
        foreach (var item in value)
            if (item is not ((byte)' ' or (byte)'\t' or (byte)'\r')) return false;
        return true;
    }

    private static string Bounded(string value, int maximum, string name)
    {
        if (value.Length > maximum) throw Invalid($"{name} exceeds its character limit.");
        if (value.Any(char.IsControl)) throw Invalid($"{name} contains a control character.");
        return value;
    }

    private static ActionProtocolValidationException Oversized(string message) => new(ActionProtocolFailure.Oversized, message);
    private static ActionProtocolValidationException Invalid(string message, Exception? inner = null) => new(ActionProtocolFailure.InvalidValue, message, inner);
}

public sealed class ActionResultCodec
{
    private static readonly string[] ResultProperties =
        ["SchemaVersion", "GeneratedAt", "Computer", "Status", "Message", "ExitCode", "RequestPath", "ProgressPath", "WingetLogPath", "Packages"];
    private static readonly string[] PackageProperties =
        ["Id", "Name", "Action", "Status", "ExitCode", "Verified", "StartedAt", "FinishedAt", "Arguments"];

    public byte[] Serialize(ActionFinalResult result, ActionRequest request, ActionArtifactPaths expectedPaths)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(expectedPaths);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("SchemaVersion", result.SchemaVersion);
            writer.WriteString("GeneratedAt", result.GeneratedAt.ToString("o", CultureInfo.InvariantCulture));
            writer.WriteString("Computer", result.Computer);
            writer.WriteString("Status", result.Status.ToString());
            writer.WriteString("Message", result.Message);
            writer.WriteNumber("ExitCode", result.ExitCode);
            writer.WriteString("RequestPath", result.RequestPath);
            writer.WriteString("ProgressPath", result.ProgressPath);
            writer.WriteString("WingetLogPath", result.WinGetLogPath);
            writer.WriteStartArray("Packages");
            foreach (var package in result.Packages)
            {
                writer.WriteStartObject();
                writer.WriteString("Id", package.Id);
                writer.WriteString("Name", package.Name);
                writer.WriteString("Action", package.Action.ToString());
                writer.WriteString("Status", package.Status.ToString());
                writer.WriteNumber("ExitCode", package.ExitCode);
                writer.WriteBoolean("Verified", package.Verified);
                if (package.StartedAt is null) writer.WriteNull("StartedAt");
                else writer.WriteString("StartedAt", package.StartedAt.Value.ToString("o", CultureInfo.InvariantCulture));
                writer.WriteString("FinishedAt", package.FinishedAt.ToString("o", CultureInfo.InvariantCulture));
                writer.WriteStartArray("Arguments");
                foreach (var argument in package.Arguments) writer.WriteStringValue(argument);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var payload = stream.ToArray();
        _ = Parse(payload, request, expectedPaths);
        return payload;
    }

    public ActionFinalResult Parse(ReadOnlySpan<byte> payload, ActionRequest request, ActionArtifactPaths expectedPaths)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(expectedPaths);
        ActionRequestRules.Validate(request);
        if (payload.Length > ActionProtocolLimits.MaximumResultBytes)
            throw new ActionProtocolValidationException(ActionProtocolFailure.Oversized, "The result artifact exceeds the 2 MiB limit.");
        if (!string.Equals(request.RequestId, expectedPaths.RequestId, StringComparison.Ordinal))
            throw new ActionProtocolValidationException(ActionProtocolFailure.RequestMismatch, "Result path identity does not match the request.");

        using var document = StrictJson.ParseObject(payload, "result artifact");
        var values = StrictJson.RequireExactProperties(document.RootElement, ResultProperties, "result artifact");
        var schemaVersion = StrictJson.RequireInt32(values["SchemaVersion"], "SchemaVersion");
        if (schemaVersion != ActionRequestRules.CurrentSchemaVersion)
            throw new ActionProtocolValidationException(ActionProtocolFailure.UnsupportedSchema, "The result schema version is not supported.");
        var generatedAt = RequireTimestamp(values["GeneratedAt"], "GeneratedAt", allowNull: false)!.Value;
        var computer = Bounded(StrictJson.RequireString(values["Computer"], "Computer"), ActionProtocolLimits.MaximumComputerCharacters, "Computer");
        var status = RequireEnum<ActionResultStatus>(values["Status"], "Status");
        var message = Bounded(DiagnosticsRedactor.Sanitize(StrictJson.RequireString(values["Message"], "Message")), ActionProtocolLimits.MaximumMessageCharacters, "Message");
        var exitCode = StrictJson.RequireInt32(values["ExitCode"], "ExitCode");
        var requestPath = StrictJson.RequireString(values["RequestPath"], "RequestPath");
        var progressPath = StrictJson.RequireString(values["ProgressPath"], "ProgressPath");
        var wingetLogPath = StrictJson.RequireString(values["WingetLogPath"], "WingetLogPath");
        RequirePathMatch(requestPath, expectedPaths.RequestPath, "RequestPath");
        RequirePathMatch(progressPath, expectedPaths.ProgressPath, "ProgressPath");
        RequirePathMatch(wingetLogPath, expectedPaths.WinGetLogPath, "WingetLogPath");
        if (values["Packages"].ValueKind != JsonValueKind.Array)
            throw StrictJson.WrongType("Packages must be a JSON array.");

        var packages = new List<ActionPackageOutcome>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in values["Packages"].EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw StrictJson.WrongType("Each Packages item must be a JSON object.");
            var package = ParsePackage(item, request);
            if (!seen.Add(package.Id)) throw new ActionProtocolValidationException(ActionProtocolFailure.PackageMismatch, "Result contains a duplicate package outcome.");
            packages.Add(package);
        }

        var result = new ActionFinalResult(schemaVersion, request.RequestId, generatedAt, computer, status, message, exitCode,
            requestPath, progressPath, wingetLogPath, packages.AsReadOnly());
        ValidateSemantics(result, request);
        return result;
    }

    private static ActionPackageOutcome ParsePackage(JsonElement element, ActionRequest request)
    {
        var values = StrictJson.RequireExactProperties(element, PackageProperties, "package result");
        var id = StrictJson.RequireString(values["Id"], "Id");
        try { ActionRequestRules.ValidatePackageIds([id]); }
        catch (ActionRequestValidationException exception) { throw Invalid(exception.Message, exception); }
        if (!request.PackageIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            throw new ActionProtocolValidationException(ActionProtocolFailure.PackageMismatch, "Result package is not part of the correlated request.");
        var name = Bounded(StrictJson.RequireString(values["Name"], "Name"), ActionProtocolLimits.MaximumNameCharacters, "Name");
        var action = RequireEnum<ManagedRequestAction>(values["Action"], "Action");
        if (action != request.Action) throw new ActionProtocolValidationException(ActionProtocolFailure.PackageMismatch, "Result package action does not match the request action.");
        var status = RequireEnum<PackageOutcomeStatus>(values["Status"], "Status");
        var exitCode = StrictJson.RequireInt32(values["ExitCode"], "ExitCode");
        var verified = StrictJson.RequireBoolean(values["Verified"], "Verified");
        var startedAt = RequireTimestamp(values["StartedAt"], "StartedAt", allowNull: true);
        var finishedAt = RequireTimestamp(values["FinishedAt"], "FinishedAt", allowNull: false)!.Value;
        if (values["Arguments"].ValueKind != JsonValueKind.Array) throw StrictJson.WrongType("Arguments must be a JSON array.");
        var arguments = values["Arguments"].EnumerateArray().Select(item =>
            Bounded(StrictJson.RequireString(item, "Arguments item"), ActionProtocolLimits.MaximumArgumentCharacters, "Arguments item")).ToArray();
        if (arguments.Length > ActionProtocolLimits.MaximumArgumentsPerPackage)
            throw Invalid("Arguments contains too many entries.");
        return new(id, name, action, status, exitCode, verified, startedAt, finishedAt, Array.AsReadOnly(arguments));
    }

    private static void ValidateSemantics(ActionFinalResult result, ActionRequest request)
    {
        var expectedExit = result.Status switch
        {
            ActionResultStatus.Succeeded => 0,
            ActionResultStatus.Cancelled => 2,
            ActionResultStatus.Blocked => 3,
            ActionResultStatus.Failed or ActionResultStatus.Rejected => 1,
            _ => throw new InvalidOperationException("Unknown result status.")
        };
        if (result.ExitCode != expectedExit) throw Invalid("Result Status and ExitCode are inconsistent.");

        foreach (var package in result.Packages)
        {
            var consistent = package.Status switch
            {
                PackageOutcomeStatus.Planned => request.DryRun && package.ExitCode == 0 && !package.Verified,
                PackageOutcomeStatus.Blocked => package.ExitCode == 3 && !package.Verified && package.StartedAt is null,
                PackageOutcomeStatus.Failed => package.ExitCode != 0 && !package.Verified,
                PackageOutcomeStatus.Succeeded => package.ExitCode == 0 && package.Verified,
                PackageOutcomeStatus.Unverified => package.ExitCode == 0 && !package.Verified,
                _ => false
            };
            if (!consistent) throw Invalid($"Package result semantics are inconsistent for {package.Id}.");
            if (package.Status != PackageOutcomeStatus.Blocked && package.StartedAt is null)
                throw Invalid($"Package result StartedAt is required for {package.Id}.");
            if (package.StartedAt is not null && package.FinishedAt < package.StartedAt)
                throw Invalid($"Package result timestamps are inconsistent for {package.Id}.");
            if (package.Status == PackageOutcomeStatus.Blocked)
            {
                if (package.Arguments.Count != 0) throw Invalid($"A blocked package result cannot contain WinGet arguments for {package.Id}.");
            }
            else if (!HasExpectedWinGetArguments(package))
            {
                throw Invalid($"Package result arguments do not match the bounded WinGet vector for {package.Id}.");
            }
        }

        if (result.Status == ActionResultStatus.Succeeded)
        {
            var requiredStatus = request.DryRun ? PackageOutcomeStatus.Planned : PackageOutcomeStatus.Succeeded;
            if (result.Packages.Count != request.PackageIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() || result.Packages.Any(item => item.Status != requiredStatus))
                throw Invalid("A successful final result must account for every requested package with the expected verified or dry-run status.");
        }
        if (result.Status == ActionResultStatus.Failed && !result.Packages.Any(item => item.Status is PackageOutcomeStatus.Failed or PackageOutcomeStatus.Unverified))
            throw Invalid("A failed final result must contain a failed or unverified package outcome.");
        if (result.Status == ActionResultStatus.Blocked && !result.Packages.Any(item => item.Status == PackageOutcomeStatus.Blocked))
            throw Invalid("A blocked final result must contain a blocked package outcome.");
        if (result.Status == ActionResultStatus.Cancelled && result.Packages.Any(item => item.Status is PackageOutcomeStatus.Failed or PackageOutcomeStatus.Unverified or PackageOutcomeStatus.Blocked))
            throw Invalid("A cancelled final result cannot contain a failed, unverified, or blocked package outcome under the shipping precedence rules.");
    }

    private static bool HasExpectedWinGetArguments(ActionPackageOutcome package)
    {
        var verb = package.Action == ManagedRequestAction.Install ? "install" : "upgrade";
        string[] required =
        [
            verb, "--id", package.Id, "--exact", "--source", "winget",
            "--accept-package-agreements", "--accept-source-agreements"
        ];
        if (package.Arguments.Count != required.Length && package.Arguments.Count != required.Length + 2) return false;
        if (!package.Arguments.Take(required.Length).SequenceEqual(required, StringComparer.Ordinal)) return false;
        return package.Arguments.Count == required.Length ||
            package.Arguments.Skip(required.Length).SequenceEqual(["--silent", "--disable-interactivity"], StringComparer.Ordinal);
    }

    private static DateTimeOffset? RequireTimestamp(JsonElement value, string name, bool allowNull)
    {
        if (allowNull && value.ValueKind == JsonValueKind.Null) return null;
        var text = StrictJson.RequireString(value, name);
        if (!DateTimeOffset.TryParseExact(text, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
            throw Invalid($"{name} must use the round-trip date/time format.");
        return timestamp;
    }

    private static T RequireEnum<T>(JsonElement value, string name) where T : struct, Enum
    {
        var text = StrictJson.RequireString(value, name);
        if (!Enum.TryParse<T>(text, false, out var result) || !Enum.IsDefined(result)) throw Invalid($"{name} is not supported.");
        return result;
    }

    private static void RequirePathMatch(string actual, string expected, string name)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new ActionProtocolValidationException(ActionProtocolFailure.RequestMismatch, $"{name} does not match the correlated artifact path.");
    }

    private static string Bounded(string value, int maximum, string name)
    {
        if (value.Length > maximum) throw Invalid($"{name} exceeds its character limit.");
        if (value.Any(char.IsControl)) throw Invalid($"{name} contains a control character.");
        return value;
    }

    private static ActionProtocolValidationException Invalid(string message, Exception? inner = null) => new(ActionProtocolFailure.InvalidValue, message, inner);
}

internal static class StrictJson
{
    public static JsonDocument ParseObject(ReadOnlySpan<byte> payload, string description)
    {
        try
        {
            var document = JsonDocument.Parse(payload.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw WrongType($"The {description} root must be a JSON object.");
            }
            return document;
        }
        catch (JsonException exception)
        {
            throw new ActionProtocolValidationException(ActionProtocolFailure.MalformedJson, $"The {description} is not valid JSON.", exception);
        }
    }

    public static Dictionary<string, JsonElement> RequireExactProperties(JsonElement element, IReadOnlyList<string> approved, string description)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var approvedSet = new HashSet<string>(approved, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!approvedSet.Contains(property.Name))
                throw new ActionProtocolValidationException(ActionProtocolFailure.UnknownField, $"Unknown {description} property: {property.Name}");
            if (!values.TryAdd(property.Name, property.Value))
                throw new ActionProtocolValidationException(ActionProtocolFailure.DuplicateField, $"Duplicate {description} property: {property.Name}");
        }
        foreach (var name in approved)
            if (!values.ContainsKey(name)) throw new ActionProtocolValidationException(ActionProtocolFailure.MissingField, $"Missing {description} property: {name}");
        return values;
    }

    public static string RequireString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String) throw WrongType($"{name} must be a JSON string.");
        return value.GetString()!;
    }

    public static int RequireInt32(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result)) throw WrongType($"{name} must be a JSON integer.");
        return result;
    }

    public static bool RequireBoolean(JsonElement value, string name)
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw WrongType($"{name} must be a JSON Boolean.");
        return value.GetBoolean();
    }

    public static ActionProtocolValidationException WrongType(string message) => new(ActionProtocolFailure.WrongType, message);
}
