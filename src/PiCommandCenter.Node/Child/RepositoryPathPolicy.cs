namespace PiCommandCenter.Node.Child;

/// <summary>Raised when a path is rejected by <see cref="RepositoryPathPolicy"/>.</summary>
public sealed class RepositoryPathPolicyException(string code, string message)
    : Exception(message)
{
    /// <summary>Stable structured error code for the tool response.</summary>
    public string Code { get; } = code;
}

/// Strict path policy for reservation-authorized filesystem operations (SPEC §5, §18.1):
/// paths are repository-relative POSIX, and every component of the resolved target must be a
/// canonical, non-symlink path inside the repository root. Absolute paths, Windows separators
/// and drive letters, traversal segments, and anything under <c>.git</c> are rejected outright.
/// The final leaf may be non-existing (it is created on write), but it must be reached only
/// through existing canonical ancestors: any symlink component — escaping the repository or
/// aliasing inside it, even a dangling one — is rejected before any I/O happens.
/// </summary>
public static class RepositoryPathPolicy
{
    /// <summary>Repository directory that is never writable through reserved tools.</summary>
    public const string ReservedSegment = ".git";

    /// <summary>
    /// Validates <paramref name="relativePath"/> against <paramref name="repositoryRoot"/> and
    /// returns the canonical absolute target path. Throws
    /// <see cref="RepositoryPathPolicyException"/> on any violation, including any symlink
    /// component (escape or alias) and a dangling symlink leaf.
    /// </summary>
    public static string Resolve(string repositoryRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            throw new RepositoryPathPolicyException(
                "invalid_repository_root", "The repository root must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new RepositoryPathPolicyException(
                "path_empty", "The path must not be empty.");
        }

        var candidate = relativePath.Trim();
        if (candidate.Contains('\\'))
        {
            throw new RepositoryPathPolicyException(
                "path_separator", "Paths must use POSIX '/' separators.");
        }

        if (candidate.StartsWith('/') || candidate.StartsWith('~'))
        {
            throw new RepositoryPathPolicyException(
                "path_absolute", "Paths must be repository-relative, not absolute.");
        }

        if (Path.IsPathRooted(candidate)
            || (candidate.Length >= 2 && candidate[1] == ':'))
        {
            throw new RepositoryPathPolicyException(
                "path_absolute", "Paths must be repository-relative, not absolute.");
        }

        if (candidate.EndsWith('/'))
        {
            throw new RepositoryPathPolicyException(
                "path_trailing_slash", "Paths must not end with a trailing slash.");
        }

        var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new RepositoryPathPolicyException(
                "path_empty", "The path must not be empty.");
        }

        foreach (var segment in segments)
        {
            if (segment == "." || segment == "..")
            {
                throw new RepositoryPathPolicyException(
                    "path_traversal", "Traversal segments are not allowed.");
            }

            if (string.Equals(segment, ReservedSegment, StringComparison.OrdinalIgnoreCase))
            {
                throw new RepositoryPathPolicyException(
                    "path_git_reserved", $"'{ReservedSegment}' paths are never accessible.");
            }
        }

        var root = Path.GetFullPath(repositoryRoot);
        var combined = Path.GetFullPath(Path.Combine(root, candidate));
        if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(combined, root, StringComparison.Ordinal))
        {
            throw new RepositoryPathPolicyException(
                "path_escape", "The resolved path is outside the repository.");
        }

        RejectSymlinkEscape(root, combined);
        return combined;
    }

    /// <summary>
    /// Like <see cref="Resolve"/>, but empty, <c>.</c>, and <c>./</c> mean the repository root
    /// (used by read-only listing/search tools).
    /// </summary>
    public static string ResolveOrRoot(string repositoryRoot, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            if (string.IsNullOrWhiteSpace(repositoryRoot))
            {
                throw new RepositoryPathPolicyException(
                    "invalid_repository_root", "The repository root must not be empty.");
            }

            var root = Path.GetFullPath(repositoryRoot);
            RejectSymlinkEscape(root, root);
            return root;
        }

        var trimmed = relativePath.Trim();
        if (trimmed is "." or "./")
        {
            return ResolveOrRoot(repositoryRoot, null);
        }

        return Resolve(repositoryRoot, trimmed);
    }

    /// <summary>
    /// Walks every component of <paramref name="candidate"/> and rejects any symlink:
    /// a link resolving outside <paramref name="root"/> is an escape; a link resolving inside
    /// the repository is an alias for the canonical path. Links are detected even when their
    /// target does not exist (dangling symlinks), so a write can never follow one out of the
    /// repository.
    /// </summary>
    private static void RejectSymlinkEscape(string root, string candidate)
    {
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, candidate)
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var link = ResolveLink(current);
            if (link is null)
            {
                continue;
            }

            var resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(link));
            if (resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || string.Equals(resolved, root, StringComparison.Ordinal))
            {
                throw new RepositoryPathPolicyException(
                    "path_symlink_alias",
                    $"The path component '{segment}' is a symlink alias inside the repository; use the canonical path.");
            }

            throw new RepositoryPathPolicyException(
                "path_symlink_escape",
                $"The path component '{segment}' is a symlink leaving the repository.");
        }
    }

    private static string? ResolveLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists && info.LinkTarget is null)
        {
            return null;
        }

        try
        {
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (IOException)
        {
            // An unresolvable link chain is treated as a link to itself — inside the repository
            // and therefore rejected as an alias — so resolution stays deterministic fail-closed.
            return path;
        }
    }
}
