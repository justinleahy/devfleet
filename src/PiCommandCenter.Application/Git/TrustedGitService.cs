namespace PiCommandCenter.Application.Git;

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
/// The single trusted seam for repository mutation. Implementations MUST perform only the two
/// exact operations below (branch creation from the project default branch, checkpoint commit of
/// explicitly listed paths) and MUST refuse anything else — no push, merge, rebase, reset, clean,
/// or credential handling. Agents can only reach this through supervisor tooling, never directly.
/// </summary>
public interface ITrustedGitService
{
    Task<RequestBranchCreated> CreateRequestBranchAsync(
        RequestBranchRequest request,
        CancellationToken cancellationToken = default);

    Task<CheckpointCommitted> CreateCheckpointCommitAsync(
        CheckpointCommitRequest request,
        CancellationToken cancellationToken = default);
}
