using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Recovery;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Domain.Reservations;
using PiCommandCenter.Infrastructure.Recovery;
using PiCommandCenter.Infrastructure.Reservations;

namespace PiCommandCenter.Infrastructure.Tests.Recovery;

public sealed class ProjectRecoveryServiceTests
{
    private const string ClaimToken = "assignment-token-0123456789abcdef0123456789abcdef";

    private readonly string _sqlitePath = TestRepositories.CreateSqliteFile();
    private readonly FakeTimeProvider _clock = TestNodes.Clock();

    [Fact]
    public async Task Diagnosis_and_start_are_no_ops_when_inventory_is_empty()
    {
        await using var db = CreateContext();
        var project = SeedProject(db);
        await SaveAsync(db);
        var service = CreateService(db);

        var diagnosis = await service.GetDiagnosisAsync(project.Id);

        Assert.False(diagnosis.HoldPresent);
        Assert.Empty(diagnosis.NonterminalAssignments);
        Assert.Empty(diagnosis.UnresolvedReservations);
        var started = await service.StartAsync(
            project.Id,
            new StartProjectRecoveryCommand(diagnosis.InventoryRevision, "none", "operator", "key-empty"));
        Assert.True(started.NoOp);
        Assert.Null(started.Operation);
        Assert.False((await service.GetDiagnosisAsync(project.Id)).HoldPresent);
        Assert.Empty(await db.Set<RecoveryHoldRow>().ToListAsync());
        Assert.Empty(await db.Set<RecoveryOperationRow>().ToListAsync());
    }

    [Fact]
    public async Task Diagnosis_exposes_retained_owner_facts_without_claim_tokens()
    {
        await using var db = CreateContext();
        var world = SeedRunningAssignment(db);
        world.Node.MarkOffline(_clock.GetUtcNow());
        var leaseId = Guid.NewGuid();
        var expiresAt = _clock.GetUtcNow().AddMinutes(2);
        db.Set<ReservationLeaseRow>().Add(new ReservationLeaseRow
        {
            Id = leaseId,
            ProjectId = world.Project.Id.Value,
            RequestId = world.Request.Id.Value,
            OwnerSessionId = "owner-session",
            Reason = "write lock",
            FencingToken = 1,
            State = nameof(ReservationLeaseState.Active),
            AcquiredAtUtcTicks = _clock.GetUtcNow().UtcTicks,
            LastRenewedAtUtcTicks = _clock.GetUtcNow().UtcTicks,
            ExpiresAtUtcTicks = expiresAt.UtcTicks,
            Version = 3,
        });
        await SaveAsync(db);
        var service = CreateService(db);

        var diagnosis = await service.GetDiagnosisAsync(world.Project.Id);

        var assignment = Assert.Single(diagnosis.NonterminalAssignments);
        Assert.Equal(world.Assignment.RequestId, assignment.RequestId);
        Assert.Equal(world.Node.Id.Value, assignment.AssignedNodeId);
        Assert.Equal(world.Node.DisplayName, assignment.AssignedNodeDisplayName);
        Assert.Equal(world.Assignment.CanonicalRepositoryPathSnapshot, assignment.CanonicalRepositoryPath);
        Assert.Equal(world.Assignment.AssignedAt, assignment.AssignedAt);
        Assert.Equal(world.Assignment.LeaseExpiresAt, assignment.LeaseExpiresAt);
        Assert.Equal(world.Node.LastHeartbeatAt, assignment.NodeLastContact);
        Assert.Equal(nameof(NodeStatus.Offline), assignment.NodeStatus);
        Assert.Null(assignment.LastRenewedAt);
        Assert.DoesNotContain(
            ClaimToken,
            assignment.CanonicalRepositoryPath,
            StringComparison.Ordinal);

        var reservation = Assert.Single(diagnosis.UnresolvedReservations);
        Assert.Equal(leaseId, reservation.LeaseId);
        Assert.Equal(world.Request.Id.Value, reservation.RequestId);
        Assert.Equal("owner-session", reservation.OwnerSessionId);
        Assert.Equal("write lock", reservation.Reason);
        Assert.Equal(expiresAt, reservation.ExpiresAt);

        var compactAssignment = new ProjectRecoveryAssignmentSnapshot(
            assignment.RequestId,
            assignment.Version,
            assignment.State,
            assignment.BindingRevision);
        var compactReservation = new ProjectRecoveryReservationSnapshot(
            reservation.LeaseId,
            reservation.Version,
            reservation.State);
        Assert.Equal(
            ProjectRecoveryInventory.ComputeRevision(
                diagnosis.ProjectVersion,
                [compactAssignment],
                [compactReservation]),
            diagnosis.InventoryRevision);
        Assert.Equal(
            diagnosis.InventoryRevision,
            ProjectRecoveryInventory.ComputeRevision(
                diagnosis.ProjectVersion,
                diagnosis.NonterminalAssignments,
                diagnosis.UnresolvedReservations));
    }


    [Fact]
    public async Task Start_records_cancellation_intent_and_hold_for_nonterminal_assignments()
    {
        await using var db = CreateContext();
        var world = SeedRunningAssignment(db);
        await SaveAsync(db);
        var service = CreateService(db);
        var diagnosis = await service.GetDiagnosisAsync(world.Project.Id);

        var started = await service.StartAsync(
            world.Project.Id,
            new StartProjectRecoveryCommand(diagnosis.InventoryRevision, "stuck", "operator", "key-1"));

        Assert.False(started.NoOp);
        Assert.NotNull(started.Operation);
        Assert.Equal(RecoveryOperationStatus.Running, started.Operation.Status);
        Assert.Equal(1, started.Operation.Attempt);
        Assert.Equal(world.Request.Id, Assert.Single(started.Operation.AssignmentTargets).RequestId);
        var reloaded = await CreateContext().ExecutionAssignments.SingleAsync();
        Assert.Equal(ExecutionAssignmentState.Cancelling, reloaded.State);
        Assert.Equal(WorkRequestStatus.Cancelling, (await CreateContext().WorkRequests.SingleAsync()).Status);
        Assert.True((await service.GetDiagnosisAsync(world.Project.Id)).HoldPresent);
    }

    [Fact]
    public async Task Start_adopts_already_cancelling_targets_without_failing()
    {
        await using var db = CreateContext();
        var world = SeedRunningAssignment(db, ExecutionAssignmentState.Cancelling);
        world.Request.BeginCancelling(_clock.GetUtcNow());
        await SaveAsync(db);
        var service = CreateService(db);
        var diagnosis = await service.GetDiagnosisAsync(world.Project.Id);

        var started = await service.StartAsync(
            world.Project.Id,
            new StartProjectRecoveryCommand(diagnosis.InventoryRevision, "adopt", "operator", "key-adopt"));

        Assert.False(started.NoOp);
        Assert.Equal(
            nameof(ExecutionAssignmentState.Cancelling),
            Assert.Single(started.Operation!.AssignmentTargets).CapturedState);
        Assert.Equal(ExecutionAssignmentState.Cancelling, (await db.ExecutionAssignments.SingleAsync()).State);
    }

    [Fact]
    public async Task Same_idempotency_key_and_input_returns_the_same_operation()
    {
        await using var db = CreateContext();
        var world = SeedRunningAssignment(db);
        await SaveAsync(db);
        var service = CreateService(db);
        var diagnosis = await service.GetDiagnosisAsync(world.Project.Id);
        var command = new StartProjectRecoveryCommand(
            diagnosis.InventoryRevision,
            "stuck",
            "operator",
            "same-key");

        var first = await service.StartAsync(world.Project.Id, command);
        var second = await service.StartAsync(world.Project.Id, command);

        Assert.Equal(first.Operation!.Id, second.Operation!.Id);
        Assert.Equal(1, await db.Set<RecoveryOperationRow>().CountAsync());
    }

    [Fact]
    public async Task Same_key_with_different_input_conflicts()
    {
        await using var db = CreateContext();
        var world = SeedRunningAssignment(db);
        await SaveAsync(db);
        var service = CreateService(db);
        var diagnosis = await service.GetDiagnosisAsync(world.Project.Id);
        await service.StartAsync(
            world.Project.Id,
            new StartProjectRecoveryCommand(diagnosis.InventoryRevision, "first", "operator", "shared"));

        var conflict = await Assert.ThrowsAsync<RecoveryIdempotencyConflictException>(() =>
            service.StartAsync(
                world.Project.Id,
                new StartProjectRecoveryCommand(diagnosis.InventoryRevision, "second", "operator", "shared")));
        Assert.Equal(world.Project.Id, conflict.ProjectId);
    }

    [Fact]
    public async Task Stale_inventory_revision_conflicts()
    {
        await using var db = CreateContext();
        var world = SeedRunningAssignment(db);
        await SaveAsync(db);
        var service = CreateService(db);

        await Assert.ThrowsAsync<RecoveryInventoryConflictException>(() =>
            service.StartAsync(
                world.Project.Id,
                new StartProjectRecoveryCommand("deadbeef", "stuck", "operator", "key-stale")));
    }

    [Fact]
    public async Task Second_unresolved_operation_with_a_different_key_conflicts()
    {
        await using var db = CreateContext();
        var world = SeedRunningAssignment(db);
        await SaveAsync(db);
        var service = CreateService(db);
        var diagnosis = await service.GetDiagnosisAsync(world.Project.Id);
        var first = await service.StartAsync(
            world.Project.Id,
            new StartProjectRecoveryCommand(diagnosis.InventoryRevision, "stuck", "operator", "key-a"));

        var conflict = await Assert.ThrowsAsync<RecoveryOperationConflictException>(() =>
            service.StartAsync(
                world.Project.Id,
                new StartProjectRecoveryCommand(diagnosis.InventoryRevision, "stuck", "operator", "key-b")));
        Assert.Equal(first.Operation!.Id, conflict.ExistingOperationId);
    }

    [Fact]
    public async Task Recheck_from_needs_intervention_increments_attempt_without_a_second_operation()
    {
        await using var db = CreateContext();
        var world = SeedRunningAssignment(db);
        await SaveAsync(db);
        var service = CreateService(db);
        var diagnosis = await service.GetDiagnosisAsync(world.Project.Id);
        var started = await service.StartAsync(
            world.Project.Id,
            new StartProjectRecoveryCommand(diagnosis.InventoryRevision, "stuck", "operator", "key-recheck"));
        var operationId = started.Operation!.Id;
        var operationVersion = started.Operation.Version;
        var row = await db.Set<RecoveryOperationRow>().SingleAsync(o => o.Id == operationId);
        row.Status = nameof(RecoveryOperationStatus.NeedsIntervention);
        await db.SaveChangesAsync();

        var rechecked = await service.RecheckAsync(world.Project.Id, operationId, operationVersion, "recheck-key");

        Assert.Equal(RecoveryOperationStatus.Running, rechecked.Status);
        Assert.Equal(2, rechecked.Attempt);
        Assert.Equal(1, await db.Set<RecoveryOperationRow>().CountAsync());
        var again = await service.RecheckAsync(world.Project.Id, operationId, operationVersion, "recheck-key");
        Assert.Equal(2, again.Attempt);
        await Assert.ThrowsAsync<RecoveryIdempotencyConflictException>(() =>
            service.RecheckAsync(world.Project.Id, operationId, operationVersion + 1, "recheck-key"));
        await Assert.ThrowsAsync<RecoveryRevisionConflictException>(() =>
            service.RecheckAsync(world.Project.Id, operationId, operationVersion, "other-key"));
    }

    [Fact]
    public async Task Resume_is_rejected_while_a_captured_assignment_is_nonterminal()
    {
        await using var db = CreateContext();
        var world = SeedRunningAssignment(db);
        await SaveAsync(db);
        var service = CreateService(db);
        var diagnosis = await service.GetDiagnosisAsync(world.Project.Id);
        var started = await service.StartAsync(
            world.Project.Id,
            new StartProjectRecoveryCommand(diagnosis.InventoryRevision, "stuck", "operator", "key-resume"));
        var operationId = started.Operation!.Id;
        var row = await db.Set<RecoveryOperationRow>().SingleAsync();
        row.Status = nameof(RecoveryOperationStatus.Recovered);
        await db.SaveChangesAsync();
        var holdVersion = (await service.GetDiagnosisAsync(world.Project.Id)).HoldVersion!.Value;

        await Assert.ThrowsAsync<RecoveryNotReadyException>(() =>
            service.ResumeAsync(world.Project.Id, operationId, holdVersion, "operator"));
        await Assert.ThrowsAsync<RecoveryRevisionConflictException>(() =>
            service.ResumeAsync(world.Project.Id, operationId, holdVersion + 9, "operator"));
        Assert.True((await service.GetDiagnosisAsync(world.Project.Id)).HoldPresent);
    }

    private ProjectRecoveryService CreateService(ControlPlaneDbContext db) =>
        new(_clock, db, new ProjectionNotifier());

    private ControlPlaneDbContext CreateContext() => TestRepositories.CreateContext(_sqlitePath);

    private Project SeedProject(ControlPlaneDbContext db) => TestNodes.SeedProject(db, _clock);

    private AssignmentWorld SeedRunningAssignment(
        ControlPlaneDbContext db,
        ExecutionAssignmentState state = ExecutionAssignmentState.Running)
    {
        var node = TestNodes.SeedNode(db, TestNodes.NewNodeId(), _clock);
        var project = SeedProject(db);
        var repositoryPath = Path.Combine(
            Path.GetTempPath(),
            "pi-cc-tests",
            Guid.NewGuid().ToString("N"),
            "repo");
        var binding = WorkspaceBinding.Designate(project.Id, node.Id, repositoryPath, _clock.GetUtcNow());
        Assert.True(binding.ApplyValidationResult(
            node.Id,
            binding.ValidationRevision,
            WorkspaceBindingStatus.Valid,
            WorkspaceBinding.ValidValidationCode,
            "ready",
            repositoryPath,
            _clock.GetUtcNow()));
        db.WorkspaceBindings.Add(binding);
        var request = TestNodes.SeedRequest(db, project, _clock);
        var now = _clock.GetUtcNow();
        request.Start(now);
        DateTimeOffset? terminalAt = state is ExecutionAssignmentState.Completed
            or ExecutionAssignmentState.Failed
            or ExecutionAssignmentState.Cancelled
                ? now
                : null;
        var assignment = ExecutionAssignment.Rehydrate(
            request.Id,
            project.Id,
            binding.Id,
            node.Id,
            binding.CanonicalRepositoryPath!,
            project.DefaultBranch,
            binding.ValidationRevision,
            state,
            ClaimToken,
            now,
            now.AddMinutes(5),
            lastRenewedAt: null,
            lastReconciledAt: null,
            terminalAt,
            version: 1);
        db.ExecutionAssignments.Add(assignment);
        return new AssignmentWorld(node, project, request, assignment);
    }

    private static async Task SaveAsync(ControlPlaneDbContext db)
    {
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private sealed record AssignmentWorld(
        FleetNode Node,
        Project Project,
        WorkRequest Request,
        ExecutionAssignment Assignment);
}
