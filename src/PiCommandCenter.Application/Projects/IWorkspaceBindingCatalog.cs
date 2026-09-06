using PiCommandCenter.Domain;

namespace PiCommandCenter.Application.Projects;

/// <summary>
/// Designation and node-local validation surface for a project's sole workspace binding.
/// </summary>
public interface IWorkspaceBindingCatalog
{
    /// <summary>Gets the designated binding, or null when the project is unbound.</summary>
    /// <exception cref="ProjectNotFoundException">No project with the given id exists.</exception>
    Task<WorkspaceBindingDto?> GetAsync(ProjectId projectId, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the designation and starts a pending validation revision.</summary>
    /// <exception cref="ProjectNotFoundException">No project with the given id exists.</exception>
    /// <exception cref="WorkspaceBindingConflictException">The requested or accepted canonical node path conflicts with another binding.</exception>
    /// <exception cref="WorkspaceBindingInUseException">The current binding still has a nonterminal assignment.</exception>
    Task<WorkspaceBindingDto> DesignateAsync(
        ProjectId projectId,
        DesignateWorkspaceBindingCommand command,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests node-local validation of the current revision through
    /// <see cref="IWorkspaceValidationGateway"/>.
    /// </summary>
    /// <exception cref="WorkspaceBindingConflictException">The accepted canonical node path conflicts with another binding.</exception>
    /// <exception cref="ProjectNotFoundException">No project with the given id exists.</exception>
    Task<WorkspaceBindingDto> ValidateAsync(
        ProjectId projectId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes the project's designated binding.</summary>
    /// <exception cref="ProjectNotFoundException">No project with the given id exists.</exception>
    /// <exception cref="WorkspaceBindingInUseException">The current binding still has a nonterminal assignment.</exception>
    Task DeleteAsync(
        ProjectId projectId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);
}
