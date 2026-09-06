using PiCommandCenter.Application.Mail;

namespace PiCommandCenter.Application.Runtime;

/// <summary>
/// Native API gateway to the node fleet's live transport. Lets <c>/api/v1</c> endpoints
/// push mail and session cancellation to connected nodes without depending on SignalR.
/// </summary>
public interface INativeApiRealtimeGateway
{
    /// <summary>
    /// Pushes a delivered message to every recipient session with a live node connection.
    /// Sessions without a live node fall back to inbox polling.
    /// </summary>
    Task RouteMailAsync(AgentMessageDto delivered, CancellationToken cancellationToken);

    /// <summary>Commands whichever node hosts <paramref name="sessionId"/> to cancel it.</summary>
    Task CancelSessionAsync(string sessionId, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Best-effort command to cancel a durable assignment on its retained owner. Returns false
    /// when that node has no live connection or dispatch fails.
    /// </summary>
    Task<bool> CancelAssignmentAsync(
        Guid nodeId,
        Guid requestId,
        string reason,
        CancellationToken cancellationToken);
}
