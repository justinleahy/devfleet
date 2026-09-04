namespace PiCommandCenter.Application.Projects;

/// <summary>
/// Command to register a new project in the catalog.
/// </summary>
public sealed record RegisterProjectCommand(
    string DisplayName,
    string RepositoryPath,
    string DefaultBranch,
    bool Enabled,
    int MaxActiveWriteRequests,
    int MaxReadOnlyRequests,
    int MaxChildAgentsPerRequest,
    bool RequireCleanStart,
    bool CreateRequestBranch,
    bool CreateRequestCommit,
    bool AutoMerge);
