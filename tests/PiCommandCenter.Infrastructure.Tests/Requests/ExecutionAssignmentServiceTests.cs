using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Requests;

namespace PiCommandCenter.Infrastructure.Tests.Requests;

public sealed class ExecutionAssignmentServiceTests : IDisposable
{
    private const string ClaimToken = "assignment-token";

    private readonly string _sqlitePath = TestRepositories.CreateSqliteFile();
    private readonly FakeTimeProvider _clock = TestNodes.Clock();

    [Fact]
    public async Task Claim_selects_the_highest_priority_eligible_request_and_persists_its_snapshot()
    {
        await using var db = CreateContext();
        var node = SeedOnlineReadyNode(db);
        var project = SeedProject(db);
        var binding = SeedValidBinding(db, project, node.Id);
        var low = SeedRequest(db, project, RequestPriority.Low);
        var high = SeedRequest(db, project, RequestPriority.High);
        await SaveAsync(db);
        var decision = await CreateEvaluator(db).EvaluateAsync(high.Id, node.Id);
        Assert.True(decision.Status.IsEligible, $"{decision.Status.Code}: {decision.Status.Detail}");
        var service = CreateService(db);

        var result = await service.ClaimNextAsync(node.Id, TimeSpan.FromMinutes(5));

        Assert.NotNull(result);
        Assert.Equal(high.Id, result.RequestId);
        Assert.Equal(binding.Id, result.WorkspaceBindingId);
        Assert.Equal(binding.CanonicalRepositoryPath, result.CanonicalRepositoryPathSnapshot);
        Assert.Equal(binding.ValidationRevision, result.BindingValidationRevisionSnapshot);
        Assert.Equal(64, result.ClaimToken.Length);
        Assert.NotEqual(ClaimToken, result.ClaimToken);
        await using var reload = CreateContext();
        Assert.Equal(WorkRequestStatus.Starting, (await reload.WorkRequests.SingleAsync(
            request => request.Id == high.Id)).Status);
        Assert.Equal(WorkRequestStatus.Queued, (await reload.WorkRequests.SingleAsync(
            request => request.Id == low.Id)).Status);
        Assert.Equal(high.Id, (await reload.ExecutionAssignments.SingleAsync()).RequestId);
    }

    [Fact]
    public async Task Competing_claims_create_exactly_one_assignment()
    {
        await using (var seed = CreateContext())
        {
            var node = SeedOnlineReadyNode(seed);
            var project = SeedProject(seed);
            SeedValidBinding(seed, project, node.Id);
            SeedRequest(seed, project);
            await SaveAsync(seed);
        }

        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();
        var nodeId = await firstDb.FleetNodes.Select(node => node.Id).SingleAsync();
        var claims = await Task.WhenAll(
            CreateService(firstDb).ClaimNextAsync(nodeId, TimeSpan.FromMinutes(5)),
            CreateService(secondDb).ClaimNextAsync(nodeId, TimeSpan.FromMinutes(5)));

        Assert.Single(claims, assignment => assignment is not null);
        await using var reload = CreateContext();
        Assert.Single(await reload.ExecutionAssignments.ToListAsync());
        Assert.Equal(WorkRequestStatus.Starting, (await reload.WorkRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task Cancelling_queued_work_prevents_it_from_being_claimed()
    {
        await using var db = CreateContext();
        var node = SeedOnlineReadyNode(db);
        var project = SeedProject(db);
        SeedValidBinding(db, project, node.Id);
        var request = SeedRequest(db, project);
        await SaveAsync(db);

        var cancelled = await CreateCancellationService(db).CancelAsync(
            request.Id,
            new CancelWorkRequestCommand(Reason: null));
        var claimed = await CreateService(db).ClaimNextAsync(node.Id, TimeSpan.FromMinutes(5));

        Assert.Equal(WorkRequestStatus.Cancelled, cancelled.RequestStatus);
        Assert.Null(cancelled.AssignmentState);
        Assert.Null(claimed);
        Assert.False(await db.ExecutionAssignments.AnyAsync());
    }

    [Fact]
    public async Task Assigned_cancellation_persists_both_nonterminal_states_before_notification()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Running);
        await SaveAsync(db);
        var service = CreateCancellationService(db);

        var first = await service.CancelAsync(
            world.Request.Id,
            new CancelWorkRequestCommand("operator stop"));
        var requestVersion = (await db.WorkRequests.SingleAsync()).Version;
        var assignmentVersion = (await db.ExecutionAssignments.SingleAsync()).Version;
        var retry = await service.CancelAsync(
            world.Request.Id,
            new CancelWorkRequestCommand("operator stop"));

        Assert.Equal(WorkRequestStatus.Cancelling, first.RequestStatus);
        Assert.Equal(ExecutionAssignmentState.Cancelling, first.AssignmentState);
        Assert.Equal(world.Node.Id, first.AssignedNodeId);
        Assert.Equal(first.RequestStatus, retry.RequestStatus);
        Assert.Equal(first.AssignmentState, retry.AssignmentState);
        Assert.Equal(requestVersion, (await db.WorkRequests.SingleAsync()).Version);
        Assert.Equal(assignmentVersion, (await db.ExecutionAssignments.SingleAsync()).Version);
        Assert.Null((await db.ExecutionAssignments.SingleAsync()).TerminalAt);
    }

    [Fact]
    public async Task Claim_returns_null_when_the_current_evaluator_rejects_the_request()
    {
        await using var db = CreateContext();
        var node = SeedOnlineReadyNode(db);
        var project = SeedProject(db);
        SeedRequest(db, project);
        await SaveAsync(db);

        var result = await CreateService(db).ClaimNextAsync(node.Id, TimeSpan.FromMinutes(5));

        Assert.Null(result);
        Assert.False(await db.ExecutionAssignments.AnyAsync());
        Assert.Equal(WorkRequestStatus.Queued, (await db.WorkRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task Claim_rejects_an_empty_node_id()
    {
        await using var db = CreateContext();
        var service = CreateService(db);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ClaimNextAsync(default, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Claim_rejects_a_non_positive_lease()
    {
        await using var db = CreateContext();
        var service = CreateService(db);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.ClaimNextAsync(NodeId.New(), TimeSpan.Zero));
    }

    [Fact]
    public async Task Renew_extends_an_active_assignment_owned_by_the_node_and_token()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Starting);
        await SaveAsync(db);
        var service = CreateService(db);
        _clock.Advance(TimeSpan.FromMinutes(1));

        var renewedUntil = await service.RenewAsync(
            world.Request.Id,
            world.Node.Id,
            ClaimToken,
            TimeSpan.FromMinutes(5));

        Assert.Equal(_clock.GetUtcNow().AddMinutes(5), renewedUntil);
        await using var reload = CreateContext();
        var assignment = await reload.ExecutionAssignments.SingleAsync();
        Assert.Equal(renewedUntil, assignment.LeaseExpiresAt);
        Assert.Equal(_clock.GetUtcNow(), assignment.LastRenewedAt);
        Assert.Equal(2, assignment.Version);
    }

    [Fact]
    public async Task Renew_rejects_when_no_assignment_exists()
    {
        await using var db = CreateContext();
        var service = CreateService(db);

        await Assert.ThrowsAsync<ClaimRenewalRejectedException>(() => service.RenewAsync(
            WorkRequestId.New(),
            NodeId.New(),
            ClaimToken,
            TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Renew_rejects_an_expired_assignment_without_mutating_it()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Starting);
        await SaveAsync(db);
        var service = CreateService(db);
        var originalExpiry = world.Assignment.LeaseExpiresAt;
        _clock.Advance(TimeSpan.FromMinutes(6));

        await Assert.ThrowsAsync<ClaimRenewalRejectedException>(() => service.RenewAsync(
            world.Request.Id,
            world.Node.Id,
            ClaimToken,
            TimeSpan.FromMinutes(5)));

        await using var reload = CreateContext();
        var assignment = await reload.ExecutionAssignments.SingleAsync();
        Assert.Equal(originalExpiry, assignment.LeaseExpiresAt);
        Assert.Null(assignment.LastRenewedAt);
        Assert.Equal(1, assignment.Version);
    }

    [Fact]
    public async Task Renew_rejects_a_wrong_node_or_token_without_mutating_the_assignment()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Starting);
        await SaveAsync(db);
        var service = CreateService(db);
        var originalExpiry = world.Assignment.LeaseExpiresAt;

        await Assert.ThrowsAsync<ClaimRenewalRejectedException>(() => service.RenewAsync(
            world.Request.Id,
            NodeId.New(),
            ClaimToken,
            TimeSpan.FromMinutes(5)));
        await Assert.ThrowsAsync<ClaimRenewalRejectedException>(() => service.RenewAsync(
            world.Request.Id,
            world.Node.Id,
            "wrong-token",
            TimeSpan.FromMinutes(5)));

        await using var reload = CreateContext();
        var assignment = await reload.ExecutionAssignments.SingleAsync();
        Assert.Equal(originalExpiry, assignment.LeaseExpiresAt);
        Assert.Null(assignment.LastRenewedAt);
        Assert.Equal(1, assignment.Version);
    }

    [Fact]
    public async Task Migrated_recovery_assignment_is_not_renewed_or_requeued()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.RecoveryRequired);
        await SaveAsync(db);
        var service = CreateService(db);
        var originalExpiry = world.Assignment.LeaseExpiresAt;

        await Assert.ThrowsAsync<ClaimRenewalRejectedException>(() => service.RenewAsync(
            world.Request.Id,
            world.Node.Id,
            ClaimToken,
            TimeSpan.FromMinutes(5)));

        await using var reload = CreateContext();
        var assignment = await reload.ExecutionAssignments.SingleAsync();
        Assert.Equal(ExecutionAssignmentState.RecoveryRequired, assignment.State);
        Assert.Equal(originalExpiry, assignment.LeaseExpiresAt);
        Assert.Null(assignment.LastRenewedAt);
        Assert.Equal(1, assignment.Version);
        Assert.Equal(WorkRequestStatus.Starting, (await reload.WorkRequests.SingleAsync()).Status);
    }

    [Theory]
    [InlineData(ExecutionAssignmentState.Completed)]
    [InlineData(ExecutionAssignmentState.Failed)]
    [InlineData(ExecutionAssignmentState.Cancelled)]
    public async Task Renew_rejects_terminal_assignments(ExecutionAssignmentState state)
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, state);
        await SaveAsync(db);
        var service = CreateService(db);

        await Assert.ThrowsAsync<ClaimRenewalRejectedException>(() => service.RenewAsync(
            world.Request.Id,
            world.Node.Id,
            ClaimToken,
            TimeSpan.FromMinutes(5)));

        await using var reload = CreateContext();
        var assignment = await reload.ExecutionAssignments.SingleAsync();
        Assert.Equal(state, assignment.State);
        Assert.Null(assignment.LastRenewedAt);
        Assert.Equal(1, assignment.Version);
    }

    [Fact]
    public async Task Reconcile_acknowledges_a_running_supervisor_for_a_nonexpired_starting_assignment()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Starting);
        await SaveAsync(db);
        _clock.Advance(TimeSpan.FromMinutes(1));

        var result = Assert.Single(await CreateService(db).ReconcileAsync(
            world.Node.Id,
            [Inventory(world)],
            TimeSpan.FromMinutes(5)));

        Assert.Equal(AssignmentReconciliationDisposition.Resume, result.Disposition);
        Assert.NotNull(result.Assignment);
        Assert.Equal(ExecutionAssignmentState.Running, result.Assignment.State);
        await using var reload = CreateContext();
        var persisted = await reload.ExecutionAssignments.SingleAsync();
        Assert.Equal(ExecutionAssignmentState.Running, persisted.State);
        Assert.Equal(_clock.GetUtcNow(), persisted.LastRenewedAt);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(5), persisted.LeaseExpiresAt);
        Assert.Equal(3, persisted.Version);
    }
    [Fact]
    public async Task Reconcile_preserves_a_start_blocked_assignment_for_node_retry()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Starting);
        await SaveAsync(db);
        _clock.Advance(TimeSpan.FromMinutes(1));

        var inventory = Inventory(world) with
        {
            SupervisorState = AssignmentSupervisorState.StartBlocked,
            RepositoryKnown = false,
        };
        var result = Assert.Single(await CreateService(db).ReconcileAsync(
            world.Node.Id,
            [inventory],
            TimeSpan.FromMinutes(5)));

        Assert.Equal(AssignmentReconciliationDisposition.Resume, result.Disposition);
        Assert.Equal(ExecutionAssignmentState.Starting, result.Assignment?.State);
        await using var reload = CreateContext();
        var persisted = await reload.ExecutionAssignments.SingleAsync();
        Assert.Equal(ExecutionAssignmentState.Starting, persisted.State);
        Assert.Equal(_clock.GetUtcNow(), persisted.LastRenewedAt);
    }


    [Theory]
    [InlineData(ExecutionAssignmentState.Starting)]
    [InlineData(ExecutionAssignmentState.Running)]
    public async Task Reconcile_resumes_exact_expired_running_inventory_without_changing_ownership(
        ExecutionAssignmentState persistedState)
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, persistedState);
        await SaveAsync(db);
        _clock.Advance(TimeSpan.FromMinutes(6));

        var result = Assert.Single(await CreateService(db).ReconcileAsync(
            world.Node.Id,
            [Inventory(world)],
            TimeSpan.FromMinutes(5)));

        Assert.Equal(AssignmentReconciliationDisposition.Resume, result.Disposition);
        Assert.NotNull(result.Assignment);
        Assert.Equal(ClaimToken, result.Assignment.ClaimToken);
        Assert.Equal(ExecutionAssignmentState.Running, result.Assignment.State);
        await using var reload = CreateContext();
        var persisted = await reload.ExecutionAssignments.SingleAsync();
        Assert.Equal(ExecutionAssignmentState.Running, persisted.State);
        Assert.Equal(ClaimToken, persisted.ClaimToken);
        Assert.Equal(_clock.GetUtcNow(), persisted.LastReconciledAt);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(5), persisted.LeaseExpiresAt);
    }

    [Fact]
    public async Task Reconcile_preserves_exact_expired_finalizing_inventory()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Finalizing);
        await SaveAsync(db);
        _clock.Advance(TimeSpan.FromMinutes(6));

        var result = Assert.Single(await CreateService(db).ReconcileAsync(
            world.Node.Id,
            [Inventory(world)],
            TimeSpan.FromMinutes(5)));

        Assert.Equal(AssignmentReconciliationDisposition.Resume, result.Disposition);
        Assert.Equal(ExecutionAssignmentState.Finalizing, result.Assignment?.State);
        var persisted = await db.ExecutionAssignments.SingleAsync();
        Assert.Equal(ExecutionAssignmentState.Finalizing, persisted.State);
        Assert.Equal(_clock.GetUtcNow(), persisted.LastReconciledAt);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(5), persisted.LeaseExpiresAt);
    }

    [Fact]
    public async Task Reconcile_directs_the_owner_to_cancel_instead_of_resuming_offline_work()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Running);
        await SaveAsync(db);
        var reportedBeforeCancellation = Inventory(world);
        await CreateCancellationService(db).CancelAsync(
            world.Request.Id,
            new CancelWorkRequestCommand("operator stop"));

        var result = Assert.Single(await CreateService(db).ReconcileAsync(
            world.Node.Id,
            [reportedBeforeCancellation],
            TimeSpan.FromMinutes(5)));

        Assert.Equal(AssignmentReconciliationDisposition.Cancel, result.Disposition);
        Assert.Equal(ExecutionAssignmentState.Cancelling, result.Assignment?.State);
        Assert.Equal(
            ExecutionAssignmentState.Cancelling,
            (await db.ExecutionAssignments.SingleAsync()).State);
        Assert.Equal(
            WorkRequestStatus.Cancelling,
            (await db.WorkRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task Reconcile_marks_unknown_supervisor_evidence_recovery_required()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Running);
        await SaveAsync(db);
        var inventory = Inventory(world) with
        {
            SupervisorState = AssignmentSupervisorState.Unknown,
        };

        var result = Assert.Single(await CreateService(db).ReconcileAsync(
            world.Node.Id,
            [inventory],
            TimeSpan.FromMinutes(5)));

        Assert.Equal(AssignmentReconciliationDisposition.RecoveryRequired, result.Disposition);
        Assert.Equal(
            ExecutionAssignmentState.RecoveryRequired,
            (await db.ExecutionAssignments.SingleAsync()).State);
    }

    [Fact]
    public async Task Reconcile_marks_mismatched_token_recovery_required()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Running);
        await SaveAsync(db);
        var inventory = Inventory(world) with { ClaimToken = "wrong-token" };

        var result = Assert.Single(await CreateService(db).ReconcileAsync(
            world.Node.Id,
            [inventory],
            TimeSpan.FromMinutes(5)));

        Assert.Equal(AssignmentReconciliationDisposition.RecoveryRequired, result.Disposition);
        var persisted = await db.ExecutionAssignments.SingleAsync();
        Assert.Equal(ExecutionAssignmentState.RecoveryRequired, persisted.State);
        Assert.Equal(ClaimToken, persisted.ClaimToken);
    }

    [Fact]
    public async Task Reconcile_marks_control_plane_assignment_absent_from_inventory_recovery_required()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Running);
        await SaveAsync(db);

        var result = Assert.Single(await CreateService(db).ReconcileAsync(
            world.Node.Id,
            [],
            TimeSpan.FromMinutes(5)));

        Assert.Equal(AssignmentReconciliationDisposition.RecoveryRequired, result.Disposition);
        Assert.Equal(
            ExecutionAssignmentState.RecoveryRequired,
            (await db.ExecutionAssignments.SingleAsync()).State);
    }

    [Fact]
    public async Task Reconcile_returns_terminal_without_reopening_the_assignment()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Completed);
        await SaveAsync(db);

        var result = Assert.Single(await CreateService(db).ReconcileAsync(
            world.Node.Id,
            [Inventory(world) with { SupervisorState = AssignmentSupervisorState.Stopped }],
            TimeSpan.FromMinutes(5)));

        Assert.Equal(AssignmentReconciliationDisposition.Terminal, result.Disposition);
        Assert.Equal(ExecutionAssignmentState.Completed, result.Assignment?.State);
        var persisted = await db.ExecutionAssignments.SingleAsync();
        Assert.Equal(ExecutionAssignmentState.Completed, persisted.State);
        Assert.Equal(1, persisted.Version);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_sqlitePath)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ControlPlaneDbContext CreateContext() => TestRepositories.CreateContext(_sqlitePath);

    private ExecutionAssignmentService CreateService(ControlPlaneDbContext db) => new(
        _clock,
        db,
        CreateEvaluator(db),
        new RecordingProjectionNotifier());

    private RequestCancellationService CreateCancellationService(ControlPlaneDbContext db) => new(
        _clock,
        db,
        new RecordingProjectionNotifier());

    private RequestEligibilityEvaluator CreateEvaluator(ControlPlaneDbContext db) => new(
        _clock,
        Options.Create(new NodeLivenessOptions { HeartbeatSeconds = 10 }),
        db);

    private static ExecutionAssignmentInventoryDto Inventory(AssignmentWorld world) => new(
        world.Assignment.RequestId,
        world.Assignment.ProjectId,
        world.Assignment.WorkspaceBindingId,
        world.Assignment.NodeIdSnapshot,
        world.Assignment.CanonicalRepositoryPathSnapshot,
        world.Assignment.DefaultBranchSnapshot,
        world.Assignment.BindingValidationRevisionSnapshot,
        world.Assignment.State,
        world.Assignment.ClaimToken,
        world.Assignment.AssignedAt,
        AssignmentSupervisorState.Running,
        RepositoryKnown: true,
        PendingEventCount: 0);

    private sealed class RecordingProjectionNotifier : IProjectionNotifier
    {
        public void Publish(ProjectionChange change)
        {
        }

        public IDisposable Subscribe(Action<ProjectionChange> handler) =>
            EmptyDisposable.Instance;

        private sealed class EmptyDisposable : IDisposable
        {
            public static readonly EmptyDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private FleetNode SeedOnlineReadyNode(ControlPlaneDbContext db)
    {
        var now = _clock.GetUtcNow();
        var node = FleetNode.Register(NodeId.New(), "ready-node", "1.0.0", "{}", now);
        var executionStatus = new NodeExecutionStatusDto(
            now,
            AvailableRequestSlots: 1,
            ActiveAssignmentIds: [],
            RoutingRevision: "routing-v1",
            Routes:
            [
                new RuntimeRouteReadinessDto(
                    "implementer",
                    "codex/default",
                    "ready",
                    "runtime-adapter",
                    now,
                    "routing-v1"),
            ]);
        node.Heartbeat(
            "1.0.0",
            "{}",
            now,
            executionStatusJson: JsonSerializer.Serialize(executionStatus, TestRepositories.WebJson));
        db.FleetNodes.Add(node);
        return node;
    }

    private Project SeedProject(ControlPlaneDbContext db)
    {
        var project = Project.Register(
            "Fleet project",
            "main",
            enabled: true,
            maxActiveWriteRequests: 1,
            maxReadOnlyRequests: 4,
            maxChildAgentsPerRequest: 1,
            requireCleanStart: false,
            createRequestBranch: false,
            createRequestCommit: false,
            autoMerge: false,
            _clock.GetUtcNow());
        db.Projects.Add(project);
        return project;
    }

    private WorkspaceBinding SeedValidBinding(
        ControlPlaneDbContext db,
        Project project,
        NodeId nodeId)
    {
        var repositoryPath = Path.Combine(
            Path.GetTempPath(),
            "pi-cc-tests",
            Guid.NewGuid().ToString("N"),
            "repo");
        var binding = WorkspaceBinding.Designate(
            project.Id,
            nodeId,
            repositoryPath,
            _clock.GetUtcNow());
        Assert.True(binding.ApplyValidationResult(
            nodeId,
            binding.ValidationRevision,
            WorkspaceBindingStatus.Valid,
            WorkspaceBinding.ValidValidationCode,
            "Workspace and runtime are ready.",
            repositoryPath,
            _clock.GetUtcNow()));
        db.WorkspaceBindings.Add(binding);
        return binding;
    }

    private WorkRequest SeedRequest(
        ControlPlaneDbContext db,
        Project project,
        RequestPriority priority = RequestPriority.Normal)
    {
        var request = WorkRequest.Enqueue(
            project.Id,
            WorkRequestKind.Development,
            priority,
            RiskLevel.Standard,
            "Queued work",
            "Do the thing",
            _clock.GetUtcNow());
        db.WorkRequests.Add(request);
        return request;
    }

    private AssignmentWorld SeedAssignment(
        ControlPlaneDbContext db,
        ExecutionAssignmentState state)
    {
        var node = SeedOnlineReadyNode(db);
        var project = SeedProject(db);
        var binding = SeedValidBinding(db, project, node.Id);
        var request = SeedRequest(db, project);
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
        return new AssignmentWorld(node, request, assignment);
    }

    private static async Task SaveAsync(ControlPlaneDbContext db)
    {
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private sealed record AssignmentWorld(
        FleetNode Node,
        WorkRequest Request,
        ExecutionAssignment Assignment);
}
