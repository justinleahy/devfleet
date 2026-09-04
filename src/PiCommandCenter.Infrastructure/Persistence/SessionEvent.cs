namespace PiCommandCenter.Infrastructure.Persistence;

/// <summary>
/// Persistence record of a node event received by the Control Plane. The globally unique
/// <see cref="EventId"/> is the primary key, which makes duplicate batch deliveries inert:
/// a repeated insert violates the key and the original row is acknowledged unchanged.
/// Timestamps are stored as UTC ticks.
/// </summary>
public sealed class SessionEvent
{
    public string EventId { get; init; } = string.Empty;

    public Guid NodeId { get; init; }

    public Guid ProjectId { get; init; }

    public Guid? RequestId { get; init; }

    public string? SessionId { get; init; }

    public long Sequence { get; init; }

    public string Type { get; init; } = string.Empty;

    public long OccurredAtUtcTicks { get; init; }

    public long ReceivedAtUtcTicks { get; init; }

    public string PayloadJson { get; init; } = string.Empty;
}
