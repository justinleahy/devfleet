using Microsoft.AspNetCore.SignalR;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.ControlPlane.Hubs;
using PiCommandCenter.ControlPlane.RuntimeRouting;

namespace PiCommandCenter.ControlPlane.Projects;

/// <summary>Invokes workspace validation on the selected node's current SignalR connection.</summary>
internal sealed class NodeWorkspaceValidationGateway(
    IHubContext<NodeHub> hub,
    NodeConnectionDirectory connections) : IWorkspaceValidationGateway
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(35);

    public async Task<WorkspaceBindingValidationResultMessage?> ValidateAsync(
        Guid nodeId,
        WorkspaceBindingValidationRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connectionId = connections.Find(nodeId);
        if (connectionId is null)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            return await hub.Clients.Client(connectionId)
                .InvokeCoreAsync<WorkspaceBindingValidationResultMessage>(
                    "ValidateWorkspaceBinding",
                    [request],
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Node '{nodeId}' did not answer 'ValidateWorkspaceBinding' within {CommandTimeout.TotalSeconds:0} seconds.");
        }
        catch (IOException)
        {
            return null;
        }
    }
}
