using PiCommandCenter.Domain;

namespace PiCommandCenter.Application.Recovery;

/// <summary>
/// Administrator-attested recovery transition. Never a <c>force=true</c> unlock and never
/// node-observed proof. The scheduling hold is retained on success.
/// </summary>
public interface IManualProjectRecoveryService
{
    /// <summary>
    /// Validates operator attestation against the current <see cref="RecoveryOperationStatus.NeedsIntervention"/>
    /// operation and, when accepted, atomically cancels remaining targets, fences captured
    /// reservations, audits provenance, and marks the operation recovered.
    /// Same idempotency key and input replays; different input conflicts.
    /// </summary>
    Task<ProjectRecoveryOperation> ConfirmManualAsync(
        ProjectId projectId,
        ConfirmManualProjectRecoveryCommand command,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Administrator evidence for manual recovery. Confirmations and dated owning-workspace
/// status cannot be waived. Evidence is operator attestation, never node proof.
/// </summary>
public sealed record ConfirmManualProjectRecoveryCommand(
    Guid OperationId,
    long ExpectedOperationVersion,
    int ExpectedAttempt,
    string ExactProjectName,
    string Reason,
    string Actor,
    string IdempotencyKey,
    bool ConfirmOriginalExecutionCannotResume,
    bool WriterAccessPrevented,
    bool AcknowledgeEvidenceGaps,
    string ProcessStopEvidence,
    string RepositoryStatusSnapshot,
    string RepositoryStatusSource,
    DateTimeOffset RepositoryCollectedAt,
    string ReservationAndEventGapAccounting);
