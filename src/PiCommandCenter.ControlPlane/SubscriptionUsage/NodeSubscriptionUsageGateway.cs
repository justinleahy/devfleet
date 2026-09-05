using Microsoft.AspNetCore.SignalR;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.ControlPlane.Hubs;
using PiCommandCenter.ControlPlane.RuntimeRouting;

namespace PiCommandCenter.ControlPlane.SubscriptionUsage;

/// <summary>Invokes bounded request/response commands on the node's current SignalR connection.</summary>
internal sealed class NodeSubscriptionUsageGateway(
    IHubContext<NodeHub> hub,
    NodeConnectionDirectory connections) : INodeSubscriptionUsageGateway
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(35);

    public Task<NodeSubscriptionUsageMessage> GetAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default)
        => InvokeAsync(nodeId, cancellationToken);

    private async Task<NodeSubscriptionUsageMessage> InvokeAsync(
        Guid nodeId,
        CancellationToken cancellationToken)
    {
        const string method = "GetSubscriptionUsage";
        var connectionId = connections.Find(nodeId)
            ?? throw new InvalidOperationException($"Node '{nodeId}' is not connected.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            return await hub.Clients.Client(connectionId)
                .InvokeCoreAsync<NodeSubscriptionUsageMessage>(method, [], timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Node '{nodeId}' did not answer '{method}' within {CommandTimeout.TotalSeconds:0} seconds.");
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Node '{nodeId}' disconnected while handling '{method}'.", ex);
        }
    }
}
