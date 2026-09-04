namespace PiCommandCenter.Application.Projects;

/// <summary>
/// Raised when registration is attempted with data that violates a registration rule
/// (maps deterministically to HTTP 400).
/// </summary>
public sealed class ProjectValidationException : Exception
{
    public ProjectValidationException(IReadOnlyList<string> errors)
        : base("Project registration failed validation.")
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}

/// <summary>
/// Raised when a project with the same id or canonical repository path already exists
/// (maps deterministically to HTTP 409).
/// </summary>
public sealed class DuplicateProjectException : Exception
{
    public DuplicateProjectException(string repositoryPath)
        : base($"A project with repository path '{repositoryPath}' is already registered.")
    {
        RepositoryPath = repositoryPath;
    }

    public string RepositoryPath { get; }
}

/// <summary>
/// Raised when a requested project does not exist (maps deterministically to HTTP 404).
/// </summary>
public sealed class ProjectNotFoundException : Exception
{
    public ProjectNotFoundException(Guid projectId)
        : base($"Project '{projectId}' was not found.")
    {
        ProjectId = projectId;
    }

    public Guid ProjectId { get; }
}
