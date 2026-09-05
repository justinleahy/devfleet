using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;

namespace PiCommandCenter.Domain.Tests.Sessions;

/// <summary>
/// SPEC §21.5–21.6: independent dimensions drive user-facing precedence
/// Cancelled &gt; Failed &gt; Disconnected &gt; Blocked &gt; Active &gt; Completed &gt; Idle &gt; Starting.
/// Idle is never inferred from silence.
/// </summary>
public class StatusPrecedenceTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    private static AgentSession StartSession() => AgentSession.Start(
        id: "session-root",
        projectId: new ProjectId(Guid.NewGuid()),
        requestId: WorkRequestId.New(),
        parentSessionId: null,
        agentName: "root",
        role: "root",
        runtime: "pi",
        runtimeProfile: "default",
        startedAt: StartedAt);

    private static NormalizedAgentEvent Event(
        string type,
        long sequence,
        DateTimeOffset? occurredAt = null,
        IReadOnlyDictionary<string, object?>? payload = null) => new(
        ProtocolVersion: 1,
        EventId: $"{type}-{sequence}",
        NodeId: Guid.NewGuid().ToString(),
        ProjectId: Guid.NewGuid().ToString(),
        RequestId: Guid.NewGuid().ToString(),
        SessionId: "session-root",
        ParentSessionId: null,
        Sequence: sequence,
        Runtime: "pi",
        Type: type,
        OccurredAt: occurredAt ?? StartedAt.AddSeconds(sequence),
        Payload: payload ?? new Dictionary<string, object?>());

    private static AgentSession Registered()
    {
        var session = StartSession();
        session.Apply(Event("session.registered", 1));
        return session;
    }

    [Fact]
    public void Starting_session_is_not_idle_or_disconnected()
    {
        var session = StartSession();

        Assert.Equal(AgentLiveness.Starting, session.Liveness);
        Assert.Equal("Starting — agent session requested", session.StatusReason);
        Assert.Equal(AgentActivity.Idle, session.Activity);
        Assert.NotEqual(AgentLiveness.Disconnected, session.Liveness);
    }

    [Fact]
    public void Disconnected_outranks_active_work_until_a_heartbeat_restores_online()
    {
        var session = Registered();
        session.Apply(Event("turn.started", 2));
        session.Apply(Event("tool.started", 3, payload: new Dictionary<string, object?> { ["tool"] = "read_file" }));
        Assert.Equal(AgentActivity.RunningTool, session.Activity);
        Assert.Equal(AgentLiveness.Online, session.Liveness);

        session.Apply(Event("session.disconnected", 4, payload: new Dictionary<string, object?>
        {
            ["reason"] = "Last heartbeat 37 seconds ago",
        }));

        Assert.Equal(AgentLiveness.Disconnected, session.Liveness);
        Assert.Equal("Last heartbeat 37 seconds ago", session.StatusReason);
        Assert.False(session.IsTerminal);
        Assert.Equal(AgentActivity.RunningTool, session.Activity);

        session.Apply(Event("session.heartbeat", 5));
        Assert.Equal(AgentLiveness.Online, session.Liveness);
        Assert.Equal(AgentAttention.None, session.Attention);
    }

    [Fact]
    public void Cancelled_is_terminal_and_outranks_disconnected()
    {
        var session = Registered();
        session.Apply(Event("session.cancelled", 2, payload: new Dictionary<string, object?>
        {
            ["reason"] = "User cancelled",
        }));

        Assert.Equal(AgentWorkState.Cancelled, session.WorkState);
        Assert.Equal(AgentLiveness.Exited, session.Liveness);
        Assert.True(session.IsTerminal);
        Assert.Equal("User cancelled", session.StatusReason);
        Assert.Throws<InvalidOperationException>(() => session.Apply(Event("session.disconnected", 3)));
    }

    [Fact]
    public void Failed_is_terminal_unexpected_failure()
    {
        var session = Registered();
        session.Apply(Event("session.failed", 2, payload: new Dictionary<string, object?>
        {
            ["error"] = "worker crashed",
            ["reason"] = "Runtime process crashed",
        }));

        Assert.Equal(AgentWorkState.Failed, session.WorkState);
        Assert.Equal(AgentLiveness.Exited, session.Liveness);
        Assert.Equal(AgentAttention.Error, session.Attention);
        Assert.True(session.IsTerminal);
        Assert.Contains("crashed", session.StatusReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Silence_does_not_infer_idle_after_a_running_tool()
    {
        var session = Registered();
        session.Apply(Event("tool.started", 2, payload: new Dictionary<string, object?> { ["tool"] = "edit" }));
        session.Apply(Event("tool.completed", 3));

        Assert.NotEqual(AgentActivity.Idle, session.Activity);
        Assert.Equal(AgentActivity.Reasoning, session.Activity);
    }

    [Fact]
    public void Every_applied_status_event_leaves_a_human_readable_reason()
    {
        var session = Registered();
        Assert.False(string.IsNullOrWhiteSpace(session.StatusReason));

        session.Apply(Event("session.disconnected", 2));
        Assert.False(string.IsNullOrWhiteSpace(session.StatusReason));
    }
}
