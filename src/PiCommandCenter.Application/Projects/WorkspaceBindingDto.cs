using PiCommandCenter.Domain.Projects;

namespace PiCommandCenter.Application.Projects;

/// <summary>
/// Read model of a project's designated node-local workspace.
/// </summary>
public sealed record WorkspaceBindingDto(
    Guid Id,
    Guid ProjectId,
    Guid NodeId,
    string RepositoryPath,
    string? CanonicalRepositoryPath,
    WorkspaceBindingStatus Status,
    long ValidationRevision,
    string? ValidationCode,
    string? ValidationDetail,
    DateTimeOffset? ValidatedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version);
