using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Transport;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Sessions;

namespace PiCommandCenter.Infrastructure.Transport;

/// <summary>
/// EF Core backed event sink. Events are appended transactionally; the primary key on
/// <c>SessionEvents.EventId</c> makes redelivery idempotent — duplicates are skipped, and
/// every delivered id (new and duplicate) is acknowledged exactly once per batch. Each newly
/// appended event is, in the same <c>SaveChanges</c> transaction, applied to the
/// <c>AgentSessions</c> projection: recognized lifecycle types drive the aggregate's status
/// dimensions, unknown types are stored but change no status, and a duplicate event id never
/// re-applies.
/// </summary>
public sealed class NodeEventSink(TimeProvider clock, ControlPlaneDbContext db) : INodeEventSink
{
    public async Task<EventBatchAck> AppendAsync(EventBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var eventIds = batch.Events.Select(e => e.EventId).ToList();
        if (eventIds.Count == 0)
        {
            return new EventBatchAck(eventIds);
        }

        var knownIds = await db.SessionEvents
            .Where(e => eventIds.Contains(e.EventId))
            .Select(e => e.EventId)
            .ToListAsync(cancellationToken);
        var knownIdSet = knownIds.ToHashSet();

        var receivedAtUtcTicks = clock.GetUtcNow().UtcTicks;
        foreach (var nodeEvent in batch.Events)
        {
            if (string.IsNullOrWhiteSpace(nodeEvent.EventId) || knownIdSet.Contains(nodeEvent.EventId))
            {
                continue;
            }

            db.SessionEvents.Add(new SessionEvent
            {
                EventId = nodeEvent.EventId,
                NodeId = nodeEvent.NodeId,
                ProjectId = nodeEvent.ProjectId,
                RequestId = nodeEvent.RequestId,
                SessionId = nodeEvent.SessionId,
                Sequence = nodeEvent.Sequence,
                Type = nodeEvent.Type,
                OccurredAtUtcTicks = nodeEvent.OccurredAt.UtcTicks,
                ReceivedAtUtcTicks = receivedAtUtcTicks,
                PayloadJson = nodeEvent.PayloadJson,
            });

            // Mark the id seen so a second event with the same id inside this batch is
            // collapsed instead of colliding on the tracked primary key.
            knownIdSet.Add(nodeEvent.EventId);

            // Projection transition for the same event, committed atomically with the append.
            if (!string.IsNullOrWhiteSpace(nodeEvent.SessionId))
            {
                var normalized = AgentSessionProjector.ToNormalizedEvent(nodeEvent);
                await AgentSessionProjector.ApplyAsync(db, normalized, cancellationToken);
            }
        }

        // A single SaveChanges persists all new rows and projection transitions in one transaction.
        await db.SaveChangesAsync(cancellationToken);

        // Duplicate deliveries are acknowledged like fresh events: the sender spool clears.
        return new EventBatchAck(eventIds);
    }
}
