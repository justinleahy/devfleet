namespace PiCommandCenter.Node.Child;

/// <summary>Raised when a path is rejected by <see cref="RepositoryPathPolicy"/>.</summary>
public sealed class RepositoryPathPolicyException(string code, string message)
    : Exception(message)
{
    /// <summary>Stable structured error code for the tool response.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// Strict path policy for reservation-authorized filesystem operations (SPEC §5, §18.1):
/// paths are repository-relative POSIX, and the resolved target — including every symlink
/// component — must stay inside the repository root. Absolute paths, Windows separators and
/// drive letters, traversal segments, and anything under <c>.git</c> are rejected outright.
/// </summary>
public static class RepositoryPathPolicy
{
    /// <summary>Repository directory that is never writable through reserved tools.</summary>
    public const string ReservedSegment = ".git";

    /// <summary>
    /// Validates <paramref name="relativePath"/> against <paramref name="repositoryRoot"/> and
    /// returns the canonical absolute target path. Throws
    /// <see cref="RepositoryPathPolicyException"/> on any violation, including a symlink that
    /// resolves outside the repository.
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
    /// Walks every existing ancestor of <paramref name="candidate"/> and follows symlinks to
    /// their final target; any target outside <paramref name="root"/> is rejected.
    /// </summary>
    private static void RejectSymlinkEscape(string root, string candidate)
    {
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, candidate)
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var link = ResolveLink(current);
            if (link is not null)
            {
                var resolved = Path.GetFullPath(link);
                if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !string.Equals(resolved, root, StringComparison.Ordinal))
                {
                    throw new RepositoryPathPolicyException(
                        "path_symlink_escape",
                        $"The path component '{segment}' is a symlink leaving the repository.");
                }
            }
        }
    }

    private static string? ResolveLink(string path)
    {
        if (File.Exists(path))
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
        }

        if (Directory.Exists(path))
        {
            return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
        }

        return null;
    }
}
