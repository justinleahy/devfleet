namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>Transport mirror of a review finding on completion evidence.</summary>
public sealed record ReviewFindingMessage(
    string Id,
    string Summary,
    bool Blocking,
    bool Resolved,
    bool UserOverridden);

/// <summary>Objective evidence submitted with <see cref="EvaluateCompletionMessage"/>.</summary>
public sealed record CompletionEvidenceMessage(
    string SummaryMarkdown,
    IReadOnlyList<string>? ChangedFiles,
    IReadOnlyList<ReviewFindingMessage> ReviewFindings,
    string VerificationSummary,
    string? RequestBranch = null,
    string? CheckpointCommitId = null);

/// <summary>Persisted request result returned after an accepted completion gate.</summary>
public sealed record RequestResultMessage(
    Guid RequestId,
    string SummaryMarkdown,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<ReviewFindingMessage> ReviewFindings,
    string VerificationSummary,
    DateTimeOffset CreatedAt,
    string? RequestBranch = null,
    string? CheckpointCommitId = null);

/// <summary>
/// One-argument hub payload for evaluating the objective completion gate.
/// Correlation, request, project, and root session identifiers are required.
/// </summary>
public sealed record EvaluateCompletionMessage(
    Guid CorrelationId,
    Guid ProjectId,
    Guid RequestId,
    string RootSessionId,
    CompletionEvidenceMessage Evidence);

/// <summary>
/// Typed gate outcome. Rejection lists every missing criterion; acceptance includes the result.
/// </summary>
public sealed record CompletionGateDecisionMessage(
    Guid CorrelationId,
    bool Accepted,
    IReadOnlyList<string> MissingRequirements,
    RequestResultMessage? Result);
