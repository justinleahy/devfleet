using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;

namespace PiCommandCenter.Application.Sessions;

/// <summary>
/// Append-only persistence for normalized agent events and their current <c>AgentSession</c>
/// projection (SPEC §22, §29). Every <see cref="ApplyAsync"/> inserts the event and applies the
/// projection transition in one transaction; a duplicate <see cref="NormalizedAgentEvent.EventId"/>
/// is inert — the event is not re-inserted and the projection is not re-applied.
/// </summary>
public interface IAgentSessionStore
{
    /// <summary>Lists the session projections attached to a work request (root first, then children).</summary>
    Task<IReadOnlyList<AgentSessionDto>> ListAsync(
        WorkRequestId requestId,
        CancellationToken cancellationToken = default);

    /// <summary>Gets one session projection by session id, or null when unknown.</summary>
    Task<AgentSessionDto?> GetAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists one normalized event and applies it to the projection transactionally and
    /// idempotently. Unknown event types are stored but change no status.
    /// </summary>
    Task ApplyAsync(
        NormalizedAgentEvent @event,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists persisted session events for a work request ordered by occurrence time then
    /// sequence — the request-level event timeline.
    /// </summary>
    Task<IReadOnlyList<SessionEventDto>> ListEventsAsync(
        WorkRequestId requestId,
        CancellationToken cancellationToken = default);
}
