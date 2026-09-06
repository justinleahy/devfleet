using Microsoft.AspNetCore.SignalR;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.ControlPlane.Hubs;
using PiCommandCenter.ControlPlane.RuntimeRouting;

namespace PiCommandCenter.ControlPlane.Projects;

/// <summary>Invokes workspace directory browsing on the selected node's current SignalR connection.</summary>
internal sealed class NodeWorkspaceDirectoryGateway(
    IHubContext<NodeHub> hub,
    NodeConnectionDirectory connections) : INodeWorkspaceDirectoryGateway
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(35);

    public async Task<WorkspaceDirectoryBrowseResponseMessage> BrowseAsync(
        Guid nodeId,
        WorkspaceDirectoryBrowseRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connectionId = connections.Find(nodeId)
            ?? throw new InvalidOperationException($"Node '{nodeId}' is not connected.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            return await hub.Clients.Client(connectionId)
                .InvokeCoreAsync<WorkspaceDirectoryBrowseResponseMessage>(
                    WorkspaceDirectoryBrowseCallback.MethodName,
                    [request],
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Node '{nodeId}' did not answer '{WorkspaceDirectoryBrowseCallback.MethodName}' within {CommandTimeout.TotalSeconds:0} seconds.");
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Node '{nodeId}' disconnected while handling '{WorkspaceDirectoryBrowseCallback.MethodName}'.", ex);
        }
    }
}
