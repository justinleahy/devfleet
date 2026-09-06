using Microsoft.AspNetCore.SignalR;
using PiCommandCenter.Application.VerificationPolicy;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.ControlPlane.Hubs;
using PiCommandCenter.ControlPlane.RuntimeRouting;

namespace PiCommandCenter.ControlPlane.VerificationPolicy;

/// <summary>Invokes bounded verification-policy callbacks on the node's current SignalR connection.</summary>
internal sealed class NodeVerificationPolicyGateway(
    IHubContext<NodeHub> hub,
    NodeConnectionDirectory connections) : INodeVerificationPolicyGateway
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(35);

    public Task<VerificationPolicyCatalogMessage> GetCatalogAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default)
        => InvokeAsync<VerificationPolicyCatalogMessage>(
            nodeId,
            "GetVerificationPolicyCatalog",
            argument: null,
            cancellationToken);

    public Task<VerificationProfileSelectionResultMessage> ValidateSelectionAsync(
        Guid nodeId,
        VerificationProfileSelectionRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<VerificationProfileSelectionResultMessage>(
            nodeId,
            "ValidateVerificationProfileSelection",
            request,
            cancellationToken);
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
            throw new InvalidOperationException(
                $"Node '{nodeId}' did not answer '{method}' within {CommandTimeout.TotalSeconds:0} seconds.");
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Node '{nodeId}' disconnected while handling '{method}'.", ex);
        }
    }
}
