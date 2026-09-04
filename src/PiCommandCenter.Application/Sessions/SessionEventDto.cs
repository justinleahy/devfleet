namespace PiCommandCenter.Application.Sessions;

/// <summary>
/// Read model of one persisted session event for the request/session timeline (SPEC §29,
/// SessionEvent). Payload is surfaced as stored JSON so unknown event types still render.
/// </summary>
public sealed record SessionEventDto(
    string EventId,
    string? SessionId,
    long Sequence,
    string Type,
    DateTimeOffset OccurredAt,
    string PayloadJson);
