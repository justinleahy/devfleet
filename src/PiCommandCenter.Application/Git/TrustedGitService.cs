namespace PiCommandCenter.Application.Git;

/// <summary>
/// Request to prepare an assigned workspace for a root session: initialize an ordinary
/// directory as a repository on the configured branch with one baseline commit, complete the
/// same baseline for an unborn repository, or revalidate an already-committed repository.
/// Issued only by the trusted node supervisor after the assignment journal is durable and
/// before baseline capture, request branch creation, and runtime start (SPEC: supervisor-owned
/// workspace preparation). Retries after an interrupted preparation MUST be idempotent: a
/// prepared workspace is never re-initialized or re-committed.
/// </summary>
public sealed record WorkspacePreparationRequest(
    Guid RequestId,
    string RepositoryPath,
    string DefaultBranch);

/// <summary>
/// Outcome of a trusted workspace preparation: the canonical repository root, the branch the
/// workspace is on, and the commit preparation resolved or created.
/// </summary>
public sealed record WorkspacePreparation(
    string RepositoryPath,
    string Branch,
    string BaselineCommitId);


/// <summary>
/// Request to create a request work branch from a project's default branch. Issued only by the
/// trusted node supervisor after a claim is accepted (SPEC: supervisor-owned request branch).
/// </summary>
public sealed record RequestBranchRequest(
    Guid RequestId,
    string RepositoryPath,
    string DefaultBranch,
    string BranchName);

/// <summary>Outcome of a trusted request branch creation.</summary>
public sealed record RequestBranchCreated(string BranchName, string BaseCommitId);

/// <summary>
/// Request to record one final checkpoint commit of exactly the given paths on the request
/// branch. Issued only by the trusted node supervisor; agents never invoke git directly.
/// </summary>
public sealed record CheckpointCommitRequest(
    Guid RequestId,
    string RepositoryPath,
    string BranchName,
    string Message,
    IReadOnlyList<string> Paths);

/// <summary>Outcome of a trusted checkpoint commit.</summary>
public sealed record CheckpointCommitted(string CommitId, string BranchName);

/// <summary>
/// The single trusted seam for repository mutation. Implementations MUST perform only the
/// exact operations below (workspace preparation, branch creation from the project default
/// branch, checkpoint commit of explicitly listed paths) and MUST refuse anything else — no
/// push, merge, rebase, reset, clean, or credential handling. Agents can only reach this
/// through supervisor tooling, never directly.
/// </summary>
public interface ITrustedGitService
{
    Task<RequestBranchCreated> CreateRequestBranchAsync(
        RequestBranchRequest request,
        CancellationToken cancellationToken = default);

    Task<CheckpointCommitted> CreateCheckpointCommitAsync(
        CheckpointCommitRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkspacePreparation> PrepareWorkspaceAsync(
        WorkspacePreparationRequest request,
        CancellationToken cancellationToken = default);
}
