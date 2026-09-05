using Microsoft.AspNetCore.SignalR;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.ControlPlane.Hubs;

namespace PiCommandCenter.ControlPlane.RuntimeRouting;

/// <summary>Invokes bounded request/response commands on the node's current SignalR connection.</summary>
internal sealed class NodeRuntimeConfigurationGateway(
    IHubContext<NodeHub> hub,
    NodeConnectionDirectory connections) : INodeRuntimeConfigurationGateway
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(35);

    public Task<NodeRuntimeConfigurationMessage> GetAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default)
        => InvokeAsync<NodeRuntimeConfigurationMessage>(nodeId, "GetRuntimeConfiguration", null, cancellationToken);

    public Task<IReadOnlyList<RuntimeModelCatalogMessage>> DiscoverModelsAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default)
        => InvokeAsync<IReadOnlyList<RuntimeModelCatalogMessage>>(
            nodeId, "DiscoverRuntimeModels", null, cancellationToken);

    public Task<NodeRuntimeConfigurationMessage> UpdateAsync(
        Guid nodeId,
        UpdateNodeRuntimeConfigurationMessage update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        return InvokeAsync<NodeRuntimeConfigurationMessage>(
            nodeId, "UpdateRuntimeConfiguration", update, cancellationToken);
    }

    private async Task<T> InvokeAsync<T>(
        Guid nodeId,
        string method,
        object? argument,
        CancellationToken cancellationToken)
    {
        var connectionId = connections.Find(nodeId)
            ?? throw new InvalidOperationException($"Node '{nodeId}' is not connected.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            var client = hub.Clients.Client(connectionId);
            return argument is null
                ? await client.InvokeCoreAsync<T>(method, [], timeout.Token).ConfigureAwait(false)
                : await client.InvokeCoreAsync<T>(method, [argument], timeout.Token).ConfigureAwait(false);
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
