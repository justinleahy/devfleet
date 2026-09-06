using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Recovery;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Reservations;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Recovery;
using PiCommandCenter.Infrastructure.Reservations;

namespace PiCommandCenter.Infrastructure.Tests.Recovery;

public sealed class RecoveryAttemptCoordinatorTests
{
    private const string ClaimToken = "assignment-token-0123456789abcdef0123456789abcdef";

    private readonly string _sqlitePath = TestRepositories.CreateSqliteFile();
    private readonly FakeTimeProvider _clock = TestNodes.Clock();

    [Fact]
    public async Task Wrong_node_is_rejected_without_mutating_targets()
    {
        await using var db = CreateContext();
        var world = await StartRecoveryAsync(db);
        var terminalizer = new RecordingTerminalizer();
        var coordinator = CreateCoordinator(db, terminalizer);
        var foreignNode = TestNodes.NewNodeId();

        var decision = await coordinator.AcceptProofAsync(foreignNode, ValidProof(world));

        Assert.False(decision.Accepted);
        Assert.Equal([RecoveryReasonCodes.RecoveryTargetChanged], decision.MissingRequirements);
        Assert.Empty(terminalizer.Calls);
        Assert.Equal(nameof(RecoveryOperationStatus.Running), (await OperationAsync(db)).Status);
        Assert.Null((await TargetAsync(db)).Outcome);
        Assert.Single(await db.Set<RecoveryHoldRow>().ToListAsync());
        Assert.DoesNotContain(await db.Set<RecoveryAuditFactRow>().ToListAsync(), f => f.Kind == "proof-rejected");
    }

    [Fact]
    public async Task Wrong_token_is_rejected_without_mutating_targets()
    {
        await using var db = CreateContext();
        var world = await StartRecoveryAsync(db);
        var terminalizer = new RecordingTerminalizer();
        var coordinator = CreateCoordinator(db, terminalizer);

        var decision = await coordinator.AcceptProofAsync(world.Node.Id, ValidProof(world, ClaimToken: "other-token-0123456789abcdef0123456789abcdef"));

        Assert.False(decision.Accepted);
        Assert.Equal([RecoveryReasonCodes.RecoveryTargetChanged], decision.MissingRequirements);
        Assert.Empty(terminalizer.Calls);
        Assert.Null((await TargetAsync(db)).Outcome);
    }

    [Fact]
    public async Task Wrong_attempt_persists_needs_intervention_and_skips_terminalizer()
    {
        await using var db = CreateContext();
        var world = await StartRecoveryAsync(db);
        var terminalizer = new RecordingTerminalizer();
        var coordinator = CreateCoordinator(db, terminalizer);

        var decision = await coordinator.AcceptProofAsync(world.Node.Id, ValidProof(world, Attempt: 2));

        Assert.False(decision.Accepted);
        Assert.Contains(RecoveryReasonCodes.RecoveryEvidenceStale, decision.MissingRequirements);
        Assert.Empty(terminalizer.Calls);
        Assert.Equal(nameof(RecoveryOperationStatus.NeedsIntervention), (await OperationAsync(db)).Status);
        Assert.Null((await TargetAsync(db)).Outcome);
        Assert.Single(await db.Set<RecoveryHoldRow>().ToListAsync());
        Assert.Contains(await db.Set<RecoveryAuditFactRow>().ToListAsync(), f => f.Kind == "proof-rejected");
    }

    [Fact]
    public async Task Wrong_binding_revision_is_rejected_without_terminalizer()
    {
        await using var db = CreateContext();
        var world = await StartRecoveryAsync(db);
        var terminalizer = new RecordingTerminalizer();
        var coordinator = CreateCoordinator(db, terminalizer);

        var decision = await coordinator.AcceptProofAsync(world.Node.Id, ValidProof(world, BindingRevision: 99));

        Assert.False(decision.Accepted);
        Assert.Equal([RecoveryReasonCodes.RecoveryTargetChanged], decision.MissingRequirements);
        Assert.Empty(terminalizer.Calls);
        Assert.Equal(nameof(RecoveryOperationStatus.Running), (await OperationAsync(db)).Status);
    }

    [Fact]
    public async Task Unknown_inventory_persists_needs_intervention_and_retains_hold()
    {
        await using var db = CreateContext();
        var world = await StartRecoveryAsync(db);
        var terminalizer = new RecordingTerminalizer();
        var coordinator = CreateCoordinator(db, terminalizer);
        var proof = ValidProof(world) with
        {
            Processes = new RecoveryKnownCountMessage(null, RecoveryReasonCodes.ProcessStopUnproven),
        };

        var decision = await coordinator.AcceptProofAsync(world.Node.Id, proof);

        Assert.False(decision.Accepted);
        Assert.Contains(RecoveryReasonCodes.ProcessStopUnproven, decision.MissingRequirements);
        Assert.Empty(terminalizer.Calls);
        Assert.Equal(nameof(RecoveryOperationStatus.NeedsIntervention), (await OperationAsync(db)).Status);
        Assert.Null((await TargetAsync(db)).Outcome);
        Assert.Single(await db.Set<RecoveryHoldRow>().ToListAsync());
    }

    [Fact]
    public async Task Known_nonzero_inventory_is_rejected()
    {
        await using var db = CreateContext();
        var world = await StartRecoveryAsync(db);
        var terminalizer = new RecordingTerminalizer();
        var coordinator = CreateCoordinator(db, terminalizer);
        var proof = ValidProof(world) with
        {
            Children = new RecoveryKnownCountMessage(1, null),
        };

        var decision = await coordinator.AcceptProofAsync(world.Node.Id, proof);

        Assert.False(decision.Accepted);
        Assert.Contains(RecoveryReasonCodes.ProcessStopUnproven, decision.MissingRequirements);
        Assert.Empty(terminalizer.Calls);
        Assert.Equal(nameof(RecoveryOperationStatus.NeedsIntervention), (await OperationAsync(db)).Status);
    }

    [Fact]
    public async Task Stale_evidence_past_deadline_needs_intervention()
    {
        await using var db = CreateContext();
        var world = await StartRecoveryAsync(db);
        var operation = await OperationAsync(db);
        operation.DeadlineUtcTicks = _clock.GetUtcNow().AddSeconds(-1).UtcTicks;
        await db.SaveChangesAsync();
        var terminalizer = new RecordingTerminalizer();
        var coordinator = CreateCoordinator(db, terminalizer);

        var decision = await coordinator.AcceptProofAsync(world.Node.Id, ValidProof(world));

        Assert.False(decision.Accepted);
        Assert.Contains(RecoveryReasonCodes.RecoveryEvidenceStale, decision.MissingRequirements);
        Assert.Empty(terminalizer.Calls);
        Assert.Equal(nameof(RecoveryOperationStatus.NeedsIntervention), (await OperationAsync(db)).Status);
    }

    [Fact]
    public async Task Dirty_repository_snapshot_is_accepted()
    {
        await using var db = CreateContext();
        var world = await StartRecoveryAsync(db);
        var terminalizer = new RecordingTerminalizer();
        var coordinator = CreateCoordinator(db, terminalizer);
        var proof = ValidProof(world) with
        {
            Repository = DirtyRepository(),
        };

        var decision = await coordinator.AcceptProofAsync(world.Node.Id, proof);

        Assert.True(decision.Accepted);
        Assert.Empty(decision.MissingRequirements);
        Assert.Single(terminalizer.Calls);
        Assert.Equal(TerminalizationIntent.Cancel, terminalizer.Calls[0].Intent);
        Assert.Equal(nameof(ExecutionAssignmentState.Cancelled), (await TargetAsync(db)).Outcome);
        Assert.Equal(nameof(RecoveryOperationStatus.Recovered), (await OperationAsync(db)).Status);
        Assert.Single(await db.Set<RecoveryHoldRow>().ToListAsync());
    }

    [Fact]
    public async Task Missing_repository_is_rejected()
    {
        await using var db = CreateContext();
        var world = await StartRecoveryAsync(db);
        var terminalizer = new RecordingTerminalizer();
        var coordinator = CreateCoordinator(db, terminalizer);
        var proof = ValidProof(world) with { Repository = null };

        var decision = await coordinator.AcceptProofAsync(world.Node.Id, proof);

        Assert.False(decision.Accepted);
        Assert.Contains(RecoveryReasonCodes.RepositoryStatusUnknown, decision.MissingRequirements);
        Assert.Empty(terminalizer.Calls);
        Assert.Null((await TargetAsync(db)).Outcome);
        Assert.Equal(nameof(RecoveryOperationStatus.NeedsIntervention), (await OperationAsync(db)).Status);
    }

    [Fact]
    public async Task Partial_multi_target_progress_does_not_mark_recovered()
    {
        await using var db = CreateContext();
        var world = SeedRunningAssignment(db);
        var second = SeedSecondAssignment(db, world);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = new ProjectRecoveryService(_clock, db, new ProjectionNotifier());
        var diagnosis = await service.GetDiagnosisAsync(world.Project.Id);
        var started = await service.StartAsync(
            world.Project.Id,
            new StartProjectRecoveryCommand(diagnosis.InventoryRevision, "stuck", "operator", "multi"));
        world = world with
        {
            OperationId = started.Operation!.Id,
            BindingRevision = world.Assignment.BindingValidationRevisionSnapshot,
        };
        var terminalizer = new RecordingTerminalizer();
        var coordinator = CreateCoordinator(db, terminalizer);

        var decision = await coordinator.AcceptProofAsync(world.Node.Id, ValidProof(world));

        Assert.True(decision.Accepted);
        Assert.Single(terminalizer.Calls);
        var operation = await OperationAsync(db);
        Assert.Equal(nameof(RecoveryOperationStatus.Running), operation.Status);
        Assert.Equal(
            nameof(ExecutionAssignmentState.Cancelled),
            operation.AssignmentTargets.Single(t => t.RequestId == world.Request.Id.Value).Outcome);
        Assert.Null(operation.AssignmentTargets.Single(t => t.RequestId == second.Request.Id.Value).Outcome);
        Assert.Single(await db.Set<RecoveryHoldRow>().ToListAsync());
    }

    [Fact]
    public async Task Valid_cancellation_proof_terminalizes_and_keeps_hold()
    {
        await using var db = CreateContext();
        var world = await StartRecoveryAsync(db);
        var leaseId = Guid.NewGuid();
        db.Set<RecoveryReservationTargetRow>().Add(new RecoveryReservationTargetRow
        {
            Id = Guid.NewGuid(),
            OperationId = world.OperationId,
            LeaseId = leaseId,
            CapturedVersion = 1,
            CapturedState = nameof(ReservationLeaseState.Active),
        });
        await db.SaveChangesAsync();
        var terminalizer = new RecordingTerminalizer();
        var coordinator = CreateCoordinator(db, terminalizer);
        var proof = ValidProof(world) with
        {
            ReservationDispositions =
            [
                new RecoveryReservationDispositionMessage(leaseId, "resolved", null),
            ],
        };

        var decision = await coordinator.AcceptProofAsync(world.Node.Id, proof);

        Assert.True(decision.Accepted);
        Assert.Equal(TerminalizationIntent.Cancel, Assert.Single(terminalizer.Calls).Intent);
        Assert.Equal(nameof(RecoveryOperationStatus.Recovered), (await OperationAsync(db)).Status);
        Assert.Equal("Resolved", (await db.Set<RecoveryReservationTargetRow>().SingleAsync()).Outcome);
        Assert.Single(await db.Set<RecoveryHoldRow>().ToListAsync());
    }

    [Fact]
    public async Task Preserved_finalizing_outcome_is_delegated_to_terminalizer()
    {
        await using var db = CreateContext();
        var world = SeedOperation(
            db,
            SeedFinalizingWorld(db),
            nameof(ExecutionAssignmentState.Finalizing));
        await db.SaveChangesAsync();
        var terminalizer = new RecordingTerminalizer();
        var coordinator = CreateCoordinator(db, terminalizer);

        var decision = await coordinator.AcceptProofAsync(world.Node.Id, ValidProof(world));

        Assert.True(decision.Accepted);
        Assert.Equal(TerminalizationIntent.Complete, Assert.Single(terminalizer.Calls).Intent);
        Assert.Equal(nameof(ExecutionAssignmentState.Completed), (await TargetAsync(db)).Outcome);
    }

    [Fact]
    public async Task Duplicate_accepted_proof_is_an_exact_no_op()
    {
        await using var db = CreateContext();
        var world = await StartRecoveryAsync(db);
        var terminalizer = new RecordingTerminalizer();
        var coordinator = CreateCoordinator(db, terminalizer);
        var proof = ValidProof(world);
        var first = await coordinator.AcceptProofAsync(world.Node.Id, proof);
        Assert.True(first.Accepted);
        var version = (await OperationAsync(db)).Version;
        var updated = (await OperationAsync(db)).UpdatedAtUtcTicks;
        terminalizer.Calls.Clear();

        var second = await coordinator.AcceptProofAsync(world.Node.Id, proof);

        Assert.True(second.Accepted);
        Assert.Empty(second.MissingRequirements);
        Assert.Empty(terminalizer.Calls);
        Assert.Equal(version, (await OperationAsync(db)).Version);
        Assert.Equal(updated, (await OperationAsync(db)).UpdatedAtUtcTicks);
        Assert.Single(await db.Set<RecoveryHoldRow>().ToListAsync());
    }

    [Fact]
    public async Task Progress_cannot_regress_a_recovered_operation()
    {
        await using var db = CreateContext();
        var world = await StartRecoveryAsync(db);
        var coordinator = CreateCoordinator(db, new RecordingTerminalizer());
        Assert.True((await coordinator.AcceptProofAsync(world.Node.Id, ValidProof(world))).Accepted);
        var version = (await OperationAsync(db)).Version;

        await coordinator.AcceptProgressAsync(world.Node.Id, new AssignmentRecoveryProgressMessage(
            world.OperationId,
            1,
            world.Project.Id.Value,
            world.Request.Id.Value,
            ClaimToken,
            world.BindingRevision,
            _clock.GetUtcNow(),
            "Stopping agents",
            Zero(),
            Zero(),
            Zero(),
            Zero(),
            Zero(),
            []));

        Assert.Equal(nameof(RecoveryOperationStatus.Recovered), (await OperationAsync(db)).Status);
        Assert.Equal(version, (await OperationAsync(db)).Version);
    }

    [Fact]
    public async Task Rejected_proof_does_not_resolve_reservations()
    {
        await using var db = CreateContext();
        var world = await StartRecoveryAsync(db);
        var leaseId = Guid.NewGuid();
        db.Set<RecoveryReservationTargetRow>().Add(new RecoveryReservationTargetRow
        {
            Id = Guid.NewGuid(),
            OperationId = world.OperationId,
            LeaseId = leaseId,
            CapturedVersion = 1,
            CapturedState = nameof(ReservationLeaseState.Active),
        });
        await db.SaveChangesAsync();
        var terminalizer = new RecordingTerminalizer();
        var coordinator = CreateCoordinator(db, terminalizer);
        var proof = ValidProof(world) with
        {
            Repository = null,
            ReservationDispositions =
            [
                new RecoveryReservationDispositionMessage(leaseId, "resolved", null),
            ],
        };

        var decision = await coordinator.AcceptProofAsync(world.Node.Id, proof);

        Assert.False(decision.Accepted);
        Assert.Empty(terminalizer.Calls);
        Assert.Null((await db.Set<RecoveryReservationTargetRow>().SingleAsync()).Outcome);
        Assert.Single(await db.Set<RecoveryHoldRow>().ToListAsync());
    }

    private RecoveryAttemptCoordinator CreateCoordinator(
        ControlPlaneDbContext db,
        IRecoveryTargetTerminalizer terminalizer) =>
        new(_clock, db, terminalizer, new ProjectionNotifier());

    private ControlPlaneDbContext CreateContext() => TestRepositories.CreateContext(_sqlitePath);

    private async Task<World> StartRecoveryAsync(ControlPlaneDbContext db)
    {
        var world = SeedRunningAssignment(db);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = new ProjectRecoveryService(_clock, db, new ProjectionNotifier());
        var diagnosis = await service.GetDiagnosisAsync(world.Project.Id);
        var started = await service.StartAsync(
            world.Project.Id,
            new StartProjectRecoveryCommand(diagnosis.InventoryRevision, "stuck", "operator", "start"));
        return world with
        {
            OperationId = started.Operation!.Id,
            BindingRevision = world.Assignment.BindingValidationRevisionSnapshot,
        };
    }

    private World SeedRunningAssignment(
        ControlPlaneDbContext db,
        ExecutionAssignmentState state = ExecutionAssignmentState.Running)
    {
        var node = TestNodes.SeedNode(db, TestNodes.NewNodeId(), _clock);
        var project = TestNodes.SeedProject(db, _clock);
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
            terminalAt: null,
            version: 1);
        db.ExecutionAssignments.Add(assignment);
        return new World(node, project, request, assignment, Guid.Empty, binding.ValidationRevision);
    }

    private World SeedFinalizingWorld(ControlPlaneDbContext db)
    {
        var world = SeedRunningAssignment(db, ExecutionAssignmentState.Finalizing);
        world.Request.BeginPlanning(_clock.GetUtcNow());
        world.Request.BeginExecuting(_clock.GetUtcNow());
        world.Request.BeginReviewing(_clock.GetUtcNow());
        world.Request.BeginVerifying(_clock.GetUtcNow());
        return world;
    }

    private World SeedOperation(ControlPlaneDbContext db, World world, string capturedState)
    {
        var now = _clock.GetUtcNow();
        var operationId = Guid.NewGuid();
        db.Set<RecoveryOperationRow>().Add(new RecoveryOperationRow
        {
            Id = operationId,
            ProjectId = world.Project.Id.Value,
            Status = nameof(RecoveryOperationStatus.Running),
            Attempt = 1,
            InventoryRevision = "rev",
            Reason = "stuck",
            Actor = "operator",
            Stage = "Stopping agents",
            CreatedAtUtcTicks = now.UtcTicks,
            UpdatedAtUtcTicks = now.UtcTicks,
            LastProgressUtcTicks = now.UtcTicks,
            DeadlineUtcTicks = now.AddSeconds(60).UtcTicks,
            Version = 1,
        });
        db.Set<RecoveryTargetRow>().Add(new RecoveryTargetRow
        {
            Id = Guid.NewGuid(),
            OperationId = operationId,
            RequestId = world.Request.Id.Value,
            CapturedVersion = 1,
            CapturedState = capturedState,
            BindingRevision = world.Assignment.BindingValidationRevisionSnapshot,
        });
        db.Set<RecoveryHoldRow>().Add(new RecoveryHoldRow
        {
            ProjectId = world.Project.Id.Value,
            OperationId = operationId,
            EstablishedAtUtcTicks = now.UtcTicks,
            Version = 1,
        });
        return world with
        {
            OperationId = operationId,
            BindingRevision = world.Assignment.BindingValidationRevisionSnapshot,
        };
    }

    private SecondAssignment SeedSecondAssignment(ControlPlaneDbContext db, World world)
    {
        var request = TestNodes.SeedRequest(db, world.Project, _clock, title: "second");
        var now = _clock.GetUtcNow();
        request.Start(now);
        var assignment = ExecutionAssignment.Rehydrate(
            request.Id,
            world.Project.Id,
            world.Assignment.WorkspaceBindingId,
            world.Node.Id,
            world.Assignment.CanonicalRepositoryPathSnapshot,
            world.Project.DefaultBranch,
            world.Assignment.BindingValidationRevisionSnapshot,
            ExecutionAssignmentState.Running,
            "second-token-0123456789abcdef0123456789abcdefab",
            now,
            now.AddMinutes(5),
            lastRenewedAt: null,
            lastReconciledAt: null,
            terminalAt: null,
            version: 1);
        db.ExecutionAssignments.Add(assignment);
        return new SecondAssignment(request, assignment);
    }

    private SecondAssignment SeedForeignAssignment(ControlPlaneDbContext db)
    {
        var node = TestNodes.SeedNode(db, TestNodes.NewNodeId(), _clock);
        var project = TestNodes.SeedProject(db, _clock, displayName: "other");
        var repositoryPath = Path.Combine(Path.GetTempPath(), "pi-cc-tests", Guid.NewGuid().ToString("N"), "repo");
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
        var request = TestNodes.SeedRequest(db, project, _clock, title: "other-node");
        var now = _clock.GetUtcNow();
        request.Start(now);
        var assignment = ExecutionAssignment.Rehydrate(
            request.Id,
            project.Id,
            binding.Id,
            node.Id,
            binding.CanonicalRepositoryPath!,
            project.DefaultBranch,
            binding.ValidationRevision,
            ExecutionAssignmentState.Running,
            "other-node-token-0123456789abcdef0123456789abcd",
            now,
            now.AddMinutes(5),
            lastRenewedAt: null,
            lastReconciledAt: null,
            terminalAt: null,
            version: 1);
        db.ExecutionAssignments.Add(assignment);
        return new SecondAssignment(request, assignment);
    }

    private AssignmentRecoveryProofMessage ValidProof(
        World world,
        string? ClaimToken = null,
        int Attempt = 1,
        long? BindingRevision = null) =>
        new(
            world.OperationId,
            Attempt,
            world.Project.Id.Value,
            world.Request.Id.Value,
            ClaimToken ?? RecoveryAttemptCoordinatorTests.ClaimToken,
            BindingRevision ?? world.BindingRevision,
            _clock.GetUtcNow(),
            AdmissionClosed: true,
            Zero(),
            Zero(),
            Zero(),
            Zero(),
            Zero(),
            EventAcknowledgementPosition: 0,
            EventAcknowledgementUnknownReasonCode: null,
            ProcessIdentities: [],
            ReservationDispositions: [],
            Repository: CleanRepository());

    private RecoveryRepositoryStatusMessage CleanRepository() =>
        new(
            true,
            "abc",
            "main",
            "clean",
            "clean",
            Zero(),
            [],
            _clock.GetUtcNow());

    private RecoveryRepositoryStatusMessage DirtyRepository() =>
        new(
            true,
            "abc",
            "main",
            "dirty",
            "modified",
            new RecoveryKnownCountMessage(3, null),
            ["git-write"],
            _clock.GetUtcNow());

    private static RecoveryKnownCountMessage Zero() => new(0, null);

    private static Task<RecoveryOperationRow> OperationAsync(ControlPlaneDbContext db) =>
        db.Set<RecoveryOperationRow>().Include(o => o.AssignmentTargets).Include(o => o.ReservationTargets).SingleAsync();

    private static Task<RecoveryTargetRow> TargetAsync(ControlPlaneDbContext db) =>
        db.Set<RecoveryTargetRow>().SingleAsync();

    private sealed class RecordingTerminalizer : IRecoveryTargetTerminalizer
    {
        public List<(AssignmentRecoveryProofMessage Proof, TerminalizationIntent Intent)> Calls { get; } = [];

        public Task<CompletionGateDecision> TerminalizeAsync(
            AssignmentRecoveryProofMessage proof,
            TerminalizationIntent acceptedIntent,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((proof, acceptedIntent));
            return Task.FromResult(new CompletionGateDecision(true, [], null));
        }
    }

    private sealed record World(
        FleetNode Node,
        Project Project,
        WorkRequest Request,
        ExecutionAssignment Assignment,
        Guid OperationId,
        long BindingRevision);

    private sealed record SecondAssignment(WorkRequest Request, ExecutionAssignment Assignment);
}
