using System.Security.Cryptography;
using System.Text;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Application.Recovery;

/// <summary>
/// Durable recovery-operation statuses. The scheduling hold is stored separately and
/// survives <see cref="Recovered"/>.
/// </summary>
public enum RecoveryOperationStatus
{
    Pending,
    Running,
    NeedsIntervention,
    Recovered,
}

/// <summary>
/// Project-scoped recovery: diagnosis, durable start, progress, recheck, and hold resume.
/// Does not expose persistence rows.
/// </summary>
public interface IProjectRecoveryService
{
    Task<ProjectRecoveryDiagnosis> GetDiagnosisAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms the inventory revision and, when targets exist, atomically persists hold,
    /// operation, targets, idempotency, audit, and cancellation intent.
    /// Empty inventory is a no-op and does not create a hold.
    /// </summary>
    Task<ProjectRecoveryStartResult> StartAsync(
        ProjectId projectId,
        StartProjectRecoveryCommand command,
        CancellationToken cancellationToken = default);

    Task<ProjectRecoveryOperation> GetOperationAsync(
        ProjectId projectId,
        Guid operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Increments the attempt only from <see cref="RecoveryOperationStatus.NeedsIntervention"/>.
    /// Idempotent for the same key; rejected when <paramref name="expectedOperationVersion"/> is stale.
    /// </summary>
    Task<ProjectRecoveryOperation> RecheckAsync(
        ProjectId projectId,
        Guid operationId,
        long expectedOperationVersion,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the hold after the operation is <see cref="RecoveryOperationStatus.Recovered"/>
    /// and every captured assignment is terminal and every captured reservation is resolved.
    /// Rejected when <paramref name="expectedHoldVersion"/> is stale.
    /// </summary>
    Task ResumeAsync(
        ProjectId projectId,
        Guid operationId,
        long expectedHoldVersion,
        string actor,
        CancellationToken cancellationToken = default);
}

/// <summary>Operator confirmation for starting recovery. Reason, actor, and key must be non-blank.</summary>
public sealed record StartProjectRecoveryCommand(
    string InventoryRevision,
    string Reason,
    string Actor,
    string IdempotencyKey);

/// <summary>Read-only recovery panel: current inventory, hold, and latest operation.</summary>
public sealed record ProjectRecoveryDiagnosis(
    ProjectId ProjectId,
    long ProjectVersion,
    string InventoryRevision,
    bool HoldPresent,
    Guid? HoldOperationId,
    long? HoldVersion,
    ProjectRecoveryOperation? LatestOperation,
    IReadOnlyList<ProjectRecoveryAssignmentSnapshot> NonterminalAssignments,
    IReadOnlyList<ProjectRecoveryReservationSnapshot> UnresolvedReservations);

/// <summary>Start outcome. <see cref="NoOp"/> is true when the inventory was empty.</summary>
public sealed record ProjectRecoveryStartResult(
    bool NoOp,
    ProjectRecoveryOperation? Operation);

/// <summary>Durable recovery-operation progress without persistence types.</summary>
public sealed record ProjectRecoveryOperation(
    Guid Id,
    ProjectId ProjectId,
    RecoveryOperationStatus Status,
    int Attempt,
    long Version,
    string InventoryRevision,
    string Reason,
    string Actor,
    string? Stage,
    string? BlockerCodesJson,
    string? EvidenceJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? Deadline,
    IReadOnlyList<ProjectRecoveryAssignmentTarget> AssignmentTargets,
    IReadOnlyList<ProjectRecoveryReservationTarget> ReservationTargets);

public sealed record ProjectRecoveryAssignmentTarget(
    WorkRequestId RequestId,
    long CapturedVersion,
    string CapturedState,
    long BindingRevision,
    string? Outcome,
    string? EvidenceJson);

public sealed record ProjectRecoveryReservationTarget(
    Guid LeaseId,
    long CapturedVersion,
    string CapturedState,
    string? Outcome,
    string? EvidenceJson);

/// <summary>One nonterminal assignment contributing to the inventory revision.</summary>
public sealed record ProjectRecoveryAssignmentSnapshot(
    WorkRequestId RequestId,
    long Version,
    string State,
    long BindingRevision,
    Guid? AssignedNodeId = null,
    string? AssignedNodeDisplayName = null,
    string? CanonicalRepositoryPath = null,
    DateTimeOffset? AssignedAt = null,
    DateTimeOffset? LastRenewedAt = null,
    DateTimeOffset? LastReconciledAt = null,
    DateTimeOffset? LeaseExpiresAt = null,
    DateTimeOffset? NodeLastContact = null,
    string? NodeStatus = null);

/// <summary>One unresolved reservation contributing to the inventory revision.</summary>
public sealed record ProjectRecoveryReservationSnapshot(
    Guid LeaseId,
    long Version,
    string State,
    Guid? RequestId = null,
    string? OwnerSessionId = null,
    string? Reason = null,
    DateTimeOffset? ExpiresAt = null);

/// <summary>
/// Deterministic SHA-256 lowercase hex over ordered project version plus assignment and
/// reservation identity/version/state snapshots.
/// </summary>
public static class ProjectRecoveryInventory
{
    public static string ComputeRevision(
        long projectVersion,
        IReadOnlyList<ProjectRecoveryAssignmentSnapshot> assignments,
        IReadOnlyList<ProjectRecoveryReservationSnapshot> reservations)
    {
        var builder = new StringBuilder();
        builder.Append(projectVersion);
        builder.Append('\n');

        foreach (var assignment in assignments.OrderBy(a => a.RequestId.Value, Comparer<Guid>.Default))
        {
            builder.Append("a:");
            builder.Append(assignment.RequestId.Value.ToString("D"));
            builder.Append(':');
            builder.Append(assignment.Version);
            builder.Append(':');
            builder.Append(assignment.State);
            builder.Append(':');
            builder.Append(assignment.BindingRevision);
            builder.Append('\n');
        }

        foreach (var reservation in reservations.OrderBy(r => r.LeaseId, Comparer<Guid>.Default))
        {
            builder.Append("r:");
            builder.Append(reservation.LeaseId.ToString("D"));
            builder.Append(':');
            builder.Append(reservation.Version);
            builder.Append(':');
            builder.Append(reservation.State);
            builder.Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool IsUnresolved(RecoveryOperationStatus status) =>
        status is RecoveryOperationStatus.Pending
            or RecoveryOperationStatus.Running
            or RecoveryOperationStatus.NeedsIntervention;
}

public sealed class RecoveryInventoryConflictException(ProjectId projectId, string expectedRevision)
    : Exception($"Recovery inventory for project '{projectId.Value}' no longer matches revision '{expectedRevision}'.")
{
    public ProjectId ProjectId { get; } = projectId;

    public string ExpectedRevision { get; } = expectedRevision;
}

public sealed class RecoveryIdempotencyConflictException(ProjectId projectId, string action, string key)
    : Exception($"Idempotency key '{key}' for action '{action}' on project '{projectId.Value}' was reused with different input.")
{
    public ProjectId ProjectId { get; } = projectId;

    public string Action { get; } = action;

    public string Key { get; } = key;
}

public sealed class RecoveryOperationConflictException(ProjectId projectId, Guid existingOperationId)
    : Exception($"Project '{projectId.Value}' already has unresolved recovery operation '{existingOperationId}'.")
{
    public ProjectId ProjectId { get; } = projectId;

    public Guid ExistingOperationId { get; } = existingOperationId;
}

public sealed class RecoveryOperationNotFoundException(ProjectId projectId, Guid operationId)
    : Exception($"Recovery operation '{operationId}' was not found for project '{projectId.Value}'.")
{
    public ProjectId ProjectId { get; } = projectId;

    public Guid OperationId { get; } = operationId;
}

public sealed class RecoveryNotReadyException(string message) : Exception(message);

public sealed class RecoveryRevisionConflictException(
    ProjectId projectId,
    Guid? operationId,
    long expectedVersion)
    : Exception($"Recovery revision {expectedVersion} is stale for project '{projectId.Value}'.")
{
    public ProjectId ProjectId { get; } = projectId;

    public Guid? OperationId { get; } = operationId;

    public long ExpectedVersion { get; } = expectedVersion;
}
