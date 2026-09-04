using System.Linq;
using System.Text;
using PiCommandCenter.Node.Runtime;

namespace PiCommandCenter.Node.Tests;

public class PiProtocolFramingTests
{
    private static PiEnvelope Envelope(
        string kind = PiFrameKinds.Event,
        string type = "tool.started",
        string? payload = null) => new(
        ProtocolVersion: PiProtocol.Version,
        MessageId: "01K-msg",
        Kind: kind,
        SessionId: "session-root",
        Type: type,
        Payload: payload is null ? null : System.Text.Json.JsonDocument.Parse(payload).RootElement.Clone());

    [Fact]
    public void Encode_then_decode_round_trips_the_envelope()
    {
        var frame = PiProtocol.Encode(Envelope(payload: """{"tool":"inspect_project_diff","nested":{"a":1}}"""));

        Assert.Equal((byte)'\n', frame[^1]);

        var decoded = PiProtocol.Decode(frame);

        Assert.Equal(PiProtocol.Version, decoded.ProtocolVersion);
        Assert.Equal("01K-msg", decoded.MessageId);
        Assert.Equal(PiFrameKinds.Event, decoded.Kind);
        Assert.Equal("session-root", decoded.SessionId);
        Assert.Equal("tool.started", decoded.Type);
        Assert.NotNull(decoded.Payload);
        Assert.Equal("inspect_project_diff", decoded.Payload!.Value.GetProperty("tool").GetString());
        // Unknown/nested payload properties survive the round trip unchanged.
        Assert.Equal(1, decoded.Payload.Value.GetProperty("nested").GetProperty("a").GetInt32());
    }

    [Fact]
    public void Every_canonical_frame_kind_is_accepted()
    {
        foreach (var kind in new[]
                 {
                     PiFrameKinds.Hello, PiFrameKinds.Event, PiFrameKinds.Request,
                     PiFrameKinds.Response, PiFrameKinds.Heartbeat, PiFrameKinds.Goodbye,
                 })
        {
            var decoded = PiProtocol.Decode(PiProtocol.Encode(Envelope(kind: kind)));
            Assert.Equal(kind, decoded.Kind);
        }
    }

    [Theory]
    [InlineData("""{"protocolVersion":1,"kind":"hello"}""", "FRAME_MISSING_FIELD")]
    [InlineData("""{"protocolVersion":1,"messageId":"m","kind":"hello"}""", "FRAME_MISSING_FIELD")]
    [InlineData("""{"protocolVersion":1,"messageId":"m","kind":"wat","sessionId":"s","type":"t"}""", "FRAME_UNKNOWN_KIND")]
    [InlineData("""{"protocolVersion":2,"messageId":"m","kind":"hello","sessionId":"s","type":"t"}""", "FRAME_UNSUPPORTED_PROTOCOL_VERSION")]
    [InlineData("""{"protocolVersion":"1","messageId":"m","kind":"hello","sessionId":"s","type":"t"}""", "FRAME_MISSING_FIELD")]
    [InlineData("""{"messageId":"","kind":"hello","protocolVersion":1,"sessionId":"s","type":"t"}""", "FRAME_MISSING_FIELD")]
    [InlineData("""[1,2,3]""", "FRAME_NOT_OBJECT")]
    [InlineData("""{not json}""", "FRAME_INVALID_JSON")]
    [InlineData("""   """, "FRAME_EMPTY")]
    public void Strict_framing_rejects_malformed_frames(string frame, string errorPrefix)
    {
        var failure = Assert.Throws<PiFrameException>(() => PiProtocol.Decode(frame));
        Assert.StartsWith(errorPrefix, failure.Message);
    }

    [Fact]
    public void A_null_payload_is_optional_and_round_trips_as_absent()
    {
        var decoded = PiProtocol.Decode(PiProtocol.Encode(Envelope()));
        Assert.Null(decoded.Payload);

        var explicitNull = """{"protocolVersion":1,"messageId":"m","kind":"hello","sessionId":"s","type":"t","payload":null}""";
        Assert.Null(PiProtocol.Decode(explicitNull).Payload);
    }

    [Fact]
    public void Oversize_frames_are_rejected_before_json_parsing()
    {
        var oversized = new string('x', PiProtocol.MaxFrameBytes + 1);

        var failure = Assert.Throws<PiFrameException>(() => PiProtocol.Decode(oversized));
        Assert.StartsWith("FRAME_OVERSIZED", failure.Message);

        var decoder = new PiFrameDecoder();
        var big = Encoding.UTF8.GetBytes(new string('a', PiProtocol.MaxFrameBytes + 1));
        Assert.Throws<PiFrameException>(() => decoder.Push(big));
    }

    [Fact]
    public void The_decoder_buffers_partial_frames_across_reads()
    {
        var first = PiProtocol.Encode(Envelope(type: "turn.started"));
        var second = PiProtocol.Encode(Envelope(kind: PiFrameKinds.Heartbeat, type: "heartbeat"));
        var decoder = new PiFrameDecoder();

        // Split the byte stream at an arbitrary interior point.
        var all = first.Concat(second).ToArray();
        var cut = first.Length - 7;
        var batch1 = decoder.Push(all[..cut]);
        var batch2 = decoder.Push(all.Skip(cut).Take(3).ToArray());
        var batch3 = decoder.Push(all[(cut + 3)..].ToArray());

        Assert.Empty(batch1);
        Assert.Empty(batch2);
        Assert.Equal(2, batch3.Count);
        Assert.Equal("turn.started", batch3[0].Type);
        Assert.Equal(PiFrameKinds.Heartbeat, batch3[1].Kind);
    }

    [Fact]
    public void A_final_frame_without_a_trailing_newline_is_flushed()
    {
        var decoder = new PiFrameDecoder();
        var frame = PiProtocol.Encode(Envelope(kind: PiFrameKinds.Goodbye, type: "goodbye"));

        Assert.Empty(decoder.Push(frame[..^1])); // no newline, buffered
        var flushed = decoder.Flush();

        var single = Assert.Single(flushed);
        Assert.Equal(PiFrameKinds.Goodbye, single.Kind);
        Assert.Empty(decoder.Flush());
    }

    [Fact]
    public void Blank_lines_are_rejected_as_empty_frames()
    {
        var decoder = new PiFrameDecoder();

        Assert.Throws<PiFrameException>(() => decoder.Push("\n"u8.ToArray()));
    }
}
