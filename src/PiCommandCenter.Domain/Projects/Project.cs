namespace PiCommandCenter.Domain.Projects;

/// <summary>
/// A registered project aggregate. Constructed only through <see cref="Project.Register"/> or
/// rehydration via <see cref="Project.Rehydrate"/> so invalid state is unrepresentable.
/// </summary>
public sealed class Project
{
    private const int MaxLimit = 512;
    public const int MaxTrustedVerificationProfileIdLength = 128;
    public const int MaxTrustedVerificationProfileRevisionLength = 128;


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
        string? trustedVerificationProfileId,
        string? trustedVerificationProfileRevision,
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
        TrustedVerificationProfileId = trustedVerificationProfileId;
        TrustedVerificationProfileRevision = trustedVerificationProfileRevision;
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

    /// <summary>Selected trusted project-check profile id, or null for baseline only.</summary>
    public string? TrustedVerificationProfileId { get; private set; }

    /// <summary>Node-reported revision of the selected trusted profile, or null for baseline only.</summary>
    public string? TrustedVerificationProfileRevision { get; private set; }

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
            trustedVerificationProfileId: null,
            trustedVerificationProfileRevision: null,
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
        long version,
        string? trustedVerificationProfileId = null,
        string? trustedVerificationProfileRevision = null)
    {
        var (display, branch, writeLimit, readLimit, childLimit) = Normalize(
            displayName,
            defaultBranch,
            maxActiveWriteRequests,
            maxReadOnlyRequests,
            maxChildAgentsPerRequest);
        var (profileId, profileRevision) = NormalizeOptionalPair(
            trustedVerificationProfileId,
            trustedVerificationProfileRevision);

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
            profileId,
            profileRevision,
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

    /// <summary>
    /// Selects one trusted project-check profile. Both id and revision are required together.
    /// </summary>
    public void SelectTrustedVerificationProfile(
        string profileId,
        string profileRevision,
        DateTimeOffset updatedAt)
    {
        var (id, revision) = NormalizeSelection(profileId, profileRevision);
        TrustedVerificationProfileId = id;
        TrustedVerificationProfileRevision = revision;
        UpdatedAt = updatedAt;
        Version++;
    }

    /// <summary>Clears the trusted profile so only baseline verification applies.</summary>
    public void ClearTrustedVerificationProfile(DateTimeOffset updatedAt)
    {
        TrustedVerificationProfileId = null;
        TrustedVerificationProfileRevision = null;
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

    private static (string Id, string Revision) NormalizeSelection(string profileId, string profileRevision)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("Trusted verification profile id must not be empty.", nameof(profileId));
        }

        if (string.IsNullOrWhiteSpace(profileRevision))
        {
            throw new ArgumentException(
                "Trusted verification profile revision must not be empty.",
                nameof(profileRevision));
        }

        var id = profileId.Trim();
        var revision = profileRevision.Trim();
        EnsureBounded(id, MaxTrustedVerificationProfileIdLength, nameof(profileId), "Trusted verification profile id");
        EnsureBounded(
            revision,
            MaxTrustedVerificationProfileRevisionLength,
            nameof(profileRevision),
            "Trusted verification profile revision");
        return (id, revision);
    }

    private static (string? Id, string? Revision) NormalizeOptionalPair(
        string? profileId,
        string? profileRevision)
    {
        var hasId = !string.IsNullOrWhiteSpace(profileId);
        var hasRevision = !string.IsNullOrWhiteSpace(profileRevision);
        if (hasId != hasRevision)
        {
            throw new ArgumentException(
                "Trusted verification profile id and revision must both be present or both be absent.");
        }

        if (!hasId)
        {
            return (null, null);
        }

        return NormalizeSelection(profileId!, profileRevision!);
    }

    private static void EnsureBounded(string value, int maxLength, string paramName, string label)
    {
        if (value.Length > maxLength)
        {
            throw new ArgumentException($"{label} must not exceed {maxLength} characters.", paramName);
        }
    }
}
