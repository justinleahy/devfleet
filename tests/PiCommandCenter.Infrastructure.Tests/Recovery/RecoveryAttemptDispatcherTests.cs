using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Recovery;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Recovery;

namespace PiCommandCenter.Infrastructure.Tests.Recovery;

public sealed class RecoveryAttemptDispatcherTests
{
    private const string ClaimToken = "assignment-token-0123456789abcdef0123456789abcdef";

    private readonly string _sqlitePath = TestRepositories.CreateSqliteFile();
    private readonly FakeTimeProvider _clock = TestNodes.Clock();

    [Fact]
    public async Task Online_dispatch_sends_correlated_command_payload()
    {
        await using var db = CreateContext();
        var world = await StartRecoveryAsync(db);
        var gateway = new RecordingGateway { Online = { world.Node.Id.Value } };
        var dispatcher = CreateDispatcher(db, gateway);

        await dispatcher.DispatchAsync(world.Project.Id, world.OperationId);

        var sent = Assert.Single(gateway.Sent);
        Assert.Equal(world.Node.Id, sent.NodeId);
        Assert.Equal(world.OperationId, sent.Command.RecoveryId);
        Assert.Equal(1, sent.Command.Attempt);
        Assert.Equal(world.Project.Id.Value, sent.Command.ProjectId);
        Assert.Equal(world.Request.Id.Value, sent.Command.RequestId);
        Assert.Equal(ClaimToken, sent.Command.ClaimToken);
        Assert.Equal(world.Assignment.BindingValidationRevisionSnapshot, sent.Command.BindingRevision);
        Assert.Equal(
            new DateTimeOffset((await OperationAsync(db)).DeadlineUtcTicks!.Value, TimeSpan.Zero),
            sent.Command.Deadline);
        Assert.Equal(nameof(RecoveryOperationStatus.Running), (await OperationAsync(db)).Status);
        Assert.Single(await db.Set<RecoveryHoldRow>().ToListAsync());
        Assert.Equal(world.Node.Id, (await db.ExecutionAssignments.SingleAsync()).NodeIdSnapshot);
    }

    [Fact]
    public async Task Offline_dispatch_persists_intervention_without_clearing_hold()
    {
        await using var db = CreateContext();
        var world = await StartRecoveryAsync(db);
        var gateway = new RecordingGateway();
        var dispatcher = CreateDispatcher(db, gateway);

        await dispatcher.DispatchAsync(world.Project.Id, world.OperationId);

        Assert.Empty(gateway.Sent);
        var operation = await OperationAsync(db);
        Assert.Equal(nameof(RecoveryOperationStatus.NeedsIntervention), operation.Status);
        Assert.Contains(RecoveryReasonCodes.NodeUnreachable, operation.BlockerCodesJson);
        Assert.Contains(
            await db.Set<RecoveryAuditFactRow>().ToListAsync(),
            row => row.Kind == "node_unreachable" && row.Reason == RecoveryReasonCodes.NodeUnreachable);
        Assert.Single(await db.Set<RecoveryHoldRow>().ToListAsync());
        Assert.Null((await db.Set<RecoveryTargetRow>().SingleAsync()).Outcome);
        Assert.Equal(world.Node.Id, (await db.ExecutionAssignments.SingleAsync()).NodeIdSnapshot);
        Assert.Equal(ClaimToken, (await db.ExecutionAssignments.SingleAsync()).ClaimToken);
    }

    [Fact]
    public async Task Terminal_targets_are_not_commanded()
    {
        await using var db = CreateContext();
        var world = SeedRunningAssignment(db, ExecutionAssignmentState.Cancelled);
        await db.SaveChangesAsync();
        var seeded = SeedOperation(db, world, nameof(ExecutionAssignmentState.Cancelled));
        await db.SaveChangesAsync();
        var target = await db.Set<RecoveryTargetRow>().SingleAsync();
        target.Outcome = nameof(ExecutionAssignmentState.Cancelled);
        await db.SaveChangesAsync();
        var gateway = new RecordingGateway { Online = { world.Node.Id.Value } };
        var dispatcher = CreateDispatcher(db, gateway);

        await dispatcher.DispatchAsync(world.Project.Id, seeded.OperationId);

        Assert.Empty(gateway.Sent);
        Assert.Equal(nameof(RecoveryOperationStatus.Running), (await OperationAsync(db)).Status);
    }

    [Fact]
    public async Task Dispatch_for_node_redelivers_only_that_owner()
    {
        await using var db = CreateContext();
        var world = await StartRecoveryAsync(db);
        var foreign = SeedForeignAssignment(db);
        await db.SaveChangesAsync();
        var gateway = new RecordingGateway
        {
            Online = { world.Node.Id.Value, foreign.Assignment.NodeIdSnapshot.Value },
        };
        var dispatcher = CreateDispatcher(db, gateway);

        await dispatcher.DispatchForNodeAsync(world.Node.Id);

        var sent = Assert.Single(gateway.Sent);
        Assert.Equal(world.Node.Id, sent.NodeId);
        Assert.Equal(world.Request.Id.Value, sent.Command.RequestId);
    }

    private RecoveryAttemptDispatcher CreateDispatcher(
        ControlPlaneDbContext db,
        INodeRecoveryCommandGateway gateway) =>
        new(_clock, db, gateway, new ProjectionNotifier());

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
        return new World(node, project, request, assignment, Guid.Empty, binding.ValidationRevision);
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
        return world with { OperationId = operationId };
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

    private static Task<RecoveryOperationRow> OperationAsync(ControlPlaneDbContext db) =>
        db.Set<RecoveryOperationRow>().SingleAsync();

    private sealed class RecordingGateway : INodeRecoveryCommandGateway
    {
        public HashSet<Guid> Online { get; } = [];

        public List<(NodeId NodeId, RecoverAssignmentCommandMessage Command)> Sent { get; } = [];

        public Task<bool> TrySendAsync(
            NodeId nodeId,
            RecoverAssignmentCommandMessage command,
            CancellationToken cancellationToken = default)
        {
            if (!Online.Contains(nodeId.Value))
            {
                return Task.FromResult(false);
            }

            Sent.Add((nodeId, command));
            return Task.FromResult(true);
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
