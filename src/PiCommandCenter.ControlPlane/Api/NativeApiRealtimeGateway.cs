using Microsoft.AspNetCore.SignalR;
using PiCommandCenter.Application.Mail;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.ControlPlane.Hubs;

namespace PiCommandCenter.ControlPlane.Api;

/// <summary>
/// SignalR-backed <see cref="INativeApiRealtimeGateway"/>: mirrors the legacy <c>/api</c>
/// live-routing and cancel dispatch through <see cref="NodeHub"/> session groups.
/// </summary>
internal sealed class NativeApiRealtimeGateway(IHubContext<NodeHub> hub) : INativeApiRealtimeGateway
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
}
