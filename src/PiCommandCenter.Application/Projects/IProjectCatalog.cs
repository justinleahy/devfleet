using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;

namespace PiCommandCenter.Application.Projects;

/// <summary>
/// Registration and lookup surface for projects.
/// </summary>
public interface IProjectCatalog
{
    Task<IReadOnlyList<ProjectDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <exception cref="ProjectNotFoundException">No project with the given id exists.</exception>
    Task<ProjectDto> GetAsync(ProjectId id, CancellationToken cancellationToken = default);

    Task<ProjectValidationReport> ValidateAsync(RegisterProjectCommand command, CancellationToken cancellationToken = default);

    /// <exception cref="ProjectValidationException">The command violates a registration rule.</exception>
    /// <exception cref="DuplicateProjectException">A project with the same id or canonical path already exists.</exception>
    Task<ProjectDto> RegisterAsync(RegisterProjectCommand command, CancellationToken cancellationToken = default);
}
