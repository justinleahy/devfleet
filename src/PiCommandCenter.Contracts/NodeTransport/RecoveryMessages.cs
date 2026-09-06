using System.ComponentModel.DataAnnotations;

namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>
/// Stable recovery blocker and scheduling reason codes. Existing readiness/startup
/// codes remain authoritative for those domains.
/// </summary>
public static class RecoveryReasonCodes
{
    public const string ProjectRecoveryPaused = "project_recovery_paused";
    public const string NodeUnreachable = "node_unreachable";
    public const string ProcessStopUnproven = "process_stop_unproven";
    public const string OperationDrainTimeout = "operation_drain_timeout";
    public const string EventsUnacknowledged = "events_unacknowledged";
    public const string ReservationUnresolved = "reservation_unresolved";
    public const string RepositoryStatusUnknown = "repository_status_unknown";
    public const string RecoveryEvidenceStale = "recovery_evidence_stale";
    public const string RecoveryTargetChanged = "recovery_target_changed";
}

/// <summary>
/// Known-versus-unknown inventory count. Valid only as exactly one of a nonnegative
/// <see cref="Value"/> with a blank unknown code, or a null value with a non-blank
/// <see cref="UnknownReasonCode"/>. Zero is a known empty inventory, not unknown.
/// </summary>
public sealed record RecoveryKnownCountMessage(int? Value, string? UnknownReasonCode)
{
    /// <summary>True when the count is a known nonnegative integer.</summary>
    public bool IsKnown =>
        Value is >= 0 && string.IsNullOrWhiteSpace(UnknownReasonCode);

    /// <summary>True when the count is unknown and a reason code is present.</summary>
    public bool IsUnknown =>
        Value is null && !string.IsNullOrWhiteSpace(UnknownReasonCode);

    /// <summary>Exactly one of known nonnegative value or non-blank unknown code.</summary>
    public bool IsValid => IsKnown || IsUnknown;
}

/// <summary>
/// Assignment-owned process identity used for stop proof. PID plus start time
/// distinguish reuse; group/scope identity is the isolation primitive. No command
/// lines, environment, or secrets.
/// </summary>
public sealed record RecoveryProcessIdentityMessage(
    int Pid,
    DateTimeOffset StartedAt,
    string? GroupOrScopeId,
    bool EscapedDescendant);

/// <summary>
/// Per-reservation disposition after stop evidence. Disposition is a bounded
/// status token, never a fencing secret or path dump.
/// </summary>
public sealed record RecoveryReservationDispositionMessage(
    Guid LeaseId,
    string Disposition,
    string? ReasonCode);

/// <summary>
/// Bounded repository inspection snapshot. Availability, refs, index/worktree
/// summaries, untracked count, and interrupted-operation indicators only. Never
/// file contents, diffs, credentials, or unbounded logs.
/// </summary>
public sealed record RecoveryRepositoryStatusMessage(
    bool Available,
    string? Head,
    string? Branch,
    string? IndexSummary,
    string? WorktreeSummary,
    RecoveryKnownCountMessage UntrackedCount,
    IReadOnlyList<string> InterruptedOperationIndicators,
    DateTimeOffset ObservedAt);

/// <summary>
/// Control-plane → node command to recover one assignment. Correlates to a durable
/// recovery operation and bounded attempt. <see cref="ClaimToken"/> remains a fence:
/// the node may act only while the token matches current assignment authority.
/// Recovery always stops/cancels; it never resumes interrupted execution.
/// </summary>
public sealed record RecoverAssignmentCommandMessage(
    Guid RecoveryId,
    int Attempt,
    Guid ProjectId,
    Guid RequestId,
    [property: Required, MaxLength(128)] string ClaimToken,
    long BindingRevision,
    DateTimeOffset Deadline);

/// <summary>
/// Node → control-plane progress for one recovery attempt. Inventories distinguish
/// known zero from unknown. Evidence is attempt- and assignment-specific; stale
/// attempt numbers cannot authorize release.
/// </summary>
public sealed record AssignmentRecoveryProgressMessage(
    Guid RecoveryId,
    int Attempt,
    Guid ProjectId,
    Guid RequestId,
    [property: Required, MaxLength(128)] string ClaimToken,
    long BindingRevision,
    DateTimeOffset ObservedAt,
    string? Stage,
    RecoveryKnownCountMessage Children,
    RecoveryKnownCountMessage Operations,
    RecoveryKnownCountMessage Processes,
    RecoveryKnownCountMessage PendingEvents,
    RecoveryKnownCountMessage Reservations,
    IReadOnlyList<string> ReasonCodes);

/// <summary>
/// Node-attested recovery quiescence proof for one attempt of one assignment.
/// The control plane accepts only when every inventory is known and zero, the
/// proof correlates to the current recovery id/attempt/assignment/binding, the
/// claim token still fences the assignment, and the repository snapshot is present.
/// Event acknowledgement is a known position or an explicit unknown code.
/// </summary>
public sealed record AssignmentRecoveryProofMessage(
    Guid RecoveryId,
    int Attempt,
    Guid ProjectId,
    Guid RequestId,
    [property: Required, MaxLength(128)] string ClaimToken,
    long BindingRevision,
    DateTimeOffset ObservedAt,
    bool AdmissionClosed,
    RecoveryKnownCountMessage Children,
    RecoveryKnownCountMessage Operations,
    RecoveryKnownCountMessage Processes,
    RecoveryKnownCountMessage PendingEvents,
    RecoveryKnownCountMessage Reservations,
    long? EventAcknowledgementPosition,
    string? EventAcknowledgementUnknownReasonCode,
    IReadOnlyList<RecoveryProcessIdentityMessage> ProcessIdentities,
    IReadOnlyList<RecoveryReservationDispositionMessage> ReservationDispositions,
    RecoveryRepositoryStatusMessage? Repository);


/// <summary>
/// Typed control-plane decision on a recovery proof. Rejection lists every missing
/// requirement using stable <see cref="RecoveryReasonCodes"/> values where applicable.
/// </summary>
public sealed record RecoveryProofDecisionMessage(
    Guid RecoveryId,
    int Attempt,
    Guid ProjectId,
    Guid RequestId,
    long BindingRevision,
    bool Accepted,
    IReadOnlyList<string> MissingRequirements);
