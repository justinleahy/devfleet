using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;

namespace PiCommandCenter.Application.Projects;

/// <summary>
/// Registration and lookup surface for fleet-owned project metadata.
/// </summary>
public interface IProjectCatalog
{
    Task<IReadOnlyList<ProjectDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <exception cref="ProjectNotFoundException">No project with the given id exists.</exception>
    Task<ProjectDto> GetAsync(ProjectId id, CancellationToken cancellationToken = default);

    /// <summary>Validates project metadata without accessing a node, filesystem, or Git.</summary>
    Task<ProjectValidationReport> ValidateAsync(RegisterProjectCommand command, CancellationToken cancellationToken = default);

    /// <exception cref="ProjectValidationException">The command violates a registration rule.</exception>
    Task<ProjectDto> RegisterAsync(RegisterProjectCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a trusted project-check profile, or clears to baseline-only when both values are null.
    /// Callers must already have validated the selection on the designated live node identified by
    /// <paramref name="workspaceBindingId"/>, <paramref name="nodeId"/>, and
    /// <paramref name="validationRevision"/> against project <paramref name="expectedProjectVersion"/>.
    /// Persistence is rejected without mutation when that exact binding is missing, rebound, or
    /// revalidated, or when the project version no longer matches.
    /// </summary>
    /// <exception cref="ProjectNotFoundException">No project with the given id exists.</exception>
    Task<ProjectDto> SelectTrustedVerificationProfileAsync(
        ProjectId id,
        WorkspaceBindingId workspaceBindingId,
        NodeId nodeId,
        long validationRevision,
        long expectedProjectVersion,
        string? profileId,
        string? profileRevision,
        CancellationToken cancellationToken = default);
}
