using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Domain.Tests.Requests;

public class ExecutionAssignmentTests
{
    private static readonly DateTimeOffset AssignedAt = new(2026, 9, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    [Fact]
    public void Create_captures_an_immutable_validated_snapshot()
    {
        var requestId = WorkRequestId.New();
        var projectId = ProjectId.New();
        var bindingId = WorkspaceBindingId.New();
        var nodeId = NodeId.New();

        var assignment = ExecutionAssignment.Create(
            requestId,
            projectId,
            bindingId,
            nodeId,
            "/srv/work/project",
            "main",
            bindingValidationRevisionSnapshot: 7,
            "token-1",
            AssignedAt,
            Lease);

        assignment.MarkRunning(AssignedAt.AddMinutes(1));

        Assert.Equal(requestId, assignment.RequestId);
        Assert.Equal(projectId, assignment.ProjectId);
        Assert.Equal(bindingId, assignment.WorkspaceBindingId);
        Assert.Equal(nodeId, assignment.NodeIdSnapshot);
        Assert.Equal("/srv/work/project", assignment.CanonicalRepositoryPathSnapshot);
        Assert.Equal("main", assignment.DefaultBranchSnapshot);
        Assert.Equal(7, assignment.BindingValidationRevisionSnapshot);
        Assert.Equal("token-1", assignment.ClaimToken);
        Assert.Equal(AssignedAt, assignment.AssignedAt);
        Assert.Equal(AssignedAt + Lease, assignment.LeaseExpiresAt);
        Assert.Null(assignment.LastRenewedAt);
        Assert.Null(assignment.LastReconciledAt);
        Assert.Null(assignment.TerminalAt);
        Assert.Equal(ExecutionAssignmentState.Running, assignment.State);
        Assert.Equal(2, assignment.Version);
    }

    [Fact]
    public void Create_rejects_an_invalid_snapshot()
    {
        Assert.Throws<ArgumentException>(() => ExecutionAssignment.Create(
            WorkRequestId.New(),
            ProjectId.New(),
            WorkspaceBindingId.New(),
            NodeId.New(),
            "relative/project",
            "main",
            bindingValidationRevisionSnapshot: 1,
            "token-1",
            AssignedAt,
            Lease));

        Assert.Throws<ArgumentOutOfRangeException>(() => ExecutionAssignment.Create(
            WorkRequestId.New(),
            ProjectId.New(),
            WorkspaceBindingId.New(),
            NodeId.New(),
            "/srv/work/project",
            "main",
            bindingValidationRevisionSnapshot: 0,
            "token-1",
            AssignedAt,
            Lease));
    }

    [Fact]
    public void Rehydrate_restores_a_migrated_recovery_required_assignment()
    {
        var requestId = WorkRequestId.New();
        var projectId = ProjectId.New();
        var bindingId = WorkspaceBindingId.New();
        var nodeId = NodeId.New();

        var assignment = ExecutionAssignment.Rehydrate(
            requestId,
            projectId,
            bindingId,
            nodeId,
            "/srv/work/project",
            "main",
            bindingValidationRevisionSnapshot: 1,
            ExecutionAssignmentState.RecoveryRequired,
            "migrated-token",
            AssignedAt,
            AssignedAt + Lease,
            lastRenewedAt: null,
            lastReconciledAt: null,
            terminalAt: null,
            version: 9);

        Assert.Equal(requestId, assignment.RequestId);
        Assert.Equal(projectId, assignment.ProjectId);
        Assert.Equal(bindingId, assignment.WorkspaceBindingId);
        Assert.Equal(nodeId, assignment.NodeIdSnapshot);
        Assert.Equal(ExecutionAssignmentState.RecoveryRequired, assignment.State);
        Assert.Null(assignment.TerminalAt);
        Assert.Equal(9, assignment.Version);
    }

    [Fact]
    public void Renew_requires_the_snapshotted_node_and_exact_token()
    {
        var assignment = CreateAssignment();
        var originalExpiry = assignment.LeaseExpiresAt;

        Assert.Throws<InvalidOperationException>(() => assignment.Renew(
            NodeId.New(), assignment.ClaimToken, Lease, AssignedAt.AddMinutes(1)));
        Assert.Throws<InvalidOperationException>(() => assignment.Renew(
            assignment.NodeIdSnapshot, "wrong-token", Lease, AssignedAt.AddMinutes(1)));
        Assert.Throws<InvalidOperationException>(() => assignment.Renew(
            assignment.NodeIdSnapshot, " token-1 ", Lease, AssignedAt.AddMinutes(1)));

        Assert.Equal(originalExpiry, assignment.LeaseExpiresAt);
        Assert.Null(assignment.LastRenewedAt);
        Assert.Equal(1, assignment.Version);
    }

    [Fact]
    public void Renew_updates_lease_history_for_an_active_assignment()
    {
        var assignment = CreateAssignment();
        var renewedAt = AssignedAt.AddMinutes(2);

        var expiresAt = assignment.Renew(
            assignment.NodeIdSnapshot,
            assignment.ClaimToken,
            Lease,
            renewedAt);

        Assert.Equal(renewedAt + Lease, expiresAt);
        Assert.Equal(renewedAt, assignment.LastRenewedAt);
        Assert.Equal(ExecutionAssignmentState.Starting, assignment.State);
        Assert.Equal(2, assignment.Version);
    }

    [Fact]
    public void Lease_expiry_does_not_release_or_transition_the_assignment()
    {
        var assignment = CreateAssignment();
        var afterExpiry = assignment.LeaseExpiresAt.AddSeconds(1);

        Assert.True(assignment.IsLeaseExpired(afterExpiry));
        Assert.Throws<InvalidOperationException>(() => assignment.Renew(
            assignment.NodeIdSnapshot,
            assignment.ClaimToken,
            Lease,
            afterExpiry));

        Assert.Equal(ExecutionAssignmentState.Starting, assignment.State);
        Assert.Null(assignment.TerminalAt);
        Assert.Equal(1, assignment.Version);
    }

    [Fact]
    public void Recovery_required_assignments_cannot_be_normally_renewed()
    {
        var assignment = CreateAssignment();
        assignment.MarkRecoveryRequired(AssignedAt.AddMinutes(1));
        var version = assignment.Version;

        Assert.Throws<InvalidOperationException>(() => assignment.Renew(
            assignment.NodeIdSnapshot,
            assignment.ClaimToken,
            Lease,
            AssignedAt.AddMinutes(2)));

        Assert.Equal(ExecutionAssignmentState.RecoveryRequired, assignment.State);
        Assert.Equal(version, assignment.Version);
    }

    [Fact]
    public void Reconcile_restores_only_an_active_state_for_the_same_owner_and_snapshot()
    {
        var assignment = CreateAssignment();
        assignment.MarkRunning(AssignedAt.AddMinutes(1));
        assignment.MarkRecoveryRequired(AssignedAt.AddMinutes(2));
        var reconciledAt = assignment.LeaseExpiresAt.AddMinutes(1);

        var expiresAt = assignment.Reconcile(
            assignment.NodeIdSnapshot,
            assignment.ClaimToken,
            ExecutionAssignmentState.Running,
            Lease,
            reconciledAt);

        Assert.Equal(ExecutionAssignmentState.Running, assignment.State);
        Assert.Equal(reconciledAt, assignment.LastReconciledAt);
        Assert.Equal(reconciledAt + Lease, expiresAt);
        Assert.Equal("/srv/work/project", assignment.CanonicalRepositoryPathSnapshot);
        Assert.Equal(1, assignment.BindingValidationRevisionSnapshot);

        assignment.MarkRecoveryRequired(reconciledAt.AddMinutes(1));
        Assert.Throws<ArgumentException>(() => assignment.Reconcile(
            assignment.NodeIdSnapshot,
            assignment.ClaimToken,
            ExecutionAssignmentState.Completed,
            Lease,
            reconciledAt.AddMinutes(2)));

        Assert.Equal(ExecutionAssignmentState.RecoveryRequired, assignment.State);
        Assert.Equal(5, assignment.Version);
    }

    [Fact]
    public void Running_assignment_can_finalize_and_complete()
    {
        var assignment = CreateAssignment();
        var completedAt = AssignedAt.AddMinutes(3);

        assignment.MarkRunning(AssignedAt.AddMinutes(1));
        assignment.BeginFinalizing(AssignedAt.AddMinutes(2));
        assignment.Complete(completedAt);

        Assert.Equal(ExecutionAssignmentState.Completed, assignment.State);
        Assert.Equal(completedAt, assignment.TerminalAt);
        Assert.Throws<InvalidOperationException>(() => assignment.BeginCancelling(completedAt.AddSeconds(1)));
    }

    [Fact]
    public void Finalizing_assignment_can_fail()
    {
        var assignment = CreateAssignment();
        var failedAt = AssignedAt.AddMinutes(3);

        assignment.MarkRunning(AssignedAt.AddMinutes(1));
        assignment.BeginFinalizing(AssignedAt.AddMinutes(2));
        assignment.Fail(failedAt);

        Assert.Equal(ExecutionAssignmentState.Failed, assignment.State);
        Assert.Equal(failedAt, assignment.TerminalAt);
    }

    [Fact]
    public void Nonterminal_assignment_can_cancel_only_after_cancellation_quiescence()
    {
        var assignment = CreateAssignment();
        var cancelledAt = AssignedAt.AddMinutes(2);

        Assert.Throws<InvalidOperationException>(() => assignment.Cancel(cancelledAt));

        assignment.BeginCancelling(AssignedAt.AddMinutes(1));
        assignment.Cancel(cancelledAt);

        Assert.Equal(ExecutionAssignmentState.Cancelled, assignment.State);
        Assert.Equal(cancelledAt, assignment.TerminalAt);
    }

    [Theory]
    [InlineData(ExecutionAssignmentState.Completed)]
    [InlineData(ExecutionAssignmentState.Failed)]
    [InlineData(ExecutionAssignmentState.Cancelled)]
    public void Rehydrate_requires_a_terminal_timestamp_for_terminal_states(
        ExecutionAssignmentState state)
    {
        Assert.Throws<ArgumentException>(() => Rehydrate(state, terminalAt: null));
    }

    [Fact]
    public void Rehydrate_rejects_a_terminal_timestamp_for_a_nonterminal_state()
    {
        Assert.Throws<ArgumentException>(() => Rehydrate(
            ExecutionAssignmentState.RecoveryRequired,
            AssignedAt.AddMinutes(1)));
    }

    [Fact]
    public void Rehydrate_rejects_a_terminal_timestamp_before_assignment()
    {
        Assert.Throws<ArgumentException>(() => Rehydrate(
            ExecutionAssignmentState.Completed,
            AssignedAt.AddTicks(-1)));
    }

    private static ExecutionAssignment CreateAssignment() => ExecutionAssignment.Create(
        WorkRequestId.New(),
        ProjectId.New(),
        WorkspaceBindingId.New(),
        NodeId.New(),
        "/srv/work/project",
        "main",
        bindingValidationRevisionSnapshot: 1,
        "token-1",
        AssignedAt,
        Lease);

    private static ExecutionAssignment Rehydrate(
        ExecutionAssignmentState state,
        DateTimeOffset? terminalAt) => ExecutionAssignment.Rehydrate(
            WorkRequestId.New(),
            ProjectId.New(),
            WorkspaceBindingId.New(),
            NodeId.New(),
            "/srv/work/project",
            "main",
            bindingValidationRevisionSnapshot: 1,
            state,
            "token-1",
            AssignedAt,
            AssignedAt + Lease,
            lastRenewedAt: null,
            lastReconciledAt: null,
            terminalAt,
            version: 1);
}
