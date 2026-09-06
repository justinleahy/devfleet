namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>Stable workspace validation result statuses.</summary>
public static class WorkspaceValidationStatuses
{
    public const string Valid = "valid";
    public const string Invalid = "invalid";
}

/// <summary>Stable workspace validation result codes.</summary>
public static class WorkspaceValidationCodes
{
    public const string Valid = "valid";
    public const string PathMissing = "path_missing";
    public const string PathNotDirectory = "path_not_directory";
    public const string PathOutsideApprovedRoot = "path_outside_approved_root";
    public const string PathSymlink = "path_symlink";
    public const string GitUnavailable = "git_unavailable";
    public const string NotGitRepository = "not_git_repository";
    public const string DefaultBranchMissing = "default_branch_missing";
    public const string Unreadable = "unreadable";
    public const string InvalidRequest = "invalid_request";
}

/// <summary>Node-local validation request for one workspace binding revision.</summary>
public sealed record WorkspaceBindingValidationRequestMessage(
    Guid BindingId,
    Guid ProjectId,
    long Revision,
    string RepositoryPath,
    string DefaultBranch);

/// <summary>Node-local validation result for the same workspace binding revision.</summary>
public sealed record WorkspaceBindingValidationResultMessage(
    Guid BindingId,
    Guid ProjectId,
    long Revision,
    string Status,
    string ValidationCode,
    string Detail,
    string? CanonicalRepositoryPath);
