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
        Assert.Equal("fake", AgentRuntimeKinds.Fake);
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
            runtimeProfile: "root-readonly");

        Assert.Equal("session-1", request.SessionId);
        Assert.Equal(projectId, request.ProjectId);
        Assert.Equal(requestId, request.RequestId);
        Assert.Null(request.ParentSessionId);
        Assert.Equal(AgentRuntimeMode.Root, request.Mode);
        Assert.Equal("root-readonly", request.RuntimeProfile);
    }

    [Fact]
    public void AgentStartRequest_validates_required_fields()
    {
        var valid = (string value) => new AgentStartRequest(
            value, new ProjectId(Guid.NewGuid()), WorkRequestId.New(), null,
            "root", "root", "/repo", "prompt", AgentRuntimeMode.Root, "default");

        Assert.Throws<ArgumentException>(() => valid(""));
        Assert.Throws<ArgumentException>(() => valid("   "));

        Assert.Throws<ArgumentException>(() => new AgentStartRequest(
            "s", new ProjectId(Guid.NewGuid()), WorkRequestId.New(), "",
            "root", "root", "/repo", "prompt", AgentRuntimeMode.Root, "default"));
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
