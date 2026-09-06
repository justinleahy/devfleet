namespace PiCommandCenter.Domain.Projects;

/// <summary>Strongly typed identifier for a workspace binding.</summary>
public readonly record struct WorkspaceBindingId
{
    public WorkspaceBindingId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Workspace binding id must not be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static WorkspaceBindingId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}

public enum WorkspaceBindingStatus
{
    PendingValidation,
    Valid,
    Invalid,
}

/// <summary>
/// A revisioned designation of one node-local repository for a project. The designation path is
/// treated only as an opaque absolute-path identifier; only the node supplies a canonical path.
/// </summary>
public sealed class WorkspaceBinding
{
    public const int MaxValidationCodeLength = 64;
    public const int MaxValidationDetailLength = 512;
    public const string ValidValidationCode = "valid";
    public const string RepositoryInitializationRequiredValidationCode = "repository_initialization_required";
    public const string BaselineCommitRequiredValidationCode = "baseline_commit_required";

    /// <summary>Valid results are classified as committed, ordinary, or unborn; nothing else is valid.</summary>
    public static bool IsValidValidationCode(string? code) =>
        code is ValidValidationCode
            or RepositoryInitializationRequiredValidationCode
            or BaselineCommitRequiredValidationCode;

    private WorkspaceBinding(
        WorkspaceBindingId id,
        ProjectId projectId,
        NodeId nodeId,
        string repositoryPath,
        string? canonicalRepositoryPath,
        WorkspaceBindingStatus status,
        long validationRevision,
        string? validationCode,
        string? validationDetail,
        DateTimeOffset? validatedAt,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long version)
    {
        Id = id;
        ProjectId = projectId;
        NodeId = nodeId;
        RepositoryPath = repositoryPath;
        CanonicalRepositoryPath = canonicalRepositoryPath;
        Status = status;
        ValidationRevision = validationRevision;
        ValidationCode = validationCode;
        ValidationDetail = validationDetail;
        ValidatedAt = validatedAt;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        Version = version;
    }

    public WorkspaceBindingId Id { get; }

    public ProjectId ProjectId { get; }

    public NodeId NodeId { get; private set; }

    /// <summary>The trimmed absolute path designated by the operator for interpretation by the node.</summary>
    public string RepositoryPath { get; private set; }

    /// <summary>The canonical path returned by the node for a valid current revision.</summary>
    public string? CanonicalRepositoryPath { get; private set; }

    public WorkspaceBindingStatus Status { get; private set; }

    public long ValidationRevision { get; private set; }

    public string? ValidationCode { get; private set; }

    public string? ValidationDetail { get; private set; }

    public DateTimeOffset? ValidatedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic concurrency token.</summary>
    public long Version { get; private set; }

    public static WorkspaceBinding Designate(
        ProjectId projectId,
        NodeId nodeId,
        string repositoryPath,
        DateTimeOffset designatedAt)
    {
        EnsureId(projectId.Value, nameof(projectId));
        EnsureId(nodeId.Value, nameof(nodeId));
        var path = NormalizeAbsolutePath(repositoryPath, nameof(repositoryPath));

        return new WorkspaceBinding(
            WorkspaceBindingId.New(),
            projectId,
            nodeId,
            path,
            canonicalRepositoryPath: null,
            WorkspaceBindingStatus.PendingValidation,
            validationRevision: 1,
            validationCode: null,
            validationDetail: null,
            validatedAt: null,
            designatedAt,
            designatedAt,
            version: 1);
    }

    /// <summary>Rehydrates persisted state without advancing its revision, timestamps, or version.</summary>
    public static WorkspaceBinding Rehydrate(
        WorkspaceBindingId id,
        ProjectId projectId,
        NodeId nodeId,
        string repositoryPath,
        string? canonicalRepositoryPath,
        WorkspaceBindingStatus status,
        long validationRevision,
        string? validationCode,
        string? validationDetail,
        DateTimeOffset? validatedAt,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long version)
    {
        EnsureId(id.Value, nameof(id));
        EnsureId(projectId.Value, nameof(projectId));
        EnsureId(nodeId.Value, nameof(nodeId));
        EnsurePositive(validationRevision, nameof(validationRevision));
        EnsurePositive(version, nameof(version));
        if (updatedAt < createdAt)
        {
            throw new ArgumentException("UpdatedAt must not precede CreatedAt.", nameof(updatedAt));
        }

        if (validatedAt is { } validationTime
            && (validationTime < createdAt || validationTime > updatedAt))
        {
            throw new ArgumentException(
                "ValidatedAt must fall between CreatedAt and UpdatedAt.",
                nameof(validatedAt));
        }

        var path = NormalizeAbsolutePath(repositoryPath, nameof(repositoryPath));
        var state = NormalizeValidationState(
            status,
            canonicalRepositoryPath,
            validationCode,
            validationDetail,
            validatedAt);

        return new WorkspaceBinding(
            id,
            projectId,
            nodeId,
            path,
            state.CanonicalRepositoryPath,
            status,
            validationRevision,
            state.ValidationCode,
            state.ValidationDetail,
            validatedAt,
            createdAt,
            updatedAt,
            version);
    }

    /// <summary>Changes the node-local designation and starts a new pending validation revision.</summary>
    public void Redesignate(NodeId nodeId, string repositoryPath, DateTimeOffset designatedAt)
    {
        EnsureId(nodeId.Value, nameof(nodeId));
        var path = NormalizeAbsolutePath(repositoryPath, nameof(repositoryPath));
        var revision = checked(ValidationRevision + 1);

        NodeId = nodeId;
        RepositoryPath = path;
        CanonicalRepositoryPath = null;
        Status = WorkspaceBindingStatus.PendingValidation;
        ValidationRevision = revision;
        ValidationCode = null;
        ValidationDetail = null;
        ValidatedAt = null;
        UpdatedAt = designatedAt;
        Version++;
    }

    /// <summary>
    /// Applies a result only when it was produced by the designated node for the current revision.
    /// Returns false for stale or wrong-node responses without mutating state.
    /// </summary>
    public bool ApplyValidationResult(
        NodeId validatingNodeId,
        long revision,
        WorkspaceBindingStatus status,
        string validationCode,
        string? validationDetail,
        string? canonicalRepositoryPath,
        DateTimeOffset validatedAt)
    {
        if (validatingNodeId != NodeId || revision != ValidationRevision)
        {
            return false;
        }

        if (status == WorkspaceBindingStatus.PendingValidation)
        {
            throw new ArgumentException("A validation result must be valid or invalid.", nameof(status));
        }

        var state = NormalizeValidationState(
            status,
            canonicalRepositoryPath,
            validationCode,
            validationDetail,
            validatedAt);

        CanonicalRepositoryPath = state.CanonicalRepositoryPath;
        Status = status;
        ValidationCode = state.ValidationCode;
        ValidationDetail = state.ValidationDetail;
        ValidatedAt = validatedAt;
        UpdatedAt = validatedAt;
        Version++;
        return true;
    }

    private static ValidationState NormalizeValidationState(
        WorkspaceBindingStatus status,
        string? canonicalRepositoryPath,
        string? validationCode,
        string? validationDetail,
        DateTimeOffset? validatedAt)
    {
        if (status == WorkspaceBindingStatus.PendingValidation)
        {
            if (canonicalRepositoryPath is not null
                || validationCode is not null
                || validationDetail is not null
                || validatedAt is not null)
            {
                throw new ArgumentException("Pending validation must not carry a validation result.", nameof(status));
            }

            return new ValidationState(null, null, null);
        }

        if (status is not WorkspaceBindingStatus.Valid and not WorkspaceBindingStatus.Invalid)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown workspace binding status.");
        }

        if (validatedAt is null)
        {
            throw new ArgumentException("A completed validation requires a timestamp.", nameof(validatedAt));
        }

        var code = NormalizeValidationCode(validationCode);
        var detail = NormalizeValidationDetail(validationDetail, status == WorkspaceBindingStatus.Invalid);

        if (status == WorkspaceBindingStatus.Valid)
        {
            if (!IsValidValidationCode(code))
            {
                throw new ArgumentException(
                    "A valid result must use a recognized preparation classification code.",
                    nameof(validationCode));
            }
            var canonicalPath = NormalizeAbsolutePath(
                canonicalRepositoryPath,
                nameof(canonicalRepositoryPath));
            return new ValidationState(canonicalPath, code, detail);
        }

        if (IsValidValidationCode(code))
        {
            throw new ArgumentException(
                "An invalid result must not use a valid preparation classification code.",
                nameof(validationCode));
        }

        if (canonicalRepositoryPath is not null)
        {
            throw new ArgumentException(
                "An invalid result must not carry a canonical repository path.",
                nameof(canonicalRepositoryPath));
        }

        return new ValidationState(null, code, detail);
    }

    private static string NormalizeAbsolutePath(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Repository path must not be empty.", paramName);
        }

        var path = value.Trim();
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Repository path must be absolute.", paramName);
        }

        if (path.Any(char.IsControl))
        {
            throw new ArgumentException("Repository path must not contain control characters.", paramName);
        }

        return path;
    }

    private static string NormalizeValidationCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Validation code must not be empty.", nameof(value));
        }

        var code = value.Trim();
        if (code.Length > MaxValidationCodeLength
            || !char.IsAsciiLetter(code[0])
            || code.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')
            || code.Any(char.IsUpper))
        {
            throw new ArgumentException(
                "Validation code must be a bounded lowercase ASCII identifier.",
                nameof(value));
        }

        return code;
    }

    private static string? NormalizeValidationDetail(string? value, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                throw new ArgumentException("Invalid validation requires safe detail.", nameof(value));
            }

            return null;
        }

        var detail = value.Trim();
        if (detail.Any(char.IsControl))
        {
            throw new ArgumentException("Validation detail must not contain control characters.", nameof(value));
        }

        return detail.Length <= MaxValidationDetailLength
            ? detail
            : detail[..MaxValidationDetailLength];
    }

    private static void EnsureId(Guid value, string paramName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Identifier must not be empty.", paramName);
        }
    }

    private static void EnsurePositive(long value, string paramName)
    {
        if (value < 1)
        {
            throw new ArgumentException("Value must be positive.", paramName);
        }
    }

    private readonly record struct ValidationState(
        string? CanonicalRepositoryPath,
        string? ValidationCode,
        string? ValidationDetail);
}
