namespace PiCommandCenter.Domain.Projects;

/// <summary>
/// A registered project aggregate. Constructed only through <see cref="Project.Register"/> or
/// rehydration via <see cref="Project.Rehydrate"/> so invalid state is unrepresentable.
/// </summary>
public sealed class Project
{
    private const int MaxLimit = 512;

    private Project(
        ProjectId id,
        NodeId nodeId,
        string displayName,
        string repositoryPath,
        string defaultBranch,
        bool enabled,
        int maxActiveWriteRequests,
        int maxReadOnlyRequests,
        int maxChildAgentsPerRequest,
        bool requireCleanStart,
        bool createRequestBranch,
        bool createRequestCommit,
        bool autoMerge,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long version)
    {
        Id = id;
        NodeId = nodeId;
        DisplayName = displayName;
        RepositoryPath = repositoryPath;
        DefaultBranch = defaultBranch;
        Enabled = enabled;
        MaxActiveWriteRequests = maxActiveWriteRequests;
        MaxReadOnlyRequests = maxReadOnlyRequests;
        MaxChildAgentsPerRequest = maxChildAgentsPerRequest;
        RequireCleanStart = requireCleanStart;
        CreateRequestBranch = createRequestBranch;
        CreateRequestCommit = createRequestCommit;
        AutoMerge = autoMerge;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        Version = version;
    }

    public ProjectId Id { get; }

    public NodeId NodeId { get; }

    /// <summary>Normalized non-empty display name.</summary>
    public string DisplayName { get; private set; }

    /// <summary>Single canonical (trimmed) repository path used for duplicate detection.</summary>
    public string RepositoryPath { get; private set; }

    /// <summary>Normalized non-empty default branch.</summary>
    public string DefaultBranch { get; private set; }

    public bool Enabled { get; private set; }

    public int MaxActiveWriteRequests { get; private set; }

    public int MaxReadOnlyRequests { get; private set; }

    public int MaxChildAgentsPerRequest { get; private set; }

    public bool RequireCleanStart { get; private set; }

    public bool CreateRequestBranch { get; private set; }

    public bool CreateRequestCommit { get; private set; }

    public bool AutoMerge { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic concurrency token.</summary>
    public long Version { get; private set; }

    /// <summary>
    /// Creates a new project. Throws <see cref="ArgumentException"/> when any invariant is violated.
    /// </summary>
    public static Project Register(
        NodeId nodeId,
        string displayName,
        string repositoryPath,
        string defaultBranch,
        bool enabled,
        int maxActiveWriteRequests,
        int maxReadOnlyRequests,
        int maxChildAgentsPerRequest,
        bool requireCleanStart,
        bool createRequestBranch,
        bool createRequestCommit,
        bool autoMerge,
        DateTimeOffset createdAt)
    {
        var (display, path, branch, writeLimit, readLimit, childLimit) = Normalize(
            displayName,
            repositoryPath,
            defaultBranch,
            maxActiveWriteRequests,
            maxReadOnlyRequests,
            maxChildAgentsPerRequest);

        return new Project(
            ProjectId.New(),
            nodeId,
            display,
            path,
            branch,
            enabled,
            writeLimit,
            readLimit,
            childLimit,
            requireCleanStart,
            createRequestBranch,
            createRequestCommit,
            autoMerge,
            createdAt,
            createdAt,
            version: 1);
    }

    /// <summary>
    /// Rehydrates a persisted project without mutating timestamps or version.
    /// </summary>
    public static Project Rehydrate(
        ProjectId id,
        NodeId nodeId,
        string displayName,
        string repositoryPath,
        string defaultBranch,
        bool enabled,
        int maxActiveWriteRequests,
        int maxReadOnlyRequests,
        int maxChildAgentsPerRequest,
        bool requireCleanStart,
        bool createRequestBranch,
        bool createRequestCommit,
        bool autoMerge,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long version)
    {
        var (display, path, branch, writeLimit, readLimit, childLimit) = Normalize(
            displayName,
            repositoryPath,
            defaultBranch,
            maxActiveWriteRequests,
            maxReadOnlyRequests,
            maxChildAgentsPerRequest);

        return new Project(
            id,
            nodeId,
            display,
            path,
            branch,
            enabled,
            writeLimit,
            readLimit,
            childLimit,
            requireCleanStart,
            createRequestBranch,
            createRequestCommit,
            autoMerge,
            createdAt,
            updatedAt,
            version);
    }

    /// <summary>
    /// Canonical comparison key for repository paths: trims surrounding whitespace, resolves to a
    /// full path, and strips trailing directory separators while preserving a filesystem root.
    /// </summary>
    public static string CanonicalizePath(string repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            throw new ArgumentException("Repository path must not be empty.", nameof(repositoryPath));
        }

        var fullPath = Path.GetFullPath(repositoryPath.Trim());
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    public void Update(
        string displayName,
        string repositoryPath,
        string defaultBranch,
        bool enabled,
        int maxActiveWriteRequests,
        int maxReadOnlyRequests,
        int maxChildAgentsPerRequest,
        bool requireCleanStart,
        bool createRequestBranch,
        bool createRequestCommit,
        bool autoMerge,
        DateTimeOffset updatedAt)
    {
        var (display, path, branch, writeLimit, readLimit, childLimit) = Normalize(
            displayName,
            repositoryPath,
            defaultBranch,
            maxActiveWriteRequests,
            maxReadOnlyRequests,
            maxChildAgentsPerRequest);

        DisplayName = display;
        RepositoryPath = path;
        DefaultBranch = branch;
        Enabled = enabled;
        MaxActiveWriteRequests = writeLimit;
        MaxReadOnlyRequests = readLimit;
        MaxChildAgentsPerRequest = childLimit;
        RequireCleanStart = requireCleanStart;
        CreateRequestBranch = createRequestBranch;
        CreateRequestCommit = createRequestCommit;
        AutoMerge = autoMerge;
        UpdatedAt = updatedAt;
        Version++;
    }

    private static (string Display, string Path, string Branch, int WriteLimit, int ReadLimit, int ChildLimit) Normalize(
        string displayName,
        string repositoryPath,
        string defaultBranch,
        int maxActiveWriteRequests,
        int maxReadOnlyRequests,
        int maxChildAgentsPerRequest)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Display name must not be empty.", nameof(displayName));
        }

        var display = displayName.Trim();
        if (display.Length == 0)
        {
            throw new ArgumentException("Display name must not be empty.", nameof(displayName));
        }

        var path = CanonicalizePath(repositoryPath);
        if (string.IsNullOrWhiteSpace(defaultBranch))
        {
            throw new ArgumentException("Default branch must not be empty.", nameof(defaultBranch));
        }

        var branch = defaultBranch.Trim();
        if (branch.Length == 0)
        {
            throw new ArgumentException("Default branch must not be empty.", nameof(defaultBranch));
        }

        return (
            display,
            path,
            branch,
            EnsurePositive(maxActiveWriteRequests, nameof(maxActiveWriteRequests)),
            EnsurePositive(maxReadOnlyRequests, nameof(maxReadOnlyRequests)),
            EnsurePositive(maxChildAgentsPerRequest, nameof(maxChildAgentsPerRequest)));
    }

    private static int EnsurePositive(int value, string paramName)
    {
        if (value < 1)
        {
            throw new ArgumentException("Concurrency limit must be a positive integer.", paramName);
        }

        return Math.Min(value, MaxLimit);
    }
}
