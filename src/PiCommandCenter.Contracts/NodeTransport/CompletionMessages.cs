using System.ComponentModel.DataAnnotations;

namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>Transport mirror of a review finding on completion evidence.</summary>
public sealed record ReviewFindingMessage(
    string Id,
    string Summary,
    bool Blocking,
    bool Resolved,
    bool UserOverridden);

/// <summary>Objective evidence submitted with terminalization commands.</summary>
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

/// <summary>The terminal outcome a node asks the control plane to commit.</summary>
public enum TerminalizationIntent
{
    Complete,
    Fail,
    Cancel,
}

/// <summary>
/// First step of the two-step terminalization authority: closes admission and moves the
/// assignment into Finalizing (Complete/Fail) or Cancelling (Cancel). Complete runs the
/// objective completion preflight before the state move; Fail/Cancel require a reason.
/// </summary>
public sealed record BeginTerminalizationMessage(
    Guid CorrelationId,
    Guid ProjectId,
    Guid RequestId,
    [property: Required, MaxLength(128)] string ClaimToken,
    string RootSessionId,
    TerminalizationIntent Intent,
    CompletionEvidenceMessage? Evidence,
    string? Reason);

/// <summary>
/// Node-attested proof that the assignment is quiescent. The control plane accepts a
/// confirmation only when every count is exactly zero and both flags are true.
/// </summary>
public sealed record AssignmentQuiescenceProofMessage(
    bool AdmissionClosed,
    int ActiveChildren,
    int ActiveOperations,
    int ActiveProcesses,
    int PendingEvents,
    int ActiveReservations,
    bool RepositoryInspected,
    DateTimeOffset ObservedAt);

/// <summary>
/// Second step: repeats the fence, intent, evidence, and reason and carries the quiescence
/// proof. A successful Confirm terminalizes the work request and the execution assignment
/// atomically (with the persisted result for Complete).
/// </summary>
public sealed record ConfirmTerminalizationMessage(
    Guid CorrelationId,
    Guid ProjectId,
    Guid RequestId,
    [property: Required, MaxLength(128)] string ClaimToken,
    string RootSessionId,
    TerminalizationIntent Intent,
    CompletionEvidenceMessage? Evidence,
    string? Reason,
    AssignmentQuiescenceProofMessage Proof);

/// <summary>
/// Typed terminalization decision. Rejection lists every missing requirement; the result is
/// exposed only after a successful Complete confirmation.
/// </summary>
public sealed record CompletionGateDecisionMessage(
    Guid CorrelationId,
    bool Accepted,
    IReadOnlyList<string> MissingRequirements,
    RequestResultMessage? Result);
