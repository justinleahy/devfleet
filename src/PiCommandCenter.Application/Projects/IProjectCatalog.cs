using PiCommandCenter.Domain;

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
}
