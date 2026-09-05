using PiCommandCenter.Node.Child;

namespace PiCommandCenter.Node.Repository;

/// <summary>
/// Records branch/base commit/status/diff using git argument lists, attributes paths to
/// lease owners, and detects unattributed external changes. Never mutates git.
/// </summary>
public sealed class RepositoryInspector : IRepositoryInspector
{
    public async Task<RepositoryBaseline> CaptureBaselineAsync(
        string repositoryRoot,
        bool requireCleanStart,
        bool allowUntrackedFiles,
        CancellationToken cancellationToken)
    {
        var branch = (await GitCli.RunAsync(
            repositoryRoot, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken)
            .ConfigureAwait(false)).Trim();
        var commit = (await GitCli.RunAsync(
            repositoryRoot, ["rev-parse", "HEAD"], cancellationToken)
            .ConfigureAwait(false)).Trim();
        var status = await GitCli.RunAsync(
            repositoryRoot, ["status", "--porcelain=v1", "-z"], cancellationToken)
            .ConfigureAwait(false);
        var dirty = ParsePorcelainPaths(status);
        var blocking = allowUntrackedFiles
            ? dirty.Where(p => !p.StartsWith("?? ", StringComparison.Ordinal))
                .Select(StripStatusPrefix)
                .ToArray()
            : dirty.Select(StripStatusPrefix).ToArray();
        var isClean = blocking.Length == 0;
        if (requireCleanStart && !isClean)
        {
            throw new RepositoryDirtyException(blocking);
        }

        return new RepositoryBaseline(branch, commit, status, isClean, blocking);
    }

    public async Task<RepositoryDiffInspection> InspectDiffAsync(
        string repositoryRoot,
        string baseCommit,
        IReadOnlyList<ReservationLeaseInfo> leases,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseCommit);
        var branch = (await GitCli.RunAsync(
            repositoryRoot, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken)
            .ConfigureAwait(false)).Trim();
        var unstaged = await GitCli.RunAsync(
            repositoryRoot, ["diff", "--name-only", "-z", baseCommit], cancellationToken)
            .ConfigureAwait(false);
        var untracked = await GitCli.RunAsync(
            repositoryRoot, ["ls-files", "--others", "--exclude-standard", "-z"], cancellationToken)
            .ConfigureAwait(false);

        var paths = ParseNullSeparated(unstaged)
            .Concat(ParseNullSeparated(untracked))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        var changed = new List<ChangedFileAttribution>(paths.Length);
        var unattributed = new List<string>();
        foreach (var path in paths)
        {
            var owner = FindOwner(path, leases);
            changed.Add(new ChangedFileAttribution(path, owner?.OwnerSessionId, owner?.LeaseId));
            if (owner is null)
            {
                unattributed.Add(path);
            }
        }

        return new RepositoryDiffInspection(branch, baseCommit, changed, unattributed);
    }

    public async Task DetectExternalChangesAsync(
        string repositoryRoot,
        string baseCommit,
        IReadOnlyList<ReservationLeaseInfo> leases,
        CancellationToken cancellationToken)
    {
        var inspection = await InspectDiffAsync(repositoryRoot, baseCommit, leases, cancellationToken)
            .ConfigureAwait(false);
        if (inspection.UnattributedPaths.Count > 0)
        {
            throw new ExternalRepositoryModificationException(inspection.UnattributedPaths);
        }
    }

    private static ReservationLeaseInfo? FindOwner(string path, IReadOnlyList<ReservationLeaseInfo> leases)
    {
        foreach (var lease in leases)
        {
            if (!string.Equals(lease.State, "Active", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var scope in lease.Scopes)
            {
                if (Covers(scope, path))
                {
                    return lease;
                }
            }
        }

        return null;
    }

    private static bool Covers(ReservationScopeSpec scope, string path)
    {
        var kind = scope.Kind;
        var prefix = scope.Path.Trim().TrimEnd('/');
        if (kind.Equals("file", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("File", StringComparison.Ordinal))
        {
            return string.Equals(prefix, path, StringComparison.Ordinal);
        }

        if (kind.Equals("directory", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("Directory", StringComparison.Ordinal))
        {
            return string.Equals(path, prefix, StringComparison.Ordinal)
                || path.StartsWith(prefix + "/", StringComparison.Ordinal);
        }

        return false;
    }

    private static IReadOnlyList<string> ParsePorcelainPaths(string status)
    {
        if (string.IsNullOrEmpty(status))
        {
            return [];
        }

        return ParseNullSeparated(status);
    }

    private static IReadOnlyList<string> ParseNullSeparated(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string StripStatusPrefix(string porcelain)
    {
        if (porcelain.Length >= 3 && porcelain[2] == ' ')
        {
            return porcelain[3..];
        }

        return porcelain;
    }
}
