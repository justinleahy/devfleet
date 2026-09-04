using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node;

/// <summary>
/// Durable local spool for events that have not yet been acknowledged by the Control Plane.
/// Appends are idempotent by event id; deletion happens only for exact acknowledged ids.
/// </summary>
public interface INodeEventSpool : IAsyncDisposable
{
    /// <summary>Appends an event, ignoring duplicates by event id.</summary>
    Task AppendAsync(NodeEventMessage message, CancellationToken cancellationToken);

    /// <summary>Peeks the oldest pending events in insertion order without deleting them.</summary>
    Task<IReadOnlyList<NodeEventMessage>> PeekPendingAsync(int max, CancellationToken cancellationToken);

    /// <summary>Deletes exactly the acknowledged event ids.</summary>
    Task DeleteAsync(IReadOnlyCollection<string> eventIds, CancellationToken cancellationToken);
}
