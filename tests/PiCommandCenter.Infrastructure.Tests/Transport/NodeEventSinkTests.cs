using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Transport;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Transport;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Application.Live;

namespace PiCommandCenter.Infrastructure.Tests.Transport;

public class NodeEventSinkTests : IDisposable
{
    private readonly string _sqlitePath = TestRepositories.CreateSqliteFile();
    private readonly FakeTimeProvider _clock = TestNodes.Clock();
    private readonly IProjectionNotifier _notifier = new ProjectionNotifier();

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

    private NodeEventDto MakeEvent(
        string? eventId = null,
        long sequence = 1,
        string type = "session.log") =>
        new(
            eventId ?? Guid.NewGuid().ToString(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            RequestId: null,
            SessionId: "session-1",
            sequence,
            type,
            TestNodes.Start,
            "{\"line\":\"hello\"}");

    [Fact]
    public async Task Append_persists_every_event_and_acknowledges_their_ids()
    {
        await using var db = CreateContext();
        var sink = new NodeEventSink(_clock, db, _notifier);
        var events = new[] { MakeEvent(sequence: 1), MakeEvent(sequence: 2) };

        var ack = await sink.AppendAsync(new EventBatch(events));

        Assert.Equal(events.Select(e => e.EventId), ack.EventIds);
        Assert.Equal(2, db.SessionEvents.Count());
    }

    [Fact]
    public async Task Reappending_a_known_event_is_idempotent_but_still_acknowledged()
    {
        await using var db = CreateContext();
        var sink = new NodeEventSink(_clock, db, _notifier);
        var first = MakeEvent(eventId: "evt-1", sequence: 1);
        var second = MakeEvent(eventId: "evt-2", sequence: 2);
        _ = await sink.AppendAsync(new EventBatch([first]));
        _clock.Advance(TimeSpan.FromSeconds(1));

        var ack = await sink.AppendAsync(new EventBatch([first, second]));

        Assert.Equal(2, db.SessionEvents.Count());
        var replayed = db.SessionEvents.Single(e => e.EventId == "evt-1");
        Assert.True(replayed.Sequence == 1);
    }

    [Fact]
    public async Task Duplicates_inside_a_single_batch_are_collapsed()
    {
        await using var db = CreateContext();
        var sink = new NodeEventSink(_clock, db, _notifier);
        var shared = MakeEvent(eventId: "evt-dup", sequence: 1);

        var ack = await sink.AppendAsync(new EventBatch([shared, shared]));

        Assert.Equal(["evt-dup", "evt-dup"], ack.EventIds);
        Assert.Single(db.SessionEvents);
    }

    [Fact]
    public async Task Append_persists_the_occurrence_and_payload_verbatim()
    {
        await using var db = CreateContext();
        var sink = new NodeEventSink(_clock, db, _notifier);
        var payload = "{\"line\":\"exact\"}";
        var dto = new NodeEventDto(
            "evt-payload", Guid.NewGuid(), Guid.NewGuid(), null, "session-9", 7,
            "request.started", TestNodes.Start.AddMinutes(3), payload);

        _ = await sink.AppendAsync(new EventBatch([dto]));

        var stored = db.SessionEvents.Single(e => e.EventId == "evt-payload");
        Assert.Equal(payload, stored.PayloadJson);
        Assert.Equal("session-9", stored.SessionId);
        Assert.Equal(7, stored.Sequence);
        Assert.Equal("request.started", stored.Type);
        Assert.Equal(TestNodes.Start.AddMinutes(3).UtcTicks, stored.OccurredAtUtcTicks);
        Assert.Equal(_clock.GetUtcNow().UtcTicks, stored.ReceivedAtUtcTicks);
    }

    [Fact]
    public async Task An_empty_batch_acknowledges_nothing()
    {
        await using var db = CreateContext();
        var sink = new NodeEventSink(_clock, db, _notifier);

        var ack = await sink.AppendAsync(new EventBatch([]));

        Assert.Empty(ack.EventIds);
    }

    [Fact]
    public async Task Lifecycle_events_advance_the_work_request_to_verifying()
    {
        await using var db = CreateContext();
        var nodeId = TestNodes.NewNodeId();
        TestNodes.SeedNode(db, nodeId, _clock);
        var project = TestNodes.SeedProject(db, _clock);
        var request = TestNodes.SeedRequest(db, project, _clock);
        request.Start(_clock.GetUtcNow());
        await TestNodes.SaveAsync(db);

        var sink = new NodeEventSink(_clock, db, _notifier);
        var requestId = request.Id.Value;
        await sink.AppendAsync(new EventBatch(
        [
            Evt(requestId, project.Id.Value, nodeId.Value, "request.phase_changed", """{"phase":"plan"}"""),
            Evt(requestId, project.Id.Value, nodeId.Value, "child.started", """{"role":"implementer"}"""),
            Evt(requestId, project.Id.Value, nodeId.Value, "child.completed", """{"role":"reviewer"}"""),
            Evt(requestId, project.Id.Value, nodeId.Value, "verification.started", """{"profileId":"default"}"""),
        ]));

        var loaded = await db.WorkRequests.FindAsync(request.Id);
        Assert.Equal(WorkRequestStatus.Verifying, loaded!.Status);
    }

    [Fact]
    public async Task Out_of_order_verification_started_catch_up_to_verifying()
    {
        await using var db = CreateContext();
        var nodeId = TestNodes.NewNodeId();
        TestNodes.SeedNode(db, nodeId, _clock);
        var project = TestNodes.SeedProject(db, _clock);
        var request = TestNodes.SeedRequest(db, project, _clock);
        request.Start(_clock.GetUtcNow());
        await TestNodes.SaveAsync(db);

        var sink = new NodeEventSink(_clock, db, _notifier);
        await sink.AppendAsync(new EventBatch(
        [
            Evt(request.Id.Value, project.Id.Value, nodeId.Value, "verification.started", "{}"),
        ]));

        Assert.Equal(WorkRequestStatus.Verifying, (await db.WorkRequests.FindAsync(request.Id))!.Status);
    }

    [Theory]
    [InlineData("request.completed")]
    [InlineData("request.failed")]
    [InlineData("request.cancelled")]
    public async Task Terminal_events_are_history_only_and_never_terminalize_the_request(string type)
    {
        await using var db = CreateContext();
        var nodeId = TestNodes.NewNodeId();
        TestNodes.SeedNode(db, nodeId, _clock);
        var project = TestNodes.SeedProject(db, _clock);
        var request = TestNodes.SeedRequest(db, project, _clock);
        request.Start(_clock.GetUtcNow());
        await TestNodes.SaveAsync(db);

        var sink = new NodeEventSink(_clock, db, _notifier);
        var terminal = Evt(request.Id.Value, project.Id.Value, nodeId.Value, type, """{"reason":"test"}""");
        await sink.AppendAsync(new EventBatch([terminal]));

        // The event is durably recorded but status stays nonterminal: only the
        // terminalization authority may terminalize a request.
        Assert.Equal(1, db.SessionEvents.Count());
        Assert.Equal(WorkRequestStatus.Starting, (await db.WorkRequests.FindAsync(request.Id))!.Status);

        await sink.AppendAsync(new EventBatch([terminal]));
        Assert.Equal(1, db.SessionEvents.Count());
        Assert.Equal(WorkRequestStatus.Starting, (await db.WorkRequests.FindAsync(request.Id))!.Status);
    }

    [Fact]
    public async Task Terminal_completion_tail_is_persisted_without_changing_the_session_projection()
    {
        await using var db = CreateContext();
        var nodeId = TestNodes.NewNodeId();
        TestNodes.SeedNode(db, nodeId, _clock);
        var project = TestNodes.SeedProject(db, _clock);
        var request = TestNodes.SeedRequest(db, project, _clock);
        await TestNodes.SaveAsync(db);

        var sink = new NodeEventSink(_clock, db, _notifier);
        await sink.AppendAsync(new EventBatch(
        [
            Evt(request.Id.Value, project.Id.Value, nodeId.Value, "session.registered", "{}", sequence: 1),
        ]));
        await sink.AppendAsync(new EventBatch(
        [
            Evt(request.Id.Value, project.Id.Value, nodeId.Value, "tool.started", """{"tool":"write"}""", sequence: 2),
        ]));
        await sink.AppendAsync(new EventBatch(
        [
            Evt(request.Id.Value, project.Id.Value, nodeId.Value, "session.closed", "{}", sequence: 3),
        ]));
        var terminal = await db.AgentSessions.AsNoTracking().SingleAsync();
        var terminalProjection = (
            terminal.ProviderSessionId,
            terminal.Liveness,
            terminal.Activity,
            terminal.Attention,
            terminal.WorkState,
            terminal.StatusReason,
            terminal.CurrentOperation,
            terminal.ProcessId,
            terminal.LastHeartbeatAtUtcTicks,
            terminal.EndedAtUtcTicks,
            terminal.LastSequence,
            terminal.Version);
        var tail = new[]
        {
            Evt(request.Id.Value, project.Id.Value, nodeId.Value, "tool.completed", "{}", sequence: 4),
            Evt(request.Id.Value, project.Id.Value, nodeId.Value, "turn.completed", "{}", sequence: 5),
        };

        var acknowledgement = await sink.AppendAsync(new EventBatch(tail));

        var afterTail = await db.AgentSessions.AsNoTracking().SingleAsync();
        Assert.Equal(tail.Select(@event => @event.EventId), acknowledgement.EventIds);
        Assert.Equal(5, await db.SessionEvents.CountAsync());
        Assert.Equal(
            terminalProjection,
            (
                afterTail.ProviderSessionId,
                afterTail.Liveness,
                afterTail.Activity,
                afterTail.Attention,
                afterTail.WorkState,
                afterTail.StatusReason,
                afterTail.CurrentOperation,
                afterTail.ProcessId,
                afterTail.LastHeartbeatAtUtcTicks,
                afterTail.EndedAtUtcTicks,
                afterTail.LastSequence,
                afterTail.Version));
    }

    [Fact]
    public async Task Terminal_session_still_rejects_non_tail_mutations()
    {
        await using var db = CreateContext();
        var nodeId = TestNodes.NewNodeId();
        TestNodes.SeedNode(db, nodeId, _clock);
        var project = TestNodes.SeedProject(db, _clock);
        var request = TestNodes.SeedRequest(db, project, _clock);
        await TestNodes.SaveAsync(db);

        var sink = new NodeEventSink(_clock, db, _notifier);
        await sink.AppendAsync(new EventBatch(
        [
            Evt(request.Id.Value, project.Id.Value, nodeId.Value, "session.registered", "{}", sequence: 1),
        ]));
        await sink.AppendAsync(new EventBatch(
        [
            Evt(request.Id.Value, project.Id.Value, nodeId.Value, "session.closed", "{}", sequence: 2),
        ]));
        var forbidden = Evt(
            request.Id.Value,
            project.Id.Value,
            nodeId.Value,
            "tool.started",
            """{"tool":"write"}""",
            sequence: 3);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.AppendAsync(new EventBatch([forbidden])));

        Assert.Contains("cannot apply 'tool.started'", error.Message, StringComparison.Ordinal);
        await using var verify = CreateContext();
        Assert.DoesNotContain(verify.SessionEvents, @event => @event.EventId == forbidden.EventId);
    }

    [Fact]
    public async Task Late_plan_event_does_not_regress_executing()
    {
        await using var db = CreateContext();
        var nodeId = TestNodes.NewNodeId();
        TestNodes.SeedNode(db, nodeId, _clock);
        var project = TestNodes.SeedProject(db, _clock);
        var request = TestNodes.SeedRequest(db, project, _clock);
        request.Start(_clock.GetUtcNow());
        request.BeginPlanning(_clock.GetUtcNow());
        request.BeginExecuting(_clock.GetUtcNow());
        await TestNodes.SaveAsync(db);

        var sink = new NodeEventSink(_clock, db, _notifier);
        await sink.AppendAsync(new EventBatch(
        [
            Evt(request.Id.Value, project.Id.Value, nodeId.Value, "request.phase_changed", """{"phase":"plan"}"""),
        ]));

        Assert.Equal(WorkRequestStatus.Executing, (await db.WorkRequests.FindAsync(request.Id))!.Status);
    }

    [Fact]
    public async Task Startup_unblocked_event_restores_the_request_phase()
    {
        await using var db = CreateContext();
        var nodeId = TestNodes.NewNodeId();
        TestNodes.SeedNode(db, nodeId, _clock);
        var project = TestNodes.SeedProject(db, _clock);
        var request = TestNodes.SeedRequest(db, project, _clock);
        request.Block(_clock.GetUtcNow());
        await TestNodes.SaveAsync(db);

        var sink = new NodeEventSink(_clock, db, _notifier);
        await sink.AppendAsync(new EventBatch(
        [
            Evt(
                request.Id.Value,
                project.Id.Value,
                nodeId.Value,
                "request.unblocked",
                """{"status":"starting","phase":"root_start"}"""),
        ]));

        await using var reload = CreateContext();
        var persisted = await reload.WorkRequests.SingleAsync();
        Assert.Equal(WorkRequestStatus.Queued, persisted.Status);
        Assert.Null(persisted.BlockedPhase);
    }

    [Fact]
    public async Task Appended_events_publish_one_change_per_touched_request_after_the_commit()
    {
        await using var db = CreateContext();
        var nodeId = TestNodes.NewNodeId();
        TestNodes.SeedNode(db, nodeId, _clock);
        var project = TestNodes.SeedProject(db, _clock);
        var request = TestNodes.SeedRequest(db, project, _clock);
        await TestNodes.SaveAsync(db);

        var observed = new List<ProjectionChange>();
        var persistedWhenSignaled = 0;
        using var subscription = _notifier.Subscribe(change =>
        {
            observed.Add(change);
            persistedWhenSignaled = db.SessionEvents.Count();
        });

        var sink = new NodeEventSink(_clock, db, _notifier);
        await sink.AppendAsync(new EventBatch(
        [
            Evt(request.Id.Value, project.Id.Value, nodeId.Value, "session.registered", "{}"),
            Evt(request.Id.Value, project.Id.Value, nodeId.Value, "turn.started", "{}"),
        ]));

        // Two events, one request: subscribers are signaled once, and only after the rows exist.
        var change = Assert.Single(observed);
        Assert.True(change.AffectsRequest(request.Id.Value));
        Assert.True(change.AffectsProject(project.Id.Value));
        Assert.Equal(2, persistedWhenSignaled);
    }

    [Fact]
    public async Task A_duplicate_delivery_publishes_nothing()
    {
        await using var db = CreateContext();
        var nodeId = TestNodes.NewNodeId();
        TestNodes.SeedNode(db, nodeId, _clock);
        var project = TestNodes.SeedProject(db, _clock);
        var request = TestNodes.SeedRequest(db, project, _clock);
        await TestNodes.SaveAsync(db);

        var sink = new NodeEventSink(_clock, db, _notifier);
        var only = Evt(request.Id.Value, project.Id.Value, nodeId.Value, "session.registered", "{}");
        await sink.AppendAsync(new EventBatch([only]));

        var observed = new List<ProjectionChange>();
        using var subscription = _notifier.Subscribe(observed.Add);
        await sink.AppendAsync(new EventBatch([only]));

        Assert.Empty(observed);
    }

    private NodeEventDto Evt(
        Guid requestId,
        Guid projectId,
        Guid nodeId,
        string type,
        string payload,
        long sequence = 1) =>
        new(
            Guid.NewGuid().ToString("N"),
            nodeId,
            projectId,
            requestId,
            "session-root",
            sequence,
            type,
            TestNodes.Start,
            payload);
}
