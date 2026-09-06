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
        string displayName,
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
        DisplayName = displayName;
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

    /// <summary>Normalized non-empty display name.</summary>
    public string DisplayName { get; private set; }

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
        string displayName,
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
        var (display, branch, writeLimit, readLimit, childLimit) = Normalize(
            displayName,
            defaultBranch,
            maxActiveWriteRequests,
            maxReadOnlyRequests,
            maxChildAgentsPerRequest);

        return new Project(
            ProjectId.New(),
            display,
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
        string displayName,
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
        var (display, branch, writeLimit, readLimit, childLimit) = Normalize(
            displayName,
            defaultBranch,
            maxActiveWriteRequests,
            maxReadOnlyRequests,
            maxChildAgentsPerRequest);

        return new Project(
            id,
            display,
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

    public void Update(
        string displayName,
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
        var (display, branch, writeLimit, readLimit, childLimit) = Normalize(
            displayName,
            defaultBranch,
            maxActiveWriteRequests,
            maxReadOnlyRequests,
            maxChildAgentsPerRequest);

        DisplayName = display;
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

    private static (string Display, string Branch, int WriteLimit, int ReadLimit, int ChildLimit) Normalize(
        string displayName,
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

        if (string.IsNullOrWhiteSpace(defaultBranch))
        {
            throw new ArgumentException("Default branch must not be empty.", nameof(defaultBranch));
        }

        var branch = defaultBranch.Trim();

        return (
            display,
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
