using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;

namespace PiCommandCenter.Application.Tests.Runtime;

public class RuntimeContractTests
{
    [Fact]
    public void AgentRuntimeMode_covers_root_and_child_only()
    {
        Assert.Equal(2, Enum.GetValues<AgentRuntimeMode>().Length);
        Assert.Equal(AgentRuntimeMode.Root, Enum.Parse<AgentRuntimeMode>("Root"));
        Assert.Equal(AgentRuntimeMode.Child, Enum.Parse<AgentRuntimeMode>("Child"));
    }

    [Fact]
    public void Capabilities_default_to_none_so_unknown_runtimes_expose_no_controls()
    {
        var none = AgentRuntimeCapabilities.None;

        Assert.False(none.SupportsStreamingEvents);
        Assert.False(none.SupportsSendInput);
        Assert.False(none.SupportsCancel);
        Assert.False(none.SupportsSnapshot);
        Assert.False(none.SupportsChildSpawn);
        Assert.False(none.SupportsPlanTools);
    }

    [Fact]
    public void Capabilities_are_projected_per_runtime_not_per_version()
    {
        // Capability flags, never hardcoded version checks (SPEC §28).
        var pi = new AgentRuntimeCapabilities(
            SupportsStreamingEvents: true,
            SupportsSendInput: true,
            SupportsCancel: true,
            SupportsSnapshot: true,
            SupportsChildSpawn: true,
            SupportsPlanTools: true);

        Assert.True(pi.SupportsStreamingEvents);
        Assert.Equal("pi", AgentRuntimeKinds.Pi);
        Assert.Equal("claude-code", AgentRuntimeKinds.ClaudeCode);
        Assert.Equal("antigravity", AgentRuntimeKinds.Antigravity);
        Assert.Equal("muse", AgentRuntimeKinds.Muse);
        Assert.Equal("fake", AgentRuntimeKinds.Fake);
    }

    [Fact]
    public void AgentModelSelector_runtime_allowlist_names_every_hosted_adapter_once()
    {
        Assert.Equal(
            [AgentModelSelector.Codex, AgentModelSelector.ClaudeCode, AgentModelSelector.Antigravity, AgentModelSelector.Muse],
            AgentModelSelector.Runtimes);
        Assert.Equal(AgentRuntimeKinds.ClaudeCode, AgentModelSelector.ClaudeCode);
        Assert.Equal(AgentRuntimeKinds.Antigravity, AgentModelSelector.Antigravity);
        Assert.Equal(AgentRuntimeKinds.Muse, AgentModelSelector.Muse);
    }

    [Theory]
    [InlineData("muse/default", "default")]
    [InlineData("muse/gpt-5.4", "gpt-5.4")]
    [InlineData("muse/anthropic/claude-sonnet-4.6", "anthropic/claude-sonnet-4.6")]
    [InlineData("  muse/default  ", "default")]
    public void AgentModelSelector_parses_canonical_muse_selectors(string value, string modelId)
    {
        var selector = AgentModelSelector.Parse(value);

        Assert.Equal(AgentModelSelector.Muse, selector.Runtime);
        Assert.Equal(modelId, selector.ModelId);
        Assert.Equal(value.Trim(), selector.Value);
        Assert.Equal(modelId == AgentModelSelector.DefaultModelId, selector.IsProviderDefault);
    }

    [Fact]
    public void AgentModelSelector_trims_and_splits_at_the_first_slash()
    {
        var selector = AgentModelSelector.Parse("  codex/openai/gpt-6-astra  ");

        Assert.Equal("codex", selector.Runtime);
        Assert.Equal("openai/gpt-6-astra", selector.ModelId);
        Assert.Equal("codex/openai/gpt-6-astra", selector.Value);
        Assert.Equal(selector.Value, selector.ToString());
        Assert.False(selector.IsProviderDefault);
    }

    [Fact]
    public void AgentModelSelector_recognizes_the_provider_default_model()
    {
        Assert.True(AgentModelSelector.Parse("claude-code/default").IsProviderDefault);
        Assert.True(AgentModelSelector.Parse("antigravity/default").IsProviderDefault);
        Assert.True(AgentModelSelector.Parse("muse/default").IsProviderDefault);
        Assert.False(AgentModelSelector.Parse("antigravity/default-preview").IsProviderDefault);
        Assert.False(AgentModelSelector.Parse("muse/default-preview").IsProviderDefault);
    }

    [Fact]
    public void AgentModelSelector_accepts_exactly_the_max_length_and_rejects_one_more()
    {
        var atLimit = "codex/" + new string('a', AgentModelSelector.MaxLength - "codex/".Length);

        Assert.True(AgentModelSelector.TryParse(atLimit, out var selector));
        Assert.Equal(atLimit, selector.Value);
        Assert.False(AgentModelSelector.TryParse(atLimit + "a", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("codex")]
    [InlineData("codex/")]
    [InlineData("codex/ ")]
    [InlineData("/gpt-6-astra")]
    [InlineData("pi/gpt-6-astra")]
    [InlineData("Codex/gpt-6-astra")]
    [InlineData("local-pi")]
    [InlineData("muse")]
    [InlineData("muse/")]
    [InlineData("muse/ ")]
    [InlineData("Muse/default")]
    [InlineData("MUSE/gpt-5.4")]
    [InlineData(" muse /default")]
    [InlineData("muse-code/default")]
    [InlineData("muse:default")]
    public void AgentModelSelector_fails_closed_on_malformed_or_unknown_values(string? value)
    {
        Assert.False(AgentModelSelector.TryParse(value, out var selector));
        Assert.Null(selector);
        Assert.Throws<ArgumentException>(() => AgentModelSelector.Parse(value));
    }

    [Fact]
    public void AgentStartRequest_trims_and_keeps_every_assignment_field()
    {
        var projectId = new ProjectId(Guid.NewGuid());
        var requestId = WorkRequestId.New();

        var request = new AgentStartRequest(
            sessionId: " session-1 ",
            projectId: projectId,
            requestId: requestId,
            parentSessionId: null,
            agentName: "root",
            role: "root",
            workingDirectory: "/repo",
            prompt: "Ship it",
            mode: AgentRuntimeMode.Root,
            model: " codex/gpt-6-astra ");

        Assert.Equal("session-1", request.SessionId);
        Assert.Equal(projectId, request.ProjectId);
        Assert.Equal(requestId, request.RequestId);
        Assert.Null(request.ParentSessionId);
        Assert.Equal(AgentRuntimeMode.Root, request.Mode);
        Assert.Equal(AgentModelSelector.Parse("codex/gpt-6-astra"), request.Model);
        Assert.Null(request.Authorization);
    }

    [Fact]
    public void AgentStartRequest_validates_required_fields()
    {
        var valid = (string value) => new AgentStartRequest(
            value, new ProjectId(Guid.NewGuid()), WorkRequestId.New(), null,
            "root", "root", "/repo", "prompt", AgentRuntimeMode.Root, "codex/default");

        Assert.Throws<ArgumentException>(() => valid(""));
        Assert.Throws<ArgumentException>(() => valid("   "));

        Assert.Throws<ArgumentException>(() => new AgentStartRequest(
            "s", new ProjectId(Guid.NewGuid()), WorkRequestId.New(), "",
            "root", "root", "/repo", "prompt", AgentRuntimeMode.Root, "codex/default"));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("gpt-6-astra")]
    [InlineData("local-pi")]
    [InlineData("claude-reserved-write/fable-5-1")]
    public void AgentStartRequest_requires_a_canonical_model_selector(string model)
    {
        Assert.Throws<ArgumentException>(() => new AgentStartRequest(
            "s", new ProjectId(Guid.NewGuid()), WorkRequestId.New(), null,
            "root", "root", "/repo", "prompt", AgentRuntimeMode.Root, model));
    }

    [Fact]
    public void AgentStartRequest_keeps_host_owned_authorization()
    {
        var grant = new AgentRuntimeAuthorizationContext(Guid.NewGuid(), 42);
        var request = new AgentStartRequest(
            "s", new ProjectId(Guid.NewGuid()), WorkRequestId.New(), "parent",
            "writer", "implementer", "/repo", "prompt", AgentRuntimeMode.Child,
            "claude-code/fable-5-1", grant);

        Assert.Equal(grant, request.Authorization);
    }

    [Fact]
    public void AgentSessionHandle_rejects_an_empty_session_id_and_normalizes_provider()
    {
        Assert.Throws<ArgumentException>(() =>
            new AgentSessionHandle(" ", "prov", "pi", DateTimeOffset.UtcNow));

        var handle = new AgentSessionHandle("s1", "  ", "pi", DateTimeOffset.UtcNow);
        Assert.Null(handle.ProviderSessionId);
        Assert.Equal("pi", handle.RuntimeKind);
    }

    [Fact]
    public void AgentInput_requires_non_empty_text()
    {
        Assert.Throws<ArgumentException>(() => new AgentInput("   "));
        Assert.Throws<ArgumentException>(() => new AgentInput(""));

        var input = new AgentInput(" continue with the review ", new Dictionary<string, string> { ["plan"] = "/plans/p.md" });
        Assert.Equal("continue with the review", input.Text);
        Assert.NotNull(input.Attachments);
    }
}
