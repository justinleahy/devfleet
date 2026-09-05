using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;

namespace PiCommandCenter.Domain.Tests.Sessions;

public class AgentSessionTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    private static AgentSession StartSession() => AgentSession.Start(
        id: "session-root",
        projectId: new ProjectId(Guid.NewGuid()),
        requestId: WorkRequestId.New(),
        parentSessionId: null,
        agentName: "root",
        role: "root",
        runtime: "codex",
        model: "codex/gpt-6-astra",
        startedAt: StartedAt);

    private static NormalizedAgentEvent Event(
        string type,
        long sequence,
        DateTimeOffset? occurredAt = null,
        string? eventId = null,
        IReadOnlyDictionary<string, object?>? payload = null) => new(
        ProtocolVersion: 1,
        EventId: eventId ?? $"{type}-{sequence}",
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

    private static Dictionary<string, object?> Payload(params (string Key, object? Value)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Value);

    [Fact]
    public void Start_opens_a_session_in_Starting_Queued_Idle_None()
    {
        var session = StartSession();

        Assert.Equal(AgentLiveness.Starting, session.Liveness);
        Assert.Equal(AgentWorkState.Queued, session.WorkState);
        Assert.Equal(AgentActivity.Idle, session.Activity);
        Assert.Equal(AgentAttention.None, session.Attention);
        Assert.Null(session.ProviderSessionId);
        Assert.Null(session.EndedAt);
        Assert.Equal(1, session.Version);
        Assert.False(session.IsTerminal);
    }

    [Fact]
    public void Start_rejects_empty_identifiers()
    {
        Assert.Throws<ArgumentException>(() => AgentSession.Start(
            " ", new ProjectId(Guid.NewGuid()), WorkRequestId.New(), null,
            "root", "root", "codex", "codex/gpt-6-astra", StartedAt));
        Assert.Throws<ArgumentException>(() => AgentSession.Start(
            "session-root", new ProjectId(Guid.NewGuid()), WorkRequestId.New(), null,
            "", "root", "codex", "codex/gpt-6-astra", StartedAt));
        Assert.Throws<ArgumentException>(() => AgentSession.Start(
            "session-root", new ProjectId(Guid.NewGuid()), WorkRequestId.New(), null,
            "root", "root", "codex", " ", StartedAt));
    }

    [Fact]
    public void Start_trims_runtime_and_model()
    {
        var session = AgentSession.Start(
            "session-root", new ProjectId(Guid.NewGuid()), WorkRequestId.New(), null,
            "root", "root", " codex ", " codex/gpt-6-astra ", StartedAt);

        Assert.Equal("codex", session.Runtime);
        Assert.Equal("codex/gpt-6-astra", session.Model);
    }

    [Fact]
    public void Registration_moves_the_session_online_and_records_provider_state()
    {
        var session = StartSession();

        session.Apply(Event("session.registered", 1, payload: Payload(
            ("providerSessionId", "prov-1"),
            ("processId", 4242))));

        Assert.Equal(AgentLiveness.Online, session.Liveness);
        Assert.Equal(AgentWorkState.Starting, session.WorkState);
        Assert.Equal("prov-1", session.ProviderSessionId);
        Assert.Equal(4242, session.ProcessId);
        Assert.Equal(StartedAt.AddSeconds(1), session.LastHeartbeatAt);
    }

    [Fact]
    public void Tool_lifecycle_drives_activity_and_current_operation()
    {
        var session = Registered();

        session.Apply(Event("tool.started", 2, payload: Payload(("tool", "read_agent_inbox"))));
        Assert.Equal(AgentActivity.RunningTool, session.Activity);
        Assert.Equal("read_agent_inbox", session.CurrentOperation);

        session.Apply(Event("tool.completed", 3));
        Assert.Equal(AgentActivity.Reasoning, session.Activity);
        Assert.Null(session.CurrentOperation);
    }

    [Fact]
    public void Turn_lifecycle_is_the_only_implicit_path_back_to_Idle()
    {
        var session = Registered();

        session.Apply(Event("turn.started", 2));
        Assert.Equal(AgentActivity.Reasoning, session.Activity);
        Assert.Equal(AgentWorkState.Executing, session.WorkState);

        session.Apply(Event("tool.started", 3));
        session.Apply(Event("tool.completed", 4));

        // Silence alone must not infer Idle — the dimensions survive tool completion.
        Assert.NotEqual(AgentActivity.Idle, session.Activity);

        session.Apply(Event("turn.completed", 5));
        Assert.Equal(AgentActivity.Idle, session.Activity);
        Assert.Equal(AgentAttention.None, session.Attention);
    }

    [Fact]
    public void A_snapshot_is_authoritative_and_may_set_Idle()
    {
        var session = Registered();

        session.Apply(Event("tool.started", 2));

        session.Apply(Event("session.snapshot", 3, payload: Payload(
            ("liveness", "Online"),
            ("activity", "Idle"),
            ("attention", "None"),
            ("workState", "Executing"))));

        Assert.Equal(AgentActivity.Idle, session.Activity);
        Assert.Equal(AgentWorkState.Executing, session.WorkState);
    }

    [Fact]
    public void Failure_and_cancellation_are_distinct_terminal_dimensions()
    {
        var failed = Registered();
        failed.Apply(Event("session.failed", 2, payload: Payload(("error", "worker crashed"))));
        Assert.Equal(AgentLiveness.Exited, failed.Liveness);
        Assert.Equal(AgentWorkState.Failed, failed.WorkState);
        Assert.Equal(AgentAttention.Error, failed.Attention);
        Assert.Equal(StartedAt.AddSeconds(2), failed.EndedAt);
        Assert.True(failed.IsTerminal);

        var cancelled = Registered();
        cancelled.Apply(Event("session.cancelled", 2));
        Assert.Equal(AgentWorkState.Cancelled, cancelled.WorkState);
        Assert.True(cancelled.IsTerminal);
    }

    [Fact]
    public void Applying_the_same_event_id_twice_is_inert()
    {
        var session = Registered();
        session.Apply(Event("tool.started", 2, eventId: "dup"));
        var afterFirst = (session.Activity, session.CurrentOperation, session.Version, session.LastSequence);

        // Same event id redelivered with different content must not re-apply.
        session.Apply(Event("turn.completed", 3, eventId: "dup"));

        Assert.Equal(afterFirst, (session.Activity, session.CurrentOperation, session.Version, session.LastSequence));
    }

    [Fact]
    public void Events_at_or_below_the_last_sequence_are_ignored()
    {
        var session = Registered();
        session.Apply(Event("turn.started", 5));

        session.Apply(Event("session.registered", 5));
        session.Apply(Event("tool.started", 4));
        session.Apply(Event("tool.started", 1));

        Assert.Equal(AgentActivity.Reasoning, session.Activity);
        Assert.Equal(5, session.LastSequence);
    }

    [Fact]
    public void Unknown_event_types_are_recorded_but_change_no_status()
    {
        var session = Registered();
        var before = (session.Liveness, session.Activity, session.Attention, session.WorkState, session.CurrentOperation);

        session.Apply(Event("warp_drive.engaged", 2, payload: Payload(("anything", true))));

        Assert.Equal(before, (session.Liveness, session.Activity, session.Attention, session.WorkState, session.CurrentOperation));
        Assert.Equal(2, session.LastSequence);
        Assert.Equal(3, session.Version);
    }

    [Fact]
    public void Events_before_the_session_started_are_ignored()
    {
        var session = StartSession();

        session.Apply(Event("session.registered", 1, occurredAt: StartedAt.AddSeconds(-30)));

        Assert.Equal(AgentLiveness.Starting, session.Liveness);
        Assert.Equal(0, session.LastSequence);
    }

    [Fact]
    public void A_terminal_session_rejects_new_mutating_transitions()
    {
        var session = Registered();
        session.Apply(Event("session.closed", 2));

        Assert.Throws<InvalidOperationException>(() => session.Apply(Event("turn.started", 3)));
    }

    [Fact]
    public void Rehydrate_restores_persisted_state_without_mutating_version()
    {
        var session = AgentSession.Rehydrate(
            id: "session-root",
            projectId: new ProjectId(Guid.NewGuid()),
            requestId: WorkRequestId.New(),
            parentSessionId: null,
            agentName: "root",
            role: "root",
            runtime: "codex",
            model: "codex/gpt-6-astra",
            providerSessionId: "prov-9",
            liveness: AgentLiveness.Exited,
            activity: AgentActivity.Idle,
            attention: AgentAttention.Error,
            workState: AgentWorkState.Failed,
            statusReason: "Session failed",
            currentOperation: null,
            processId: 4242,
            startedAt: StartedAt,
            lastHeartbeatAt: StartedAt.AddMinutes(1),
            endedAt: StartedAt.AddMinutes(2),
            lastSequence: 42,
            version: 9);

        Assert.Equal(9, session.Version);
        Assert.Equal(42, session.LastSequence);
        Assert.True(session.IsTerminal);
        Assert.Equal("prov-9", session.ProviderSessionId);
    }

    [Fact]
    public void Rehydrate_rejects_a_heartbeat_or_end_before_the_start()
    {
        Assert.Throws<ArgumentException>(() => AgentSession.Rehydrate(
            "s", new ProjectId(Guid.NewGuid()), WorkRequestId.New(), null, "root", "root",
            "codex", "codex/gpt-6-astra", null, AgentLiveness.Exited, AgentActivity.Idle, AgentAttention.None,
            AgentWorkState.Completed, "done", null, null, StartedAt, null, StartedAt.AddSeconds(-1), 1, 1));
        Assert.Throws<ArgumentException>(() => AgentSession.Rehydrate(
            "s", new ProjectId(Guid.NewGuid()), WorkRequestId.New(), null, "root", "root",
            "codex", "codex/gpt-6-astra", null, AgentLiveness.Online, AgentActivity.Idle, AgentAttention.None,
            AgentWorkState.Executing, "busy", null, null, StartedAt, StartedAt.AddSeconds(-1), null, 1, 1));
    }

    private static AgentSession Registered()
    {
        var session = StartSession();
        session.Apply(Event("session.registered", 1));
        return session;
    }
}
