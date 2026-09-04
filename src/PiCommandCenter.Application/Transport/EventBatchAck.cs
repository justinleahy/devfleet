namespace PiCommandCenter.Application.Transport;

/// <summary>
/// Acknowledgement of the event ids the Control Plane durably accepted; the node deletes exactly
/// these ids from its local spool.
/// </summary>
public sealed record EventBatchAck(IReadOnlyList<string> EventIds);
