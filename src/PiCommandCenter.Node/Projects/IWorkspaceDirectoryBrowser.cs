using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.Projects;

/// <summary>
/// Node-local bounded directory browser used by the Control Plane folder picker.
/// Only enumerates configured <see cref="WorkspaceValidationOptions.ApprovedRoots"/>
/// or canonical descendants of them; never follows or returns symbolic links.
/// </summary>
public interface IWorkspaceDirectoryBrowser
{
    /// <summary>
    /// Browses one directory (or the approved roots when <see cref="WorkspaceDirectoryBrowseRequestMessage.Path"/>
    /// is null). Results are bounded, sorted deterministically, and errors use stable codes.
    /// </summary>
    Task<WorkspaceDirectoryBrowseResponseMessage> BrowseAsync(
        WorkspaceDirectoryBrowseRequestMessage request,
        CancellationToken cancellationToken = default);
}
