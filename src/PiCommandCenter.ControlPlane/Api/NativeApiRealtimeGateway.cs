using Microsoft.AspNetCore.SignalR;
using PiCommandCenter.Application.Mail;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.ControlPlane.Hubs;
using PiCommandCenter.ControlPlane.RuntimeRouting;

namespace PiCommandCenter.ControlPlane.Api;

/// <summary>
/// SignalR-backed <see cref="INativeApiRealtimeGateway"/> for live session routing and
/// best-effort commands to the authenticated connection of an assignment's retained owner.
/// </summary>
internal sealed class NativeApiRealtimeGateway(
    IHubContext<NodeHub> hub,
    NodeConnectionDirectory connections,
    ILogger<NativeApiRealtimeGateway> logger) : INativeApiRealtimeGateway
{
    public async Task RouteMailAsync(AgentMessageDto delivered, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivered);
        foreach (var recipient in delivered.Recipients)
        {
            await hub.Clients.Group(NodeHub.SessionGroup(recipient.SessionId))
                .SendAsync("ReceiveMail", NodeHub.ToTransport(delivered, recipient), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public Task CancelSessionAsync(string sessionId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(reason);
        return hub.Clients.Group(NodeHub.SessionGroup(sessionId))
            .SendAsync("CancelSession", new CancelSessionCommand(sessionId, reason), cancellationToken);
    }

    public async Task<bool> CancelAssignmentAsync(
        Guid nodeId,
        Guid requestId,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var connectionId = connections.Find(nodeId);
        if (connectionId is null)
        {
            return false;
        }

        try
        {
            await hub.Clients.Client(connectionId)
                .SendAsync(
                    "CancelAssignment",
                    new CancelAssignmentCommand(requestId, reason),
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Could not notify node {NodeId} to cancel request {RequestId}; reconciliation will retry.",
                nodeId,
                requestId);
            return false;
        }
    }
}
