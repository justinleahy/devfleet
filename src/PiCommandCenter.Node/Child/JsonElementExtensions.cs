using System.Text.Json;

namespace PiCommandCenter.Node.Child;

/// <summary>Small accessors for loosely typed protocol payloads.</summary>
internal static class JsonElementExtensions
{
    /// <summary>Returns a non-empty string property, or null when absent or of another kind.</summary>
    public static string? GetStringProperty(this JsonElement element, string field)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
            ? text
            : null;


    /// <summary>Null-safe wrapper on <see cref="GetStringProperty"/> for optional payloads.</summary>
    public static string? GetStringProperty(this JsonElement? payload, string field)
        => payload is JsonElement element ? element.GetStringProperty(field) : null;

    /// <summary>Null-safe wrapper on <see cref="GetInt64Property"/> for optional payloads.</summary>
    public static long? GetInt64Property(this JsonElement? payload, string field)
        => payload is JsonElement element ? element.GetInt64Property(field) : null;

    /// <summary>Null-safe 32-bit accessor for optional payloads.</summary>
    public static int? GetInt32Property(this JsonElement? payload, string field)
        => payload is JsonElement element
            && element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>Null-safe boolean accessor for optional payloads.</summary>
    public static bool? GetBooleanProperty(this JsonElement? payload, string field)
        => payload is JsonElement element
            && element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(field, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    /// <summary>Null-safe string array accessor for optional payloads.</summary>
    public static IReadOnlyList<string>? GetStringListProperty(this JsonElement? payload, string field)
        => payload is JsonElement element
            && element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray()
                .Where(i => i.ValueKind == JsonValueKind.String)
                .Select(i => i.GetString()!)]
            : null;
    /// <summary>Returns a long property, or null when absent or not a number.</summary>
    public static long? GetInt64Property(this JsonElement element, string field)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
            ? number
            : null;

    /// <summary>Returns a nested property clone, or null when absent.</summary>
    public static JsonElement? GetPropertyOrNull(this JsonElement element, string field)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(field, out var value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value.Clone()
            : null;
}
