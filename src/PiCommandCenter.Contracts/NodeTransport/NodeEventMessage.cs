namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>
/// Transport message: one node event inside a batch (protocolVersion 1).
/// </summary>
public sealed record NodeEventMessage(
    string EventId,
    Guid NodeId,
    Guid ProjectId,
    Guid? RequestId,
    string? SessionId,
    long Sequence,
    string Type,
    DateTimeOffset OccurredAt,
    string PayloadJson);
