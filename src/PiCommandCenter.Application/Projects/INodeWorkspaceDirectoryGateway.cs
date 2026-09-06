using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Application.Projects;

/// <summary>
/// Routes bounded node-local workspace directory browsing to the authenticated selected
/// node. The control plane never inspects node filesystems; a null request path lists
/// configured approved roots, and results stay inside those roots.
/// </summary>
public interface INodeWorkspaceDirectoryGateway
{
    Task<WorkspaceDirectoryBrowseResponseMessage> BrowseAsync(
        Guid nodeId,
        WorkspaceDirectoryBrowseRequestMessage request,
        CancellationToken cancellationToken = default);
}
