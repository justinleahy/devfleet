using Microsoft.AspNetCore.SignalR;
using PiCommandCenter.Application.Recovery;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.ControlPlane.Hubs;
using PiCommandCenter.ControlPlane.RuntimeRouting;
using PiCommandCenter.Domain;

namespace PiCommandCenter.ControlPlane.Recovery;

/// <summary>
/// Sends RecoverAssignment to the node's current retained SignalR connection.
/// Returns false when disconnected or the send fails.
/// </summary>
internal sealed class NodeRecoveryCommandGateway(
    IHubContext<NodeHub> hub,
    NodeConnectionDirectory connections,
    ILogger<NodeRecoveryCommandGateway> logger) : INodeRecoveryCommandGateway
{
    public async Task<bool> TrySendAsync(
        NodeId nodeId,
        RecoverAssignmentCommandMessage command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var connectionId = connections.Find(nodeId.Value);
        if (connectionId is null)
        {
            return false;
        }

        try
        {
            await hub.Clients.Client(connectionId)
                .SendAsync("RecoverAssignment", command, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Could not send RecoverAssignment {RecoveryId} attempt {Attempt} to node {NodeId}.",
                command.RecoveryId,
                command.Attempt,
                nodeId.Value);
            return false;
        }
    }
}
