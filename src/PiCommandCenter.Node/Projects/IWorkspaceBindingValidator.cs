using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.Projects;

/// <summary>Validates a revisioned workspace binding against this node's filesystem and Git installation.</summary>
public interface IWorkspaceBindingValidator
{
    Task<WorkspaceBindingValidationResultMessage> ValidateAsync(
        WorkspaceBindingValidationRequestMessage request,
        CancellationToken cancellationToken = default);
}
