using PiCommandCenter.Application.Transport;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Transport;

namespace PiCommandCenter.Infrastructure.Tests.Transport;

public class NodeEventSinkTests : IDisposable
{
    private readonly string _sqlitePath = TestRepositories.CreateSqliteFile();
    private readonly FakeTimeProvider _clock = TestNodes.Clock();

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
        var sink = new NodeEventSink(_clock, db);
        var events = new[] { MakeEvent(sequence: 1), MakeEvent(sequence: 2) };

        var ack = await sink.AppendAsync(new EventBatch(events));

        Assert.Equal(events.Select(e => e.EventId), ack.EventIds);
        Assert.Equal(2, db.SessionEvents.Count());
    }

    [Fact]
    public async Task Reappending_a_known_event_is_idempotent_but_still_acknowledged()
    {
        await using var db = CreateContext();
        var sink = new NodeEventSink(_clock, db);
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
        var sink = new NodeEventSink(_clock, db);
        var shared = MakeEvent(eventId: "evt-dup", sequence: 1);

        var ack = await sink.AppendAsync(new EventBatch([shared, shared]));

        Assert.Equal(["evt-dup", "evt-dup"], ack.EventIds);
        Assert.Single(db.SessionEvents);
    }

    [Fact]
    public async Task Append_persists_the_occurrence_and_payload_verbatim()
    {
        await using var db = CreateContext();
        var sink = new NodeEventSink(_clock, db);
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
        var sink = new NodeEventSink(_clock, db);

        var ack = await sink.AppendAsync(new EventBatch([]));

        Assert.Empty(ack.EventIds);
    }
}
