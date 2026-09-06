namespace PiCommandCenter.Application.Projects;

/// <summary>
/// Command to register fleet-owned project metadata.
/// </summary>
public sealed record RegisterProjectCommand(
    string DisplayName,
    string DefaultBranch,
    bool Enabled,
    int MaxActiveWriteRequests,
    int MaxReadOnlyRequests,
    int MaxChildAgentsPerRequest,
    bool RequireCleanStart,
    bool CreateRequestBranch,
    bool CreateRequestCommit,
    bool AutoMerge);
