using PiCommandCenter.Domain.Projects;

namespace PiCommandCenter.Domain.Requests;

public enum ExecutionAssignmentState
{
    Starting,
    Running,
    Finalizing,
    Cancelling,
    RecoveryRequired,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// Durable authorization for one request to execute on the snapshotted node and workspace.
/// Lease expiry removes recent proof of liveness; it never releases or reassigns the request.
/// </summary>
public sealed class ExecutionAssignment
{
    private ExecutionAssignment(
        WorkRequestId requestId,
        ProjectId projectId,
        WorkspaceBindingId workspaceBindingId,
        NodeId nodeIdSnapshot,
        string canonicalRepositoryPathSnapshot,
        string defaultBranchSnapshot,
        long bindingValidationRevisionSnapshot,
        ExecutionAssignmentState state,
        string claimToken,
        DateTimeOffset assignedAt,
        DateTimeOffset leaseExpiresAt,
        DateTimeOffset? lastRenewedAt,
        DateTimeOffset? lastReconciledAt,
        DateTimeOffset? terminalAt,
        long version)
    {
        RequestId = requestId;
        ProjectId = projectId;
        WorkspaceBindingId = workspaceBindingId;
        NodeIdSnapshot = nodeIdSnapshot;
        CanonicalRepositoryPathSnapshot = canonicalRepositoryPathSnapshot;
        DefaultBranchSnapshot = defaultBranchSnapshot;
        BindingValidationRevisionSnapshot = bindingValidationRevisionSnapshot;
        State = state;
        ClaimToken = claimToken;
        AssignedAt = assignedAt;
        LeaseExpiresAt = leaseExpiresAt;
        LastRenewedAt = lastRenewedAt;
        LastReconciledAt = lastReconciledAt;
        TerminalAt = terminalAt;
        Version = version;
    }

    /// <summary>The request identity is also the assignment's primary key.</summary>
    public WorkRequestId RequestId { get; }

    public ProjectId ProjectId { get; }

    public WorkspaceBindingId WorkspaceBindingId { get; }

    public NodeId NodeIdSnapshot { get; }

    public string CanonicalRepositoryPathSnapshot { get; }

    public string DefaultBranchSnapshot { get; }

    public long BindingValidationRevisionSnapshot { get; }

    public ExecutionAssignmentState State { get; private set; }

    /// <summary>Opaque token required with the assigned node identity for lease operations.</summary>
    public string ClaimToken { get; }

    public DateTimeOffset AssignedAt { get; }

    public DateTimeOffset LeaseExpiresAt { get; private set; }

    public DateTimeOffset? LastRenewedAt { get; private set; }

    public DateTimeOffset? LastReconciledAt { get; private set; }

    public DateTimeOffset? TerminalAt { get; private set; }

    /// <summary>Optimistic concurrency token.</summary>
    public long Version { get; private set; }

    /// <summary>Creates a starting assignment with an immutable validated placement snapshot.</summary>
    public static ExecutionAssignment Create(
        WorkRequestId requestId,
        ProjectId projectId,
        WorkspaceBindingId workspaceBindingId,
        NodeId nodeIdSnapshot,
        string canonicalRepositoryPathSnapshot,
        string defaultBranchSnapshot,
        long bindingValidationRevisionSnapshot,
        string claimToken,
        DateTimeOffset assignedAt,
        TimeSpan lease)
    {
        ValidateSnapshot(
            requestId,
            projectId,
            workspaceBindingId,
            nodeIdSnapshot,
            canonicalRepositoryPathSnapshot,
            defaultBranchSnapshot,
            bindingValidationRevisionSnapshot,
            claimToken);
        EnsurePositiveLease(lease);

        return new ExecutionAssignment(
            requestId,
            projectId,
            workspaceBindingId,
            nodeIdSnapshot,
            canonicalRepositoryPathSnapshot,
            defaultBranchSnapshot,
            bindingValidationRevisionSnapshot,
            ExecutionAssignmentState.Starting,
            claimToken,
            assignedAt,
            assignedAt + lease,
            lastRenewedAt: null,
            lastReconciledAt: null,
            terminalAt: null,
            version: 1);
    }

    /// <summary>Rehydrates persisted assignment and migration history without changing it.</summary>
    public static ExecutionAssignment Rehydrate(
        WorkRequestId requestId,
        ProjectId projectId,
        WorkspaceBindingId workspaceBindingId,
        NodeId nodeIdSnapshot,
        string canonicalRepositoryPathSnapshot,
        string defaultBranchSnapshot,
        long bindingValidationRevisionSnapshot,
        ExecutionAssignmentState state,
        string claimToken,
        DateTimeOffset assignedAt,
        DateTimeOffset leaseExpiresAt,
        DateTimeOffset? lastRenewedAt,
        DateTimeOffset? lastReconciledAt,
        DateTimeOffset? terminalAt,
        long version)
    {
        ValidateSnapshot(
            requestId,
            projectId,
            workspaceBindingId,
            nodeIdSnapshot,
            canonicalRepositoryPathSnapshot,
            defaultBranchSnapshot,
            bindingValidationRevisionSnapshot,
            claimToken);
        EnsureDefinedState(state);
        EnsureRehydratedTimeline(
            state,
            assignedAt,
            leaseExpiresAt,
            lastRenewedAt,
            lastReconciledAt,
            terminalAt,
            version);

        return new ExecutionAssignment(
            requestId,
            projectId,
            workspaceBindingId,
            nodeIdSnapshot,
            canonicalRepositoryPathSnapshot,
            defaultBranchSnapshot,
            bindingValidationRevisionSnapshot,
            state,
            claimToken,
            assignedAt,
            leaseExpiresAt,
            lastRenewedAt,
            lastReconciledAt,
            terminalAt,
            version);
    }

    /// <summary>Reports lease liveness without changing assignment ownership or state.</summary>
    public bool IsLeaseExpired(DateTimeOffset at) => LeaseExpiresAt <= at;

    /// <summary>Renews an active assignment for its immutable node and exact token.</summary>
    public DateTimeOffset Renew(NodeId nodeId, string claimToken, TimeSpan lease, DateTimeOffset at)
    {
        EnsureOwner(nodeId, claimToken);
        EnsureStateAllowsRenewal();
        EnsurePositiveLease(lease);
        EnsureOperationTime(at);
        if (IsLeaseExpired(at))
        {
            throw new InvalidOperationException(
                "The assignment lease has expired and requires explicit reconciliation.");
        }

        LastRenewedAt = at;
        LeaseExpiresAt = at + lease;
        Version++;
        return LeaseExpiresAt;
    }

    /// <summary>Transitions a newly started assignment to running.</summary>
    public void MarkRunning(DateTimeOffset at)
    {
        EnsureCurrentState(ExecutionAssignmentState.Starting, nameof(MarkRunning));
        TransitionTo(ExecutionAssignmentState.Running, at);
    }

    /// <summary>Closes normal execution admission while terminal quiescence is established.</summary>
    public void BeginFinalizing(DateTimeOffset at)
    {
        EnsureCurrentState(ExecutionAssignmentState.Running, nameof(BeginFinalizing));
        TransitionTo(ExecutionAssignmentState.Finalizing, at);
    }

    /// <summary>Closes execution admission and begins cancellation quiescence.</summary>
    public void BeginCancelling(DateTimeOffset at)
    {
        if (State is not ExecutionAssignmentState.Starting
            and not ExecutionAssignmentState.Running
            and not ExecutionAssignmentState.Finalizing
            and not ExecutionAssignmentState.RecoveryRequired)
        {
            throw InvalidTransition(nameof(BeginCancelling));
        }

        TransitionTo(ExecutionAssignmentState.Cancelling, at);
    }

    /// <summary>
    /// Preserves uncertain ownership for recovery. This operation never changes the immutable
    /// placement snapshot or makes the request assignable elsewhere.
    /// </summary>
    public void MarkRecoveryRequired(DateTimeOffset at)
    {
        if (State is not ExecutionAssignmentState.Starting
            and not ExecutionAssignmentState.Running
            and not ExecutionAssignmentState.Finalizing
            and not ExecutionAssignmentState.Cancelling)
        {
            throw InvalidTransition(nameof(MarkRecoveryRequired));
        }

        TransitionTo(ExecutionAssignmentState.RecoveryRequired, at);
    }

    /// <summary>
    /// Restores a recovery-required assignment only for its original node and token. Reconciliation
    /// renews the lease but cannot change the snapshotted binding, node, path, branch, or revision.
    /// </summary>
    public DateTimeOffset Reconcile(
        NodeId nodeId,
        string claimToken,
        ExecutionAssignmentState restoredState,
        TimeSpan lease,
        DateTimeOffset at)
    {
        EnsureOwner(nodeId, claimToken);
        EnsureCurrentState(ExecutionAssignmentState.RecoveryRequired, nameof(Reconcile));
        EnsureRestorableState(restoredState);
        EnsurePositiveLease(lease);
        EnsureOperationTime(at);

        State = restoredState;
        LastReconciledAt = at;
        LeaseExpiresAt = at + lease;
        Version++;
        return LeaseExpiresAt;
    }

    /// <summary>Completes a finalizing assignment after its quiescence barrier succeeds.</summary>
    public void Complete(DateTimeOffset at)
    {
        EnsureCurrentState(ExecutionAssignmentState.Finalizing, nameof(Complete));
        TransitionToTerminal(ExecutionAssignmentState.Completed, at);
    }

    /// <summary>Records failure after finalizing and proving quiescence.</summary>
    public void Fail(DateTimeOffset at)
    {
        EnsureCurrentState(ExecutionAssignmentState.Finalizing, nameof(Fail));
        TransitionToTerminal(ExecutionAssignmentState.Failed, at);
    }

    /// <summary>Records cancellation after cancellation quiescence succeeds.</summary>
    public void Cancel(DateTimeOffset at)
    {
        EnsureCurrentState(ExecutionAssignmentState.Cancelling, nameof(Cancel));
        TransitionToTerminal(ExecutionAssignmentState.Cancelled, at);
    }

    private void EnsureOwner(NodeId nodeId, string claimToken)
    {
        if (nodeId != NodeIdSnapshot)
        {
            throw new InvalidOperationException(
                $"Assignment is owned by node '{NodeIdSnapshot}', not '{nodeId}'.");
        }

        EnsureRequired(claimToken, nameof(claimToken), "Claim token");
        if (!string.Equals(ClaimToken, claimToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claim token does not match the assignment.");
        }
    }

    private void EnsureStateAllowsRenewal()
    {
        if (State is ExecutionAssignmentState.Starting
            or ExecutionAssignmentState.Running
            or ExecutionAssignmentState.Finalizing
            or ExecutionAssignmentState.Cancelling)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assignment in state '{State}' cannot be renewed.");
    }

    private void EnsureCurrentState(ExecutionAssignmentState expected, string operation)
    {
        if (State != expected)
        {
            throw new InvalidOperationException(
                $"'{operation}' requires state '{expected}' but assignment is '{State}'.");
        }
    }

    private InvalidOperationException InvalidTransition(string operation) => new(
        $"Assignment in state '{State}' cannot be transitioned by '{operation}'.");

    private void TransitionTo(ExecutionAssignmentState next, DateTimeOffset at)
    {
        EnsureOperationTime(at);
        State = next;
        Version++;
    }

    private void TransitionToTerminal(ExecutionAssignmentState next, DateTimeOffset at)
    {
        EnsureOperationTime(at);
        State = next;
        TerminalAt = at;
        Version++;
    }

    private void EnsureOperationTime(DateTimeOffset at)
    {
        var latestRecordedAt = AssignedAt;
        if (LastRenewedAt is { } renewedAt && renewedAt > latestRecordedAt)
        {
            latestRecordedAt = renewedAt;
        }

        if (LastReconciledAt is { } reconciledAt && reconciledAt > latestRecordedAt)
        {
            latestRecordedAt = reconciledAt;
        }

        if (at < latestRecordedAt)
        {
            throw new ArgumentException(
                "Assignment operations cannot precede its recorded history.",
                nameof(at));
        }
    }

    private static void ValidateSnapshot(
        WorkRequestId requestId,
        ProjectId projectId,
        WorkspaceBindingId workspaceBindingId,
        NodeId nodeIdSnapshot,
        string canonicalRepositoryPathSnapshot,
        string defaultBranchSnapshot,
        long bindingValidationRevisionSnapshot,
        string claimToken)
    {
        if (requestId.Value == Guid.Empty)
        {
            throw new ArgumentException("Request id must not be empty.", nameof(requestId));
        }

        if (projectId.Value == Guid.Empty)
        {
            throw new ArgumentException("Project id must not be empty.", nameof(projectId));
        }

        if (workspaceBindingId.Value == Guid.Empty)
        {
            throw new ArgumentException("Workspace binding id must not be empty.", nameof(workspaceBindingId));
        }

        if (nodeIdSnapshot.Value == Guid.Empty)
        {
            throw new ArgumentException("Node id snapshot must not be empty.", nameof(nodeIdSnapshot));
        }

        if (string.IsNullOrWhiteSpace(canonicalRepositoryPathSnapshot))
        {
            throw new ArgumentException(
                "Canonical repository path snapshot must not be empty.",
                nameof(canonicalRepositoryPathSnapshot));
        }

        if (!Path.IsPathFullyQualified(canonicalRepositoryPathSnapshot))
        {
            throw new ArgumentException(
                "Canonical repository path snapshot must be fully qualified.",
                nameof(canonicalRepositoryPathSnapshot));
        }

        EnsureRequired(
            defaultBranchSnapshot,
            nameof(defaultBranchSnapshot),
            "Default branch snapshot");
        if (bindingValidationRevisionSnapshot < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bindingValidationRevisionSnapshot),
                "Binding validation revision snapshot must be positive.");
        }

        EnsureRequired(claimToken, nameof(claimToken), "Claim token");
    }

    private static void EnsureRehydratedTimeline(
        ExecutionAssignmentState state,
        DateTimeOffset assignedAt,
        DateTimeOffset leaseExpiresAt,
        DateTimeOffset? lastRenewedAt,
        DateTimeOffset? lastReconciledAt,
        DateTimeOffset? terminalAt,
        long version)
    {
        if (leaseExpiresAt < assignedAt)
        {
            throw new ArgumentException(
                "Lease expiry must not precede assignment.",
                nameof(leaseExpiresAt));
        }

        EnsureOptionalTimeInLeaseHistory(lastRenewedAt, assignedAt, leaseExpiresAt, nameof(lastRenewedAt));
        EnsureOptionalTimeInLeaseHistory(lastReconciledAt, assignedAt, leaseExpiresAt, nameof(lastReconciledAt));

        var terminal = IsTerminal(state);
        if (terminal != terminalAt.HasValue)
        {
            throw new ArgumentException(
                terminal
                    ? "A terminal assignment must have a terminal timestamp."
                    : "A nonterminal assignment cannot have a terminal timestamp.",
                nameof(terminalAt));
        }

        if (terminalAt is { } endedAt)
        {
            if (endedAt < assignedAt
                || lastRenewedAt is { } renewedAt && renewedAt > endedAt
                || lastReconciledAt is { } reconciledAt && reconciledAt > endedAt)
            {
                throw new ArgumentException(
                    "Terminal timestamp cannot precede assignment history.",
                    nameof(terminalAt));
            }
        }

        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Version must be positive.");
        }
    }

    private static void EnsureOptionalTimeInLeaseHistory(
        DateTimeOffset? value,
        DateTimeOffset assignedAt,
        DateTimeOffset leaseExpiresAt,
        string parameterName)
    {
        if (value is { } at && (at < assignedAt || at > leaseExpiresAt))
        {
            throw new ArgumentException(
                "Assignment lease history must fall between assignment and lease expiry.",
                parameterName);
        }
    }

    private static void EnsurePositiveLease(TimeSpan lease)
    {
        if (lease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lease), "Lease duration must be positive.");
        }
    }

    private static void EnsureDefinedState(ExecutionAssignmentState state)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown assignment state.");
        }
    }

    private static void EnsureRestorableState(ExecutionAssignmentState state)
    {
        if (state is ExecutionAssignmentState.Starting
            or ExecutionAssignmentState.Running
            or ExecutionAssignmentState.Finalizing
            or ExecutionAssignmentState.Cancelling)
        {
            return;
        }

        throw new ArgumentException(
            $"State '{state}' cannot be restored by reconciliation.",
            nameof(state));
    }

    private static bool IsTerminal(ExecutionAssignmentState state) =>
        state is ExecutionAssignmentState.Completed
            or ExecutionAssignmentState.Failed
            or ExecutionAssignmentState.Cancelled;

    private static void EnsureRequired(string value, string parameterName, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{label} must not be empty.", parameterName);
        }
    }
}
