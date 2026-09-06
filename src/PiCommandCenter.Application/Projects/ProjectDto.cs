namespace PiCommandCenter.Application.Projects;

/// <summary>
/// Read model of a registered project.
/// </summary>
public sealed record ProjectDto(
    Guid Id,
    string DisplayName,
    string DefaultBranch,
    bool Enabled,
    int MaxActiveWriteRequests,
    int MaxReadOnlyRequests,
    int MaxChildAgentsPerRequest,
    bool RequireCleanStart,
    bool CreateRequestBranch,
    bool CreateRequestCommit,
    bool AutoMerge,
    string? TrustedVerificationProfileId,
    string? TrustedVerificationProfileRevision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version,
    WorkspaceBindingDto? Binding);
