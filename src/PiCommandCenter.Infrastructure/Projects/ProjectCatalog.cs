using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Projects;

/// <summary>
/// EF Core backed catalog for fleet-owned project metadata and its optional workspace binding.
/// </summary>
public sealed class ProjectCatalog(
    TimeProvider clock,
    ControlPlaneDbContext db,
    IProjectionNotifier notifier) : IProjectCatalog
{
    public async Task<IReadOnlyList<ProjectDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var projects = await db.Projects
            .AsNoTracking()
            .OrderBy(project => project.CreatedAt)
            .ThenBy(project => project.DisplayName)
            .ToListAsync(cancellationToken);
        if (projects.Count == 0)
        {
            return [];
        }

        var bindings = await db.WorkspaceBindings
            .AsNoTracking()
            .ToDictionaryAsync(binding => binding.ProjectId, cancellationToken);

        return projects
            .Select(project => ToDto(project, bindings.GetValueOrDefault(project.Id)))
            .ToList();
    }

    public async Task<ProjectDto> GetAsync(ProjectId id, CancellationToken cancellationToken = default)
    {
        var project = await db.Projects
            .AsNoTracking()
            .SingleOrDefaultAsync(project => project.Id == id, cancellationToken)
            ?? throw new ProjectNotFoundException(id.Value);
        var binding = await db.WorkspaceBindings
            .AsNoTracking()
            .SingleOrDefaultAsync(binding => binding.ProjectId == id, cancellationToken);

        return ToDto(project, binding);
    }

    public Task<ProjectValidationReport> ValidateAsync(
        RegisterProjectCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var errors = CollectValidationErrors(command);
        return Task.FromResult(errors.Count == 0
            ? ProjectValidationReport.Success
            : ProjectValidationReport.Failure(errors));
    }

    public async Task<ProjectDto> RegisterAsync(
        RegisterProjectCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var errors = CollectValidationErrors(command);
        if (errors.Count > 0)
        {
            throw new ProjectValidationException(errors);
        }

        var project = Project.Register(
            command.DisplayName,
            command.DefaultBranch,
            command.Enabled,
            command.MaxActiveWriteRequests,
            command.MaxReadOnlyRequests,
            command.MaxChildAgentsPerRequest,
            command.RequireCleanStart,
            command.CreateRequestBranch,
            command.CreateRequestCommit,
            command.AutoMerge,
            clock.GetUtcNow());

        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken);
        notifier.Publish(ProjectionChange.Fleet());

        return ToDto(project, binding: null);
    }

    private static List<string> CollectValidationErrors(RegisterProjectCommand command)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(command.DisplayName))
        {
            errors.Add("Display name must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(command.DefaultBranch))
        {
            errors.Add("Default branch must not be empty.");
        }
        else
        {
            var branch = command.DefaultBranch.Trim();
            if (branch.Any(char.IsWhiteSpace) || branch.StartsWith('-'))
            {
                errors.Add($"Default branch '{branch}' is not a valid branch name.");
            }
        }

        var limits = new (int Value, string Name)[]
        {
            (command.MaxActiveWriteRequests, nameof(RegisterProjectCommand.MaxActiveWriteRequests)),
            (command.MaxReadOnlyRequests, nameof(RegisterProjectCommand.MaxReadOnlyRequests)),
            (command.MaxChildAgentsPerRequest, nameof(RegisterProjectCommand.MaxChildAgentsPerRequest)),
        };
        foreach (var (value, name) in limits)
        {
            if (value < 1)
            {
                errors.Add($"{name} must be a positive integer.");
            }
        }

        return errors;
    }

    private static ProjectDto ToDto(Project project, WorkspaceBinding? binding) => new(
        project.Id.Value,
        project.DisplayName,
        project.DefaultBranch,
        project.Enabled,
        project.MaxActiveWriteRequests,
        project.MaxReadOnlyRequests,
        project.MaxChildAgentsPerRequest,
        project.RequireCleanStart,
        project.CreateRequestBranch,
        project.CreateRequestCommit,
        project.AutoMerge,
        project.CreatedAt,
        project.UpdatedAt,
        project.Version,
        binding is null ? null : ToDto(binding));

    private static WorkspaceBindingDto ToDto(WorkspaceBinding binding) => new(
        binding.Id.Value,
        binding.ProjectId.Value,
        binding.NodeId.Value,
        binding.RepositoryPath,
        binding.CanonicalRepositoryPath,
        binding.Status,
        binding.ValidationRevision,
        binding.ValidationCode,
        binding.ValidationDetail,
        binding.ValidatedAt,
        binding.CreatedAt,
        binding.UpdatedAt,
        binding.Version);
}
