using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Application.Projects;

/// <summary>Routes node-local workspace validation to the selected node when it is connected.</summary>
public interface IWorkspaceValidationGateway
{
    Task<WorkspaceBindingValidationResultMessage?> ValidateAsync(
        Guid nodeId,
        WorkspaceBindingValidationRequestMessage request,
        CancellationToken cancellationToken = default);
}
