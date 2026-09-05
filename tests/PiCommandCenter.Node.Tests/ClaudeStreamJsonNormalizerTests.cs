using PiCommandCenter.Node.Runtime.Claude;

namespace PiCommandCenter.Node.Tests;

public sealed class ClaudeStreamJsonNormalizerTests
{
    [Fact]
    public void Malformed_non_json_becomes_runtime_malformed_line()
    {
        var parsed = ClaudeStreamJsonNormalizer.Parse("not-json {");

        Assert.True(parsed.IsMalformed);
        Assert.Equal("runtime.malformed_line", parsed.Type);
        Assert.Equal("not-json {", parsed.Payload["preview"]);
        Assert.Null(parsed.ProviderSessionId);
    }

    [Fact]
    public void Non_object_json_is_malformed()
    {
        var parsed = ClaudeStreamJsonNormalizer.Parse("[1,2]");

        Assert.True(parsed.IsMalformed);
        Assert.Equal("runtime.malformed_line", parsed.Type);
    }

    [Fact]
    public void System_init_maps_to_session_started_and_captures_session_id()
    {
        var parsed = ClaudeStreamJsonNormalizer.Parse(
            """{"type":"system","subtype":"init","session_id":"claude-sess-9"}""");

        Assert.False(parsed.IsMalformed);
        Assert.Equal("session.started", parsed.Type);
        Assert.Equal("claude-sess-9", parsed.ProviderSessionId);
    }

    [Fact]
    public void Permission_denial_events_are_preserved_not_dropped()
    {
        var parsed = ClaudeStreamJsonNormalizer.Parse(
            """{"type":"permission_denial","tool_name":"Bash","reason":"not allowed","session_id":"s1","extra":"keep-me"}""");

        Assert.False(parsed.IsMalformed);
        Assert.Equal("permission_denial", parsed.Type);
        Assert.Equal("s1", parsed.ProviderSessionId);
        Assert.Equal("keep-me", parsed.Payload["extra"]?.ToString());
        Assert.Equal("Bash", parsed.Payload["tool_name"]?.ToString());
    }

    [Fact]
    public void Unknown_structured_types_are_preserved()
    {
        var parsed = ClaudeStreamJsonNormalizer.Parse(
            """{"type":"future_vendor_event","session_id":"s2","payload":{"k":1}}""");

        Assert.False(parsed.IsMalformed);
        Assert.Equal("future_vendor_event", parsed.Type);
        Assert.Equal("s2", parsed.ProviderSessionId);
    }

    [Fact]
    public void Result_maps_to_result_completed()
    {
        var parsed = ClaudeStreamJsonNormalizer.Parse(
            """{"type":"result","result":"done","usage":{"input_tokens":3}}""");

        Assert.Equal("result.completed", parsed.Type);
        Assert.False(parsed.IsMalformed);
    }

    [Fact]
    public void Missing_type_is_runtime_unknown_not_malformed()
    {
        var parsed = ClaudeStreamJsonNormalizer.Parse("""{"session_id":"s3","foo":true}""");

        Assert.False(parsed.IsMalformed);
        Assert.Equal("runtime.unknown", parsed.Type);
    }
}
