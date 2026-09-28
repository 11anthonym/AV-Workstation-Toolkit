using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace AVWorkstationToolkit.Domain.Workstation;

/// <summary>A workstation inventory, workstation template, or migration session file failed validation.</summary>
public sealed class WorkstationDocumentException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// A memory-backed document destination that enforces the same byte boundary as the corresponding reader while the
/// JSON writer is producing output. This prevents an export or persisted session from becoming a file this version
/// cannot read back, without first buffering an oversized document.
/// </summary>
internal sealed class BoundedWorkstationDocumentStream(int maximumBytes, string documentType) : MemoryStream
{
    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureRoom(count);
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureRoom(buffer.Length);
        base.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        EnsureRoom(1);
        base.WriteByte(value);
    }

    private void EnsureRoom(int count)
    {
        if (count < 0 || Position > maximumBytes - count)
            throw new WorkstationDocumentException(
                $"The {WorkstationDocumentReader.Describe(documentType)} is larger than the {DescribeLimit(maximumBytes)} limit for this document.");
    }

    private static string DescribeLimit(int bytes) => bytes % (1024 * 1024) == 0
        ? $"{bytes / (1024 * 1024)} MiB"
        : $"{bytes} byte" + (bytes == 1 ? string.Empty : "s");
}

public static class WorkstationDocumentTypes
{
    public const string Inventory = "workstation-inventory";
    public const string Profile = "workstation-profile";
    public const string Session = "migration-session";
}

/// <summary>
/// Strict reader shared by the portable workstation documents. Every document is bounded, rejects duplicate and unknown
/// properties, requires an explicit schema version and document type, and reports a newer schema or the wrong kind of
/// document plainly instead of guessing.
/// </summary>
public static partial class WorkstationDocumentReader
{
    public const int MaximumTextLength = 512;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9+_.-]{1,127}$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PackageIdPattern();

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex GuidPattern();

    public static JavaScriptEncoder Encoder { get; } = JavaScriptEncoder.Create(UnicodeRanges.All);

    /// <summary>Parses a bounded document and checks its header; returns the root object for the caller to read.</summary>
    public static JsonDocument Open(ReadOnlySpan<byte> payload, int maximumBytes, string expectedType, int supportedSchema)
    {
        if (payload.Length == 0) throw new WorkstationDocumentException("The file is empty.");
        if (payload.Length > maximumBytes)
            throw new WorkstationDocumentException($"The file is larger than the {maximumBytes / (1024 * 1024)} MiB limit for this document.");
        if (payload.Length >= 3 && payload[0] == 0xEF && payload[1] == 0xBB && payload[2] == 0xBF) payload = payload[3..];
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
        }
        catch (JsonException exception)
        {
            throw new WorkstationDocumentException("The file isn't valid JSON.", exception);
        }

        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new WorkstationDocumentException("The file must contain one JSON object.");
            RejectDuplicates(root);
            if (!root.TryGetProperty("documentType", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                throw new WorkstationDocumentException("The file has no documentType, so it isn't an AV Workstation Toolkit document.");
            var type = typeElement.GetString();
            if (!string.Equals(type, expectedType, StringComparison.Ordinal))
                throw new WorkstationDocumentException(type is WorkstationDocumentTypes.Inventory or WorkstationDocumentTypes.Profile or WorkstationDocumentTypes.Session
                    ? $"This file is a {Describe(type)}, not a {Describe(expectedType)}."
                    : $"The documentType '{Bounded(type)}' isn't a {Describe(expectedType)}.");
            if (!root.TryGetProperty("schemaVersion", out var schemaElement) || schemaElement.ValueKind != JsonValueKind.Number ||
                !schemaElement.TryGetInt32(out var schema) || schema < 1)
                throw new WorkstationDocumentException("The file has no valid schemaVersion.");
            if (schema > supportedSchema)
                throw new WorkstationDocumentException($"This {Describe(expectedType)} uses schema version {schema}, which is newer than this version of AV Workstation Toolkit supports ({supportedSchema}). Update the Toolkit to open it.");
            if (schema != supportedSchema)
                throw new WorkstationDocumentException($"Schema version {schema} isn't supported for a {Describe(expectedType)}.");
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    public static string Describe(string? type) => type switch
    {
        WorkstationDocumentTypes.Inventory => "workstation inventory",
        WorkstationDocumentTypes.Profile => "workstation template",
        WorkstationDocumentTypes.Session => "migration session",
        _ => "workstation document"
    };

    public static void RequireOnly(JsonElement element, string context, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new WorkstationDocumentException($"{context} must be a JSON object.");
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new WorkstationDocumentException($"{context} contains the unsupported field '{Bounded(property.Name)}'.");
    }

    public static string Text(JsonElement element, string name, string context, bool required = false, int maximumLength = MaximumTextLength)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return required ? throw new WorkstationDocumentException($"{context} is missing '{name}'.") : string.Empty;
        if (value.ValueKind != JsonValueKind.String) throw new WorkstationDocumentException($"{context} field '{name}' must be text.");
        var text = value.GetString() ?? string.Empty;
        if (text.Length > maximumLength) throw new WorkstationDocumentException($"{context} field '{name}' is longer than {maximumLength} characters.");
        if (text.Any(char.IsControl)) throw new WorkstationDocumentException($"{context} field '{name}' contains control characters.");
        text = text.Trim();
        if (required && text.Length == 0) throw new WorkstationDocumentException($"{context} field '{name}' is empty.");
        return text;
    }

    public static string PackageId(JsonElement element, string name, string context)
    {
        var text = Text(element, name, context, maximumLength: 128);
        if (text.Length > 0 && !PackageIdPattern().IsMatch(text))
            throw new WorkstationDocumentException($"{context} field '{name}' isn't a valid package ID.");
        return text;
    }

    public static string Guid(JsonElement element, string name, string context)
    {
        var text = Text(element, name, context, maximumLength: 38);
        if (text.Length > 0 && !GuidPattern().IsMatch(text))
            throw new WorkstationDocumentException($"{context} field '{name}' isn't a braced GUID.");
        return text.ToUpperInvariant();
    }

    public static bool Boolean(JsonElement element, string name, string context, bool defaultValue = false)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return defaultValue;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new WorkstationDocumentException($"{context} field '{name}' must be true or false.")
        };
    }

    public static int Integer(JsonElement element, string name, string context, int minimum, int maximum, int? defaultValue = null)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return defaultValue ?? throw new WorkstationDocumentException($"{context} is missing '{name}'.");
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < minimum || number > maximum)
            throw new WorkstationDocumentException($"{context} field '{name}' must be a whole number from {minimum} to {maximum}.");
        return number;
    }

    public static DateTimeOffset? Timestamp(JsonElement element, string name, string context, bool required = false)
    {
        var text = Text(element, name, context, required, 40);
        if (text.Length == 0) return null;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
            throw new WorkstationDocumentException($"{context} field '{name}' isn't a valid date and time.");
        return value.ToUniversalTime();
    }

    public static T Token<T>(JsonElement element, string name, string context, IReadOnlyDictionary<string, T> tokens, T? defaultValue = null) where T : struct
    {
        var text = Text(element, name, context, defaultValue is null, 40);
        if (text.Length == 0 && defaultValue is not null) return defaultValue.Value;
        return tokens.TryGetValue(text, out var value)
            ? value
            : throw new WorkstationDocumentException($"{context} field '{name}' has the unsupported value '{Bounded(text)}'.");
    }

    public static IReadOnlyList<JsonElement> Array(JsonElement element, string name, string context, int maximum, bool required = false)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return required ? throw new WorkstationDocumentException($"{context} is missing '{name}'.") : [];
        if (value.ValueKind != JsonValueKind.Array) throw new WorkstationDocumentException($"{context} field '{name}' must be a list.");
        var items = value.EnumerateArray().ToArray();
        if (items.Length > maximum) throw new WorkstationDocumentException($"{context} field '{name}' has more than {maximum} entries.");
        return items;
    }

    public static IReadOnlyList<string> TextArray(JsonElement element, string name, string context, int maximum, int maximumLength = 256)
    {
        return Array(element, name, context, maximum).Select((item, index) =>
        {
            if (item.ValueKind != JsonValueKind.String) throw new WorkstationDocumentException($"{context} field '{name}' must contain only text.");
            var text = item.GetString() ?? string.Empty;
            if (text.Length > maximumLength || text.Any(char.IsControl) || text.Trim().Length == 0)
                throw new WorkstationDocumentException($"{context} field '{name}' entry {index + 1} is empty, too long, or contains control characters.");
            return text.Trim();
        }).ToArray();
    }

    public static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new WorkstationDocumentException($"The file repeats the field '{Bounded(property.Name)}'.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
        }
    }

    private static string Bounded(string? value)
    {
        var text = new string((value ?? string.Empty).Where(character => !char.IsControl(character)).Take(60).ToArray());
        return text.Length == 0 ? "(empty)" : text;
    }
}
