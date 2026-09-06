using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Recovery;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Reservations;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Recovery;
using PiCommandCenter.Infrastructure.Reservations;

namespace PiCommandCenter.Infrastructure.Tests.Recovery;

public sealed class RecoveryPersistenceTests
{
    private const string ClaimToken = "assignment-token-0123456789abcdef0123456789abcdef";

    private readonly FakeTimeProvider _clock = TestNodes.Clock();

    [Fact]
    public async Task Started_operation_survives_a_new_sqlite_context()
    {
        var sqlitePath = TestRepositories.CreateSqliteFile();
        Guid operationId;
        ProjectId projectId;
        await using (var db = TestRepositories.CreateContext(sqlitePath))
        {
            var world = SeedRunningAssignment(db);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var service = CreateService(db);
            var diagnosis = await service.GetDiagnosisAsync(world.Project.Id);
            var started = await service.StartAsync(
                world.Project.Id,
                new StartProjectRecoveryCommand(diagnosis.InventoryRevision, "stuck", "operator", "persist"));
            operationId = started.Operation!.Id;
            projectId = world.Project.Id;
        }

        await using var reload = TestRepositories.CreateContext(sqlitePath, createSchema: false);
        var restored = await CreateService(reload).GetOperationAsync(projectId, operationId);
        Assert.Equal(operationId, restored.Id);
        Assert.Equal(RecoveryOperationStatus.Running, restored.Status);
        Assert.Single(restored.AssignmentTargets);
        Assert.Single(await reload.Set<RecoveryHoldRow>().ToListAsync());
        Assert.Single(await reload.Set<RecoveryAuditFactRow>().ToListAsync());
        Assert.Equal(ExecutionAssignmentState.Cancelling, (await reload.ExecutionAssignments.SingleAsync()).State);
    }

    [Fact]
    public async Task Hold_survives_recovered_status_until_resume_after_targets_are_safe()
    {
        var sqlitePath = TestRepositories.CreateSqliteFile();
        await using var db = TestRepositories.CreateContext(sqlitePath);
        var world = SeedRunningAssignment(db);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = CreateService(db);
        var diagnosis = await service.GetDiagnosisAsync(world.Project.Id);
        var started = await service.StartAsync(
            world.Project.Id,
            new StartProjectRecoveryCommand(diagnosis.InventoryRevision, "stuck", "operator", "hold"));
        var operation = await db.Set<RecoveryOperationRow>().SingleAsync();
        operation.Status = nameof(RecoveryOperationStatus.Recovered);
        operation.CompletedAtUtcTicks = _clock.GetUtcNow().UtcTicks;
        await db.SaveChangesAsync();

        var held = await service.GetDiagnosisAsync(world.Project.Id);
        Assert.True(held.HoldPresent);
        Assert.Equal(RecoveryOperationStatus.Recovered, held.LatestOperation!.Status);
        Assert.NotNull(held.HoldVersion);

        var assignment = await db.ExecutionAssignments.SingleAsync();
        assignment.Cancel(_clock.GetUtcNow());
        var request = await db.WorkRequests.SingleAsync();
        request.ConfirmCancellation(_clock.GetUtcNow());
        await db.SaveChangesAsync();

        await service.ResumeAsync(world.Project.Id, started.Operation!.Id, held.HoldVersion.Value, "resume-actor");
        Assert.False((await service.GetDiagnosisAsync(world.Project.Id)).HoldPresent);
        Assert.Empty(await db.Set<RecoveryHoldRow>().ToListAsync());
        Assert.Contains(
            await db.Set<RecoveryAuditFactRow>().ToListAsync(),
            fact => fact.Kind == "resumed" && fact.Actor == "resume-actor");
    }

    [Fact]
    public async Task Unresolved_reservation_keeps_resume_rejected_and_hold_in_place()
    {
        var sqlitePath = TestRepositories.CreateSqliteFile();
        await using var db = TestRepositories.CreateContext(sqlitePath);
        var world = SeedRunningAssignment(db);
        db.Set<ReservationLeaseRow>().Add(new ReservationLeaseRow
        {
            Id = Guid.NewGuid(),
            ProjectId = world.Project.Id.Value,
            RequestId = world.Request.Id.Value,
            OwnerSessionId = "session",
            Reason = "write",
            FencingToken = 1,
            State = nameof(ReservationLeaseState.Active),
            AcquiredAtUtcTicks = _clock.GetUtcNow().UtcTicks,
            LastRenewedAtUtcTicks = _clock.GetUtcNow().UtcTicks,
            ExpiresAtUtcTicks = _clock.GetUtcNow().AddMinutes(2).UtcTicks,
            Version = 1,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = CreateService(db);
        var diagnosis = await service.GetDiagnosisAsync(world.Project.Id);
        Assert.Single(diagnosis.UnresolvedReservations);
        var started = await service.StartAsync(
            world.Project.Id,
            new StartProjectRecoveryCommand(diagnosis.InventoryRevision, "stuck", "operator", "resv"));
        Assert.Single(started.Operation!.ReservationTargets);
        var operationId = started.Operation.Id;

        var operation = await db.Set<RecoveryOperationRow>().SingleAsync();
        operation.Status = nameof(RecoveryOperationStatus.Recovered);
        var assignment = await db.ExecutionAssignments.SingleAsync();
        assignment.Cancel(_clock.GetUtcNow());
        (await db.WorkRequests.SingleAsync()).ConfirmCancellation(_clock.GetUtcNow());
        await db.SaveChangesAsync();

        var holdVersion = (await service.GetDiagnosisAsync(world.Project.Id)).HoldVersion!.Value;
        await Assert.ThrowsAsync<RecoveryNotReadyException>(() =>
            service.ResumeAsync(world.Project.Id, operationId, holdVersion, "operator"));
        Assert.True((await service.GetDiagnosisAsync(world.Project.Id)).HoldPresent);
    }

    private ProjectRecoveryService CreateService(ControlPlaneDbContext db) =>
        new(_clock, db, new ProjectionNotifier());

    private AssignmentWorld SeedRunningAssignment(ControlPlaneDbContext db)
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
            ExecutionAssignmentState.Running,
            ClaimToken,
            now,
            now.AddMinutes(5),
            lastRenewedAt: null,
            lastReconciledAt: null,
            terminalAt: null,
            version: 1);
        db.ExecutionAssignments.Add(assignment);
        return new AssignmentWorld(project, request, assignment);
    }

    private sealed record AssignmentWorld(
        Project Project,
        WorkRequest Request,
        ExecutionAssignment Assignment);
}
