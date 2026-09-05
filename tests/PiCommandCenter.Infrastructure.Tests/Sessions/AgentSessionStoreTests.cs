using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Sessions;
using PiCommandCenter.Application.Transport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Sessions;

namespace PiCommandCenter.Infrastructure.Tests.Sessions;

public class AgentSessionStoreTests : IDisposable
{
    private readonly string _sqlitePath = TestRepositories.CreateSqliteFile();
    private static readonly DateTimeOffset Base = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private ControlPlaneDbContext CreateContext() => TestRepositories.CreateContext(_sqlitePath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_sqlitePath)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private Guid _nodeId;
    private Guid _projectId;
    private Guid _requestId;

    private NormalizedAgentEvent Event(
        string type,
        long sequence,
        string sessionId = "session-root",
        string? eventId = null,
        DateTimeOffset? occurredAt = null,
        IReadOnlyDictionary<string, object?>? payload = null) => new(
        ProtocolVersion: 1,
        EventId: eventId ?? $"evt-{type}-{sequence}",
        NodeId: _nodeId.ToString("D"),
        ProjectId: _projectId.ToString("D"),
        RequestId: _requestId.ToString("D"),
        SessionId: sessionId,
        ParentSessionId: sessionId == "session-root" ? null : "session-root",
        Sequence: sequence,
        Runtime: "pi",
        Type: type,
        OccurredAt: occurredAt ?? Base.AddSeconds(sequence),
        Payload: payload ?? new Dictionary<string, object?>());

    private async Task<IAgentSessionStore> CreateStoreAsync(ControlPlaneDbContext db)
    {
        _nodeId = TestNodes.NewNodeId().Value;
        var clock = TestNodes.Clock();
        TestNodes.SeedNode(db, new NodeId(_nodeId), clock);
        var project = TestNodes.SeedProject(db, new NodeId(_nodeId), clock);
        _projectId = project.Id.Value;
        var request = TestNodes.SeedRequest(db, project, clock);
        _requestId = request.Id.Value;
        await TestNodes.SaveAsync(db);
        return new AgentSessionStore(TimeProvider.System, db, new PiCommandCenter.Application.Live.ProjectionNotifier());
    }

    [Fact]
    public async Task ApplyAsync_creates_the_projection_from_registration_and_persists_the_event()
    {
        await using var db = CreateContext();
        var store = await CreateStoreAsync(db);

        await store.ApplyAsync(Event("session.registered", 1, payload: new Dictionary<string, object?>
        {
            ["agentName"] = "root",
            ["role"] = "root",
            ["model"] = "codex/root-readonly",
            ["providerSessionId"] = "prov-1",
        }));

        Assert.Equal(1, await db.AgentSessions.AsNoTracking().CountAsync());
        var session = await store.GetAsync("session-root");
        Assert.NotNull(session);
        Assert.Equal("root", session.AgentName);
        Assert.Equal("root", session.Role);
        Assert.Equal("codex/root-readonly", session.Model);
        Assert.Equal("pi", session.Runtime);
        Assert.Equal(AgentLiveness.Online, session.Liveness);
        Assert.Equal("prov-1", session.ProviderSessionId);

        var events = await store.ListEventsAsync(new WorkRequestId(_requestId));
        var single = Assert.Single(events);
        Assert.Equal("session.registered", single.Type);
        Assert.Equal(1, single.Sequence);
    }

    [Fact]
    public async Task ApplyAsync_is_idempotent_on_duplicate_event_ids()
    {
        await using var db = CreateContext();
        var store = await CreateStoreAsync(db);
        var registered = Event("session.registered", 1);

        await store.ApplyAsync(registered);
        await store.ApplyAsync(registered);
        // Replayed with a different object instance but the same event id: still inert.
        await store.ApplyAsync(Event("session.registered", 1, eventId: registered.EventId));

        await using var verify = CreateContext();
        Assert.Equal(1, await verify.SessionEvents.CountAsync());
        Assert.Equal(1, await verify.AgentSessions.CountAsync());
        var row = await verify.AgentSessions.SingleAsync();
        Assert.Equal(1, row.LastSequence);
    }

    [Fact]
    public async Task Stale_sequences_do_not_move_the_projection_backwards()
    {
        await using var db = CreateContext();
        var store = await CreateStoreAsync(db);

        await store.ApplyAsync(Event("session.registered", 10));
        await store.ApplyAsync(Event("tool.started", 5, payload: new Dictionary<string, object?> { ["tool"] = "write" }));

        var session = await store.GetAsync("session-root");
        Assert.NotNull(session);
        Assert.Equal(10, db.AgentSessions.AsNoTracking().Single().LastSequence);
        Assert.NotEqual(AgentActivity.RunningTool, session.Activity);
    }

    [Fact]
    public async Task Unknown_event_types_are_stored_but_change_no_status()
    {
        await using var db = CreateContext();
        var store = await CreateStoreAsync(db);

        await store.ApplyAsync(Event("session.registered", 1));
        var before = await store.GetAsync("session-root");

        await store.ApplyAsync(Event("mystery.custom_event", 2, payload: new Dictionary<string, object?>
        {
            ["weird"] = "value",
            ["count"] = 7,
        }));

        var after = await store.GetAsync("session-root");
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal((before.Liveness, before.Activity, before.Attention, before.WorkState, before.CurrentOperation),
            (after.Liveness, after.Activity, after.Attention, after.WorkState, after.CurrentOperation));

        // The unknown event is still on the timeline with its sequence preserved.
        var events = await store.ListEventsAsync(new WorkRequestId(_requestId));
        var stored = Assert.Single(events, e => e.Type == "mystery.custom_event");
        Assert.Equal(2, stored.Sequence);
    }

    [Fact]
    public async Task Failure_event_projected_through_the_store_marks_the_session_failed()
    {
        await using var db = CreateContext();
        var store = await CreateStoreAsync(db);

        await store.ApplyAsync(Event("session.registered", 1));
        await store.ApplyAsync(Event("session.failed", 2, payload: new Dictionary<string, object?>
        {
            ["error"] = "worker process exited with code 1",
        }));

        var session = await store.GetAsync("session-root");
        Assert.NotNull(session);
        Assert.Equal(AgentLiveness.Exited, session.Liveness);
        Assert.Equal(AgentWorkState.Failed, session.WorkState);
        Assert.Equal(AgentAttention.Error, session.Attention);
        Assert.NotNull(session.EndedAt);
    }

    [Fact]
    public async Task ListAsync_orders_root_first_then_children_by_start_time()
    {
        await using var db = CreateContext();
        var store = await CreateStoreAsync(db);

        await store.ApplyAsync(Event("session.registered", 1, sessionId: "session-root"));
        await store.ApplyAsync(Event("session.registered", 2, sessionId: "session-child-b",
            occurredAt: Base.AddSeconds(20),
            payload: new Dictionary<string, object?> { ["agentName"] = "reviewer", ["role"] = "reviewer" }));
        await store.ApplyAsync(Event("session.registered", 3, sessionId: "session-child-a",
            occurredAt: Base.AddSeconds(10),
            payload: new Dictionary<string, object?> { ["agentName"] = "implementer", ["role"] = "implementer" }));

        var sessions = await store.ListAsync(new WorkRequestId(_requestId));

        Assert.Equal(3, sessions.Count);
        Assert.Equal("session-root", sessions[0].Id);
        Assert.Null(sessions[0].ParentSessionId);
        Assert.Equal("session-child-b", sessions[2].Id);
    }

    [Fact]
    public async Task GetAsync_returns_null_for_unknown_or_blank_session_ids()
    {
        await using var db = CreateContext();
        var store = await CreateStoreAsync(db);

        Assert.Null(await store.GetAsync("nope"));
        Assert.Null(await store.GetAsync(""));
    }

    [Fact]
    public async Task Events_for_an_unregistered_session_are_stored_without_projection()
    {
        await using var db = CreateContext();
        var store = await CreateStoreAsync(db);

        await store.ApplyAsync(Event("tool.started", 1, sessionId: "session-ghost"));

        Assert.Null(await store.GetAsync("session-ghost"));
        Assert.Equal(0, await db.AgentSessions.AsNoTracking().CountAsync());
        var events = await store.ListEventsAsync(new WorkRequestId(_requestId));
        Assert.Single(events);
    }

    [Fact]
    public void ToNormalizedEvent_maps_the_spool_envelope_and_defaults_the_runtime()
    {
        var spoolEvent = new NodeEventDto(
            EventId: "evt-1",
            NodeId: Guid.NewGuid(),
            ProjectId: Guid.NewGuid(),
            RequestId: Guid.NewGuid(),
            SessionId: "session-root",
            Sequence: 147,
            Type: "tool.started",
            OccurredAt: Base,
            PayloadJson: """{"tool":"inspect_project_diff","parentSessionId":null}""");

        var normalized = AgentSessionProjector.ToNormalizedEvent(spoolEvent);

        Assert.Equal(1, normalized.ProtocolVersion);
        Assert.Equal("evt-1", normalized.EventId);
        Assert.Equal(spoolEvent.NodeId.ToString("D"), normalized.NodeId);
        Assert.Equal(spoolEvent.RequestId!.Value.ToString("D"), normalized.RequestId);
        Assert.Equal(147, normalized.Sequence);
        Assert.Equal("pi", normalized.Runtime);
        Assert.Equal(Base, normalized.OccurredAt);
        Assert.Equal("inspect_project_diff", normalized.Payload["tool"]);
        Assert.Null(normalized.ParentSessionId);
    }

    [Fact]
    public void ToNormalizedEvent_survives_malformed_and_oversized_payloads()
    {
        var malformed = new NodeEventDto(
            "evt-bad", Guid.NewGuid(), Guid.NewGuid(), null, "session-root", 1,
            "tool.started", Base, "{\"tool\": not json}");
        var oversizedJson = "{\"tool\":\"read\",\"pad\":\"" + new string('x', 70_000) + "\"}";
        var oversized = new NodeEventDto(
            "evt-big", Guid.NewGuid(), Guid.NewGuid(), null, "session-root", 2,
            "tool.started", Base, oversizedJson);

        var malformedNormalized = AgentSessionProjector.ToNormalizedEvent(malformed);
        var oversizedNormalized = AgentSessionProjector.ToNormalizedEvent(oversized);

        Assert.Empty(malformedNormalized.Payload);
        Assert.Empty(oversizedNormalized.Payload);
        Assert.Equal("pi", malformedNormalized.Runtime);
    }

    [Fact]
    public void ToNormalizedEvent_uses_a_declared_runtime_hint_when_present()
    {
        var spoolEvent = new NodeEventDto(
            "evt-2", Guid.NewGuid(), Guid.NewGuid(), null, "session-root", 1,
            "session.heartbeat", Base, """{"runtime":"claude-code"}""");

        Assert.Equal("claude-code", AgentSessionProjector.ToNormalizedEvent(spoolEvent).Runtime);
    }
}
