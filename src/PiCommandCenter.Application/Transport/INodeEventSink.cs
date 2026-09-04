namespace PiCommandCenter.Application.Transport;

/// <summary>
/// Durable sink for node events. Append is idempotent per <see cref="NodeEventDto.EventId"/>:
/// replaying a batch already accepted returns its acknowledgement without duplicating events.
/// </summary>
public interface INodeEventSink
{
    /// <summary>
    /// Appends the batch and returns the acknowledgement of durably accepted event ids. Throws
    /// <see cref="ArgumentException"/> when any event violates its invariants.
    /// </summary>
    Task<EventBatchAck> AppendAsync(EventBatch batch, CancellationToken cancellationToken = default);
}
