using PiCommandCenter.Node.Child;

namespace PiCommandCenter.Node.Repository;

/// <summary>Git identity captured at claim start.</summary>
public sealed record RepositoryBaseline(
    string Branch,
    string BaseCommit,
    string StatusPorcelain,
    bool IsClean,
    IReadOnlyList<string> DirtyPaths);

/// <summary>One changed path attributed to a reservation holder when possible.</summary>
public sealed record ChangedFileAttribution(
    string Path,
    string? OwnerSessionId,
    Guid? LeaseId);

/// <summary>Diff against the captured base commit with ownership attribution.</summary>
public sealed record RepositoryDiffInspection(
    string Branch,
    string BaseCommit,
    IReadOnlyList<ChangedFileAttribution> ChangedFiles,
    IReadOnlyList<string> UnattributedPaths);

/// <summary>Dirty working tree at request start when <c>requireCleanStart</c> is set.</summary>
public sealed class RepositoryDirtyException(IReadOnlyList<string> paths)
    : InvalidOperationException("Repository is dirty at request start.")
{
    public IReadOnlyList<string> DirtyPaths { get; } = paths;
}

/// <summary>Workspace change that cannot be attributed to an active reservation holder.</summary>
public sealed class ExternalRepositoryModificationException(IReadOnlyList<string> paths)
    : InvalidOperationException("BLOCKED — Unattributed external repository modification")
{
    public IReadOnlyList<string> Paths { get; } = paths;
}

/// <summary>Read-only repository inspector. Never mutates git.</summary>
public interface IRepositoryInspector
{
    Task<RepositoryBaseline> CaptureBaselineAsync(
        string repositoryRoot,
        bool requireCleanStart,
        bool allowUntrackedFiles,
        CancellationToken cancellationToken);

    Task<RepositoryDiffInspection> InspectDiffAsync(
        string repositoryRoot,
        string baseCommit,
        IReadOnlyList<ReservationLeaseInfo> leases,
        CancellationToken cancellationToken);

    /// <summary>
    /// Throws <see cref="ExternalRepositoryModificationException"/> when any changed path
    /// is not covered by an active file or directory lease.
    /// </summary>
    Task DetectExternalChangesAsync(
        string repositoryRoot,
        string baseCommit,
        IReadOnlyList<ReservationLeaseInfo> leases,
        CancellationToken cancellationToken);
}
