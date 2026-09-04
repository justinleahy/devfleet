using PiCommandCenter.Domain;

namespace PiCommandCenter.Application.Transport;

/// <summary>
/// A single node event destined for the Control Plane.
/// </summary>
public sealed record NodeEventDto(
    string EventId,
    Guid NodeId,
    Guid ProjectId,
    Guid? RequestId,
    string? SessionId,
    long Sequence,
    string Type,
    DateTimeOffset OccurredAt,
    string PayloadJson);
