using System.Buffers;
using System.Text.Json;

namespace PiCommandCenter.Node.Runtime;

/// <summary>
/// One versioned NDJSON protocol envelope (SPEC §24.1). <see cref="Payload"/> is kept as a
/// <see cref="JsonElement"/> so unknown properties survive the round trip unchanged.
/// </summary>
public sealed record PiEnvelope(
    int ProtocolVersion,
    string MessageId,
    string Kind,
    string SessionId,
    string Type,
    JsonElement? Payload);

/// <summary>Canonical protocol frame kinds (SPEC §24.1).</summary>
public static class PiFrameKinds
{
    public const string Hello = "hello";
    public const string Event = "event";
    public const string Request = "request";
    public const string Response = "response";
    public const string Heartbeat = "heartbeat";
    public const string Goodbye = "goodbye";
}

/// <summary>Raised when a protocol frame violates the framing contract.</summary>
public sealed class PiFrameException(string message) : Exception(message);

/// <summary>
/// Strict NDJSON framing for the Pi worker stdio protocol: LF-delimited UTF-8 frames with a
/// hard 1 MiB limit per frame (excluding the newline). Frames are JSON objects with
/// <c>protocolVersion: 1</c> and a non-empty string <c>messageId</c>, <c>kind</c>,
/// <c>sessionId</c>, and <c>type</c>.
/// </summary>
public static class PiProtocol
{
    /// <summary>Protocol version emitted and accepted by this implementation.</summary>
    public const int Version = 1;

    /// <summary>Maximum size of a single frame in bytes, excluding the trailing newline.</summary>
    public const int MaxFrameBytes = 1024 * 1024;

    private static readonly string[] KnownKinds =
    [
        PiFrameKinds.Hello,
        PiFrameKinds.Event,
        PiFrameKinds.Request,
        PiFrameKinds.Response,
        PiFrameKinds.Heartbeat,
        PiFrameKinds.Goodbye,
    ];

    private static readonly JsonWriterOptions WriterOptions = new();

    /// <summary>Serializes one envelope to a UTF-8 NDJSON line including the trailing LF.</summary>
    public static byte[] Encode(PiEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, WriterOptions);
        writer.WriteStartObject();
        writer.WriteNumber("protocolVersion", envelope.ProtocolVersion);
        writer.WriteString("messageId", envelope.MessageId);
        writer.WriteString("kind", envelope.Kind);
        writer.WriteString("sessionId", envelope.SessionId);
        writer.WriteString("type", envelope.Type);
        if (envelope.Payload is JsonElement payload)
        {
            writer.WritePropertyName("payload");
            payload.WriteTo(writer);
        }
        else
        {
            writer.WriteNull("payload");
        }

        writer.WriteEndObject();
        writer.Flush();
        var frame = buffer.WrittenSpan;
        var line = new byte[frame.Length + 1];
        frame.CopyTo(line);
        line[^1] = (byte)'\n';
        return line;
    }

    /// <summary>Parses one LF-terminated UTF-8 frame (newline optional for the final frame).</summary>
    public static PiEnvelope Decode(ReadOnlySpan<byte> frame)
    {
        while (frame.Length > 0 && (frame[^1] == (byte)'\n' || frame[^1] == (byte)'\r'))
        {
            frame = frame[..^1];
        }

        if (frame.Length == 0 || frame.IndexOfAnyExcept((byte)' ', (byte)'\t') < 0)
        {
            throw new PiFrameException("FRAME_EMPTY: empty protocol frame.");
        }

        if (frame.Length > MaxFrameBytes)
        {
            throw new PiFrameException(
                $"FRAME_OVERSIZED: frame is {frame.Length} bytes; the limit is {MaxFrameBytes}.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(frame.ToArray());
        }
        catch (JsonException cause)
        {
            throw new PiFrameException($"FRAME_INVALID_JSON: {cause.Message}");
        }

        using (document)
        {
            return FromDocument(document.RootElement);
        }
    }

    /// <summary>Parses one frame from text (used by tests and non-stream callers).</summary>
    public static PiEnvelope Decode(string frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Decode(System.Text.Encoding.UTF8.GetBytes(frame));
    }

    private static PiEnvelope FromDocument(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new PiFrameException("FRAME_NOT_OBJECT: protocol frames must be JSON objects.");
        }

        if (!root.TryGetProperty("protocolVersion", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt32(out var version))
        {
            throw new PiFrameException("FRAME_MISSING_FIELD: 'protocolVersion' must be a number.");
        }

        if (version != Version)
        {
            throw new PiFrameException(
                $"FRAME_UNSUPPORTED_PROTOCOL_VERSION: got {version}, expected {Version}.");
        }

        var messageId = RequireString(root, "messageId");
        var kind = RequireString(root, "kind");
        if (Array.IndexOf(KnownKinds, kind) < 0)
        {
            throw new PiFrameException($"FRAME_UNKNOWN_KIND: '{kind}' is not a known frame kind.");
        }

        var sessionId = RequireString(root, "sessionId");
        var type = RequireString(root, "type");

        JsonElement? payload = null;
        if (root.TryGetProperty("payload", out var payloadElement)
            && payloadElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            payload = payloadElement.Clone();
        }

        return new PiEnvelope(version, messageId, kind, sessionId, type, payload);
    }

    private static string RequireString(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out var element)
            || element.ValueKind != JsonValueKind.String
            || element.GetString() is not { Length: > 0 } value)
        {
            throw new PiFrameException($"FRAME_MISSING_FIELD: '{field}' must be a non-empty string.");
        }

        return value;
    }
}

/// <summary>
/// Incremental byte-stream decoder for NDJSON frames. Buffers partial lines across reads and
/// enforces the 1 MiB frame limit before any JSON parsing takes place.
/// </summary>
public sealed class PiFrameDecoder
{
    private readonly MemoryStream _buffer = new();

    /// <summary>
    /// Feeds raw bytes and returns every complete frame now available. Oversized frames throw
    /// <see cref="PiFrameException"/>; the caller owns the failure policy for the stream.
    /// </summary>
    public IReadOnlyList<PiEnvelope> Push(ReadOnlySpan<byte> bytes)
    {
        var frames = new List<PiEnvelope>();
        var position = 0;
        while (position < bytes.Length)
        {
            var newline = bytes[position..].IndexOf((byte)'\n');
            if (newline < 0)
            {
                Append(bytes[position..]);
                break;
            }

            Append(bytes[position..(position + newline)]);
            frames.Add(DecodeBuffered());
            position += newline + 1;
        }

        return frames;
    }

    /// <summary>Decodes any trailing buffered bytes as the final frame (stream end).</summary>
    public IReadOnlyList<PiEnvelope> Flush()
    {
        if (_buffer.Length == 0)
        {
            return [];
        }

        var frame = DecodeBuffered();
        return [frame];
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        if (_buffer.Length + bytes.Length > PiProtocol.MaxFrameBytes)
        {
            throw new PiFrameException(
                $"FRAME_OVERSIZED: buffered frame exceeds {PiProtocol.MaxFrameBytes} bytes.");
        }

        _buffer.Write(bytes);
    }

    private PiEnvelope DecodeBuffered()
    {
        var length = (int)_buffer.Length;
        var bytes = _buffer.GetBuffer().AsSpan(0, length).ToArray();
        _buffer.SetLength(0);
        return PiProtocol.Decode(bytes);
    }
}
