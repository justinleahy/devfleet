namespace PiCommandCenter.Domain.Reservations;

/// <summary>
/// A single reservation scope: one exact file path, one directory prefix (path always ends
/// with <c>/</c>), or one named shared resource. Constructed only through
/// <see cref="Create"/> so stored paths are always normalized (repository-relative POSIX,
/// no traversal, no <c>.git</c>).
/// </summary>
public readonly record struct ReservationScope
{
    public const int MaxPathLength = 1024;

    /// <summary>Named shared resource that excludes every source file/directory lease on the same Project.</summary>
    public const string ProjectBuildResource = "project-build";

    private ReservationScope(ReservationScopeKind kind, string path)
    {
        Kind = kind;
        Path = path;
    }

    public ReservationScopeKind Kind { get; }

    /// <summary>
    /// Normalized repository-relative POSIX path; for <see cref="ReservationScopeKind.Directory"/>
    /// it ends with <c>/</c>; for <see cref="ReservationScopeKind.Resource"/> it is the resource name.
    /// </summary>
    public string Path { get; }

    /// <summary>Normalizes and validates a raw scope. Throws <see cref="InvalidReservationScopeException"/>.</summary>
    public static ReservationScope Create(ReservationScopeKind kind, string rawPath)
    {
        var normalized = Normalize(kind, rawPath);
        return new ReservationScope(kind, normalized);
    }

    /// <summary>
    /// Normalizes a raw scope value without constructing: separators to <c>/</c>, absolute
    /// paths and <c>..</c> traversal rejected, <c>.git</c> segments rejected, duplicate and
    /// <c>.</c> segments collapsed, directories keep a trailing slash, resources are trimmed
    /// names without separators. Filesystem case is preserved.
    /// </summary>
    public static string Normalize(ReservationScopeKind kind, string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            throw new InvalidReservationScopeException("Reservation scope path must not be empty.");
        }

        var candidate = rawPath.Trim().Replace('\\', '/');

        if (kind == ReservationScopeKind.Resource)
        {
            if (candidate.Contains('/') || candidate.Contains('\\'))
            {
                throw new InvalidReservationScopeException(
                    $"Resource scope '{candidate}' must be a plain name without path separators.");
            }

            if (candidate == "." || candidate == "..")
            {
                throw new InvalidReservationScopeException(
                    $"Resource scope name '{candidate}' is not allowed.");
            }

            return RequireBounded(candidate, kind);
        }

        if (candidate.StartsWith('/')
            || candidate.StartsWith('~')
            || (candidate.Length >= 2 && char.IsAsciiLetter(candidate[0]) && candidate[1] == ':'))
        {
            throw new InvalidReservationScopeException(
                $"Reservation scope path '{candidate}' must be relative to the repository root.");
        }

        var segments = new List<string>();
        foreach (var segment in candidate.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                throw new InvalidReservationScopeException(
                    $"Reservation scope path '{candidate}' must not traverse outside the repository ('..').");
            }

            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidReservationScopeException(
                    $"Reservation scope path '{candidate}' must not target '.git' contents.");
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            throw new InvalidReservationScopeException(
                $"Reservation scope path '{candidate}' does not name any repository path.");
        }

        var normalized = string.Join('/', segments);
        if (kind == ReservationScopeKind.Directory)
        {
            normalized += "/";
        }

        return RequireBounded(normalized, kind);
    }

    /// <summary>
    /// Deterministic PoC conflict rule: same resource name conflicts; equal files conflict;
    /// a file conflicts with any containing directory prefix; directories conflict when
    /// either prefix contains the other. <see cref="ProjectBuildResource"/> additionally
    /// conflicts with every file and directory scope. Other resources never conflict with
    /// a path scope.
    /// </summary>
    public static bool ConflictsWith(ReservationScope existing, ReservationScope requested)
    {
        if (IsProjectBuildSourceConflict(existing, requested))
        {
            return true;
        }

        if (existing.Kind == ReservationScopeKind.Resource
            || requested.Kind == ReservationScopeKind.Resource)
        {
            return existing.Kind == requested.Kind
                && string.Equals(existing.Path, requested.Path, StringComparison.Ordinal);
        }

        if (existing.Kind == ReservationScopeKind.File
            && requested.Kind == ReservationScopeKind.File)
        {
            return string.Equals(existing.Path, requested.Path, StringComparison.Ordinal);
        }

        var directory = existing.Kind == ReservationScopeKind.Directory ? existing : requested;
        var file = existing.Kind == ReservationScopeKind.Directory ? requested : existing;

        if (file.Kind == ReservationScopeKind.File)
        {
            return file.Path.StartsWith(directory.Path, StringComparison.Ordinal);
        }

        return existing.Path.StartsWith(requested.Path, StringComparison.Ordinal)
            || requested.Path.StartsWith(existing.Path, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when this scope covers (authorizes mutation of) a normalized target of the same
    /// resource name, equal file path, or file path inside a directory prefix.
    /// </summary>
    public bool Covers(ReservationScope target)
    {
        if (Kind == ReservationScopeKind.Resource)
        {
            return target.Kind == ReservationScopeKind.Resource
                && string.Equals(Path, target.Path, StringComparison.Ordinal);
        }

        if (target.Kind == ReservationScopeKind.Resource)
        {
            return false;
        }

        if (Kind == ReservationScopeKind.File)
        {
            return target.Kind == ReservationScopeKind.File
                && string.Equals(Path, target.Path, StringComparison.Ordinal);
        }

        return target.Path.StartsWith(Path, StringComparison.Ordinal);
    }

    private static string RequireBounded(string value, ReservationScopeKind kind)
    {
        if (value.Length > MaxPathLength)
        {
            throw new InvalidReservationScopeException(
                $"Reservation scope path '{value}' exceeds the {MaxPathLength} character limit.");
        }

        _ = kind;
        return value;
    }

    /// <summary>
    /// True when one scope is <see cref="ProjectBuildResource"/> and the other is a file or directory.
    /// </summary>
    public static bool IsProjectBuildSourceConflict(ReservationScope existing, ReservationScope requested) =>
        (IsProjectBuild(existing) && IsSourceScope(requested))
        || (IsProjectBuild(requested) && IsSourceScope(existing));

    private static bool IsProjectBuild(ReservationScope scope) =>
        scope.Kind == ReservationScopeKind.Resource
        && string.Equals(scope.Path, ProjectBuildResource, StringComparison.Ordinal);

    private static bool IsSourceScope(ReservationScope scope) =>
        scope.Kind is ReservationScopeKind.File or ReservationScopeKind.Directory;
}
