namespace PiCommandCenter.Domain.Sessions;

/// <summary>
/// The single normalized envelope every runtime and supervisor converts its native activity
/// into (SPEC §22). Idempotent by <see cref="EventId"/>; <see cref="Sequence"/> is strictly
/// increasing per session. Unknown types and payload properties are preserved, never rejected.
/// </summary>
public sealed record NormalizedAgentEvent(
    int ProtocolVersion,
    string EventId,
    string NodeId,
    string ProjectId,
    string RequestId,
    string SessionId,
    string? ParentSessionId,
    long Sequence,
    string Runtime,
    string Type,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, object?> Payload);
