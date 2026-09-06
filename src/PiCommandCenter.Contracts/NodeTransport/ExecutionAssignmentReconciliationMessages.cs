namespace PiCommandCenter.Contracts.NodeTransport;

public enum AssignmentSupervisorState
{
    Running,
    Stopped,
    Unknown,
}

public enum AssignmentReconciliationDisposition
{
    Resume,
    Cancel,
    RecoveryRequired,
    Terminal,
}

/// <summary>
/// One node-local assignment journal entry and the runtime evidence currently available for it.
/// </summary>
public sealed record ExecutionAssignmentInventoryItemMessage(
    ExecutionAssignmentMessage Assignment,
    AssignmentSupervisorState SupervisorState,
    bool RepositoryKnown,
    int PendingEventCount);

/// <summary>Complete durable assignment inventory reported by one node connection.</summary>
public sealed record ReconcileAssignmentsMessage(
    Guid NodeId,
    int LeaseSeconds,
    IReadOnlyList<ExecutionAssignmentInventoryItemMessage> Assignments);

/// <summary>Best-effort live command to cancel the retained assignment owned by this node.</summary>
public sealed record CancelAssignmentCommand(Guid RequestId, string Reason);

/// <summary>The control plane's authoritative disposition for one inventory entry.</summary>
public sealed record AssignmentReconciliationResultMessage(
    Guid RequestId,
    AssignmentReconciliationDisposition Disposition,
    ExecutionAssignmentMessage? Assignment);

public sealed record ReconcileAssignmentsResultMessage(
    IReadOnlyList<AssignmentReconciliationResultMessage> Assignments);
