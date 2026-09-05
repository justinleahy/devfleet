using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Transport;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Sessions;
using PiCommandCenter.Infrastructure.Requests;

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
public sealed class NodeEventSink(
    TimeProvider clock,
    ControlPlaneDbContext db,
    IProjectionNotifier? notifier = null) : INodeEventSink
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
        var touched = new HashSet<(Guid ProjectId, Guid RequestId)>();
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

            if (nodeEvent.RequestId is not null)
            {
                await WorkRequestProjector.ApplyAsync(db, nodeEvent, cancellationToken);
            }

            touched.Add((nodeEvent.ProjectId, nodeEvent.RequestId ?? Guid.Empty));
        }

        // A single SaveChanges persists all new rows and projection transitions in one transaction.
        await db.SaveChangesAsync(cancellationToken);

        // Published only after the transaction commits, so a live view that re-reads on the
        // signal can never observe state older than the change that woke it.
        if (notifier is not null)
        {
            foreach (var (projectId, requestId) in touched)
            {
                notifier.Publish(requestId == Guid.Empty
                    ? ProjectionChange.Project(projectId)
                    : ProjectionChange.Request(projectId, requestId));
            }
        }

        // Duplicate deliveries are acknowledged like fresh events: the sender spool clears.
        return new EventBatchAck(eventIds);
    }
}
