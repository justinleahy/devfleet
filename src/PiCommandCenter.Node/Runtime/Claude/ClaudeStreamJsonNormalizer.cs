using System.Text.Json;

namespace PiCommandCenter.Node.Runtime.Claude;

/// <summary>
/// Maps one Claude Code <c>stream-json</c> NDJSON object to a normalized event type and
/// payload. Unknown types and extra fields are preserved. Never reads transcript files.
/// </summary>
public static class ClaudeStreamJsonNormalizer
{
    public sealed record NormalizedLine(
        string Type,
        IReadOnlyDictionary<string, object?> Payload,
        string? ProviderSessionId,
        bool IsMalformed);

    public static NormalizedLine Parse(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return Malformed(line);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Malformed(line);
            }

            var root = document.RootElement;
            var payload = CloneObject(root);
            var providerSessionId = ReadSessionId(root);
            var nativeType = ReadString(root, "type");
            var subtype = ReadString(root, "subtype");

            if (nativeType is null)
            {
                return new NormalizedLine("runtime.unknown", payload, providerSessionId, IsMalformed: false);
            }

            var mapped = MapType(nativeType, subtype, root);
            return new NormalizedLine(mapped, payload, providerSessionId, IsMalformed: false);
        }
    }

    private static string MapType(string nativeType, string? subtype, JsonElement root)
    {
        if (nativeType is "system" && subtype is "init")
        {
            return "session.started";
        }

        if (nativeType is "system" && subtype is "api_retry")
        {
            return "runtime.retry";
        }

        if (nativeType is "result")
        {
            return "result.completed";
        }

        if (nativeType is "stream_event" || HasDelta(root))
        {
            return "message.delta";
        }

        if (nativeType is "assistant")
        {
            return HasToolUse(root) ? "tool.started" : "message.delta";
        }

        if (nativeType is "user")
        {
            return HasToolResult(root) ? "tool.completed" : "message.completed";
        }

        return nativeType;
    }

    private static bool HasDelta(JsonElement root)
    {
        if (root.TryGetProperty("event", out var evt)
            && evt.ValueKind == JsonValueKind.Object
            && evt.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String)
        {
            var value = type.GetString();
            return value is "content_block_delta" or "text_delta";
        }

        return false;
    }

    private static bool HasToolUse(JsonElement root) =>
        ContentHasBlockType(root, "tool_use");

    private static bool HasToolResult(JsonElement root) =>
        ContentHasBlockType(root, "tool_result");

    private static bool ContentHasBlockType(JsonElement root, string blockType)
    {
        if (!root.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == blockType)
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadSessionId(JsonElement root)
    {
        if (root.TryGetProperty("session_id", out var id) && id.ValueKind == JsonValueKind.String)
        {
            return id.GetString();
        }

        return null;
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static Dictionary<string, object?> CloneObject(JsonElement root)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            payload[property.Name] = property.Value.Clone();
        }

        return payload;
    }

    private static NormalizedLine Malformed(string line)
    {
        var preview = line.Length > 256 ? line[..256] : line;
        return new NormalizedLine(
            "runtime.malformed_line",
            new Dictionary<string, object?>
            {
                ["preview"] = preview,
                ["length"] = line.Length,
            },
            ProviderSessionId: null,
            IsMalformed: true);
    }
}
