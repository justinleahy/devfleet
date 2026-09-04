using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node;

namespace PiCommandCenter.Node.Tests;

public class SqliteNodeEventSpoolTests : IDisposable
{
    private readonly string _spoolPath;

    public SqliteNodeEventSpoolTests()
    {
        _spoolPath = Path.Combine(
            Path.GetTempPath(), "pi-cc-node-tests", Guid.NewGuid().ToString("N"), "spool.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_spoolPath)!);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_spoolPath)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private SqliteNodeEventSpool CreateSpool() =>
        new(Options.Create(new NodeOptions { EventSpoolPath = _spoolPath }));

    private static NodeEventMessage MakeEvent(string eventId, long sequence) => new(
        eventId,
        Guid.NewGuid(),
        Guid.NewGuid(),
        RequestId: null,
        SessionId: "session-1",
        sequence,
        "session.log",
        new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero).AddSeconds(sequence),
        "{\"line\":\"" + sequence + "\"}");


    [Fact]
    public async Task Appended_events_survive_a_spool_restart()
    {
        var message = MakeEvent("evt-1", sequence: 1);

        await using (var spool = CreateSpool())
        {
            await spool.AppendAsync(message, CancellationToken.None);
        }

        // A brand-new spool instance over the same file replays the persisted event.
        await using var reopened = CreateSpool();
        var pending = await reopened.PeekPendingAsync(10, CancellationToken.None);

        var replayed = Assert.Single(pending);
        Assert.Equal(message, replayed);
    }

    [Fact]
    public async Task Peek_returns_events_in_insertion_order_without_deleting_them()
    {
        await using var spool = CreateSpool();
        foreach (var sequence in (long[]) [3, 1, 2])
        {
            await spool.AppendAsync(MakeEvent("evt-" + sequence, sequence), CancellationToken.None);
        }

        var firstPass = await spool.PeekPendingAsync(2, CancellationToken.None);
        var secondPass = await spool.PeekPendingAsync(10, CancellationToken.None);

        Assert.Equal(["evt-3", "evt-1"], firstPass.Select(e => e.EventId));
        Assert.Equal(["evt-3", "evt-1", "evt-2"], secondPass.Select(e => e.EventId));
    }

    [Fact]
    public async Task Appending_the_same_event_id_twice_does_not_duplicate_it()
    {
        await using var spool = CreateSpool();
        var message = MakeEvent("evt-dup", sequence: 1);

        await spool.AppendAsync(message, CancellationToken.None);
        await spool.AppendAsync(message with { Sequence = 99 }, CancellationToken.None);

        var pending = await spool.PeekPendingAsync(10, CancellationToken.None);
        var stored = Assert.Single(pending);
        Assert.Equal(1, stored.Sequence);
    }

    [Fact]
    public async Task Delete_removes_exactly_the_acknowledged_ids()
    {
        await using var spool = CreateSpool();
        foreach (var sequence in (long[]) [1, 2, 3])
        {
            await spool.AppendAsync(MakeEvent("evt-" + sequence, sequence), CancellationToken.None);
        }

        // Simulate the worker: only the acknowledged subset leaves the spool.
        await spool.DeleteAsync(["evt-1", "evt-3", "evt-1", "evt-unknown"], CancellationToken.None);

        var pending = await spool.PeekPendingAsync(10, CancellationToken.None);
        var remaining = Assert.Single(pending);
        Assert.Equal("evt-2", remaining.EventId);
    }

    [Fact]
    public async Task Delete_with_no_ids_is_a_no_op()
    {
        await using var spool = CreateSpool();
        await spool.AppendAsync(MakeEvent("evt-1", 1), CancellationToken.None);

        await spool.DeleteAsync([], CancellationToken.None);

        Assert.Single(await spool.PeekPendingAsync(10, CancellationToken.None));
    }
}
