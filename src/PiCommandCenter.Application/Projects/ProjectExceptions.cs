using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;

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
/// Raised when a node already has a workspace binding for a requested or canonical repository path
/// (maps deterministically to HTTP 409).
/// </summary>
public sealed class WorkspaceBindingConflictException : Exception
{
    public WorkspaceBindingConflictException(NodeId nodeId, string repositoryPath)
        : base($"Node '{nodeId}' already has a workspace binding for repository path '{repositoryPath}'.")
    {
        NodeId = nodeId;
        RepositoryPath = repositoryPath;
    }

    public NodeId NodeId { get; }

    public string RepositoryPath { get; }
}

/// <summary>
/// Raised when an active or recovery-required assignment still owns a workspace binding
/// (maps deterministically to HTTP 409).
/// </summary>
public sealed class WorkspaceBindingInUseException : Exception
{
    public WorkspaceBindingInUseException(WorkspaceBindingId bindingId)
        : base($"Workspace binding '{bindingId}' is referenced by an active or recovery-required execution assignment.")
    {
        BindingId = bindingId;
    }

    public WorkspaceBindingId BindingId { get; }
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
