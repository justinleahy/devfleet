using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Recovery;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Reservations;
using PiCommandCenter.Infrastructure.Completion;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Recovery;
using PiCommandCenter.Infrastructure.Reservations;

namespace PiCommandCenter.Infrastructure.Tests.Recovery;

public sealed class ManualRecoveryServiceTests
{
    private const string ClaimToken = "assignment-token-0123456789abcdef0123456789abcdef";

    private readonly string _sqlitePath = TestRepositories.CreateSqliteFile();
    private readonly FakeTimeProvider _clock = TestNodes.Clock();

    [Fact]
    public async Task Incomplete_attestation_is_rejected_with_zero_mutation()
    {
        var world = await SeedNeedsInterventionAsync();
        await using var db = CreateContext();
        var before = await SnapshotAsync(db, world.OperationId);
        var command = ValidCommand(world) with { WriterAccessPrevented = false };

        await Assert.ThrowsAsync<RecoveryNotReadyException>(() => ConfirmAsync(db, world, command));

        await AssertUnchangedAsync(db, world, before);
    }

    [Fact]
    public async Task Stale_operation_version_is_rejected_with_zero_mutation()
    {
        var world = await SeedNeedsInterventionAsync();
        await using var db = CreateContext();
        var before = await SnapshotAsync(db, world.OperationId);
        var command = ValidCommand(world) with { ExpectedOperationVersion = world.OperationVersion + 9 };

        await Assert.ThrowsAsync<RecoveryRevisionConflictException>(() => ConfirmAsync(db, world, command));

        await AssertUnchangedAsync(db, world, before);
    }

    [Fact]
    public async Task Cross_binding_evidence_is_rejected_with_zero_mutation()
    {
        var world = await SeedNeedsInterventionAsync();
        await using var db = CreateContext();
        await db.Set<RecoveryTargetRow>()
            .Where(t => t.OperationId == world.OperationId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.BindingRevision, 99));
        db.ChangeTracker.Clear();
        var before = await SnapshotAsync(db, world.OperationId);

        await Assert.ThrowsAsync<RecoveryNotReadyException>(() => ConfirmAsync(db, world, ValidCommand(world)));

        await AssertUnchangedAsync(db, world, before);
    }

    [Fact]
    public async Task Wrong_project_name_is_rejected_with_zero_mutation()
    {
        var world = await SeedNeedsInterventionAsync();
        await using var db = CreateContext();
        var before = await SnapshotAsync(db, world.OperationId);
        var command = ValidCommand(world) with { ExactProjectName = "not-this-project" };

        await Assert.ThrowsAsync<RecoveryNotReadyException>(() => ConfirmAsync(db, world, command));

        await AssertUnchangedAsync(db, world, before);
    }

    [Fact]
    public async Task Future_repository_evidence_is_rejected_with_zero_mutation()
    {
        var world = await SeedNeedsInterventionAsync();
        await using var db = CreateContext();
        var before = await SnapshotAsync(db, world.OperationId);
        var command = ValidCommand(world) with
        {
            RepositoryCollectedAt = _clock.GetUtcNow().AddMinutes(1),
        };

        await Assert.ThrowsAsync<RecoveryNotReadyException>(() => ConfirmAsync(db, world, command));

        await AssertUnchangedAsync(db, world, before);
    }

    [Fact]
    public async Task Old_repository_evidence_is_rejected_with_zero_mutation()
    {
        var world = await SeedNeedsInterventionAsync();
        await using var db = CreateContext();
        var before = await SnapshotAsync(db, world.OperationId);
        var command = ValidCommand(world) with
        {
            RepositoryCollectedAt = _clock.GetUtcNow().AddHours(-2),
        };

        await Assert.ThrowsAsync<RecoveryNotReadyException>(() => ConfirmAsync(db, world, command));

        await AssertUnchangedAsync(db, world, before);
    }

    [Fact]
    public async Task Valid_history_gap_attestation_cancels_fences_audits_and_retains_hold()
    {
        var world = await SeedNeedsInterventionAsync(includeReservation: true, includePending: true);
        await using var db = CreateContext();

        var result = await ConfirmAsync(db, world, ValidCommand(world));

        Assert.Equal(RecoveryOperationStatus.Recovered, result.Status);
        Assert.Equal(nameof(ExecutionAssignmentState.Cancelled), Assert.Single(result.AssignmentTargets).Outcome);
        Assert.Contains("operator-attestation", result.EvidenceJson);
        Assert.DoesNotContain("node-observed", result.EvidenceJson, StringComparison.OrdinalIgnoreCase);
        var recoveredAssignment = await db.ExecutionAssignments.SingleAsync();
        Assert.Equal(ExecutionAssignmentState.Cancelled, recoveredAssignment.State);
        Assert.True(recoveredAssignment.Version >= 3);
        Assert.Equal(WorkRequestStatus.Cancelled, (await db.WorkRequests.SingleAsync()).Status);
        Assert.Empty(await db.PendingTerminalizations.ToListAsync());
        var lease = await db.Set<ReservationLeaseRow>().SingleAsync();
        Assert.Equal(nameof(ReservationLeaseState.Released), lease.State);
        Assert.True(lease.FencingToken > 1);
        Assert.Equal(lease.FencingToken, (await db.ProjectFencingTokens.SingleAsync()).LastFencingToken);
        Assert.Single(await db.Set<RecoveryHoldRow>().ToListAsync());
        Assert.Contains(
            await db.Set<RecoveryAuditFactRow>().ToListAsync(),
            fact => fact.Kind == "operator-attestation"
                && fact.PayloadJson != null
                && fact.PayloadJson.Contains("operator-attestation")
                && fact.PayloadJson.Contains("spool gap"));
    }

    [Fact]
    public async Task Same_idempotency_key_replays_and_different_input_conflicts()
    {
        var world = await SeedNeedsInterventionAsync();
        await using var db = CreateContext();
        var command = ValidCommand(world);

        var first = await ConfirmAsync(db, world, command);
        var second = await ConfirmAsync(db, world, command);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(RecoveryOperationStatus.Recovered, second.Status);
        Assert.Equal(1, await db.Set<RecoveryOperationRow>().CountAsync());
        await Assert.ThrowsAsync<RecoveryIdempotencyConflictException>(() =>
            ConfirmAsync(db, world, command with { Reason = "other reason" }));
    }

    [Fact]
    public async Task One_mismatched_target_rolls_back_the_whole_transition()
    {
        var world = await SeedNeedsInterventionAsync(secondAssignment: true);
        await using var db = CreateContext();
        var firstTarget = await db.Set<RecoveryTargetRow>()
            .OrderBy(t => t.RequestId)
            .FirstAsync(t => t.OperationId == world.OperationId);
        await db.Set<RecoveryTargetRow>()
            .Where(t => t.Id == firstTarget.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.BindingRevision, 77));
        db.ChangeTracker.Clear();
        var before = await SnapshotAsync(db, world.OperationId);

        await Assert.ThrowsAsync<RecoveryNotReadyException>(() => ConfirmAsync(db, world, ValidCommand(world)));

        await AssertUnchangedAsync(db, world, before);
        Assert.Equal(2, await db.ExecutionAssignments.CountAsync(a => a.State == ExecutionAssignmentState.Cancelling));
    }

    [Fact]
    public async Task Post_capture_cancelling_transition_is_accepted()
    {
        var world = await SeedNeedsInterventionAsync();
        await using var db = CreateContext();
        var target = await db.Set<RecoveryTargetRow>()
            .SingleAsync(t => t.OperationId == world.OperationId);
        var live = await db.ExecutionAssignments.SingleAsync();

        Assert.Equal(ExecutionAssignmentState.Cancelling, live.State);
        Assert.Equal(target.CapturedVersion + 1, live.Version);

        var result = await ConfirmAsync(db, world, ValidCommand(world));

        Assert.Equal(RecoveryOperationStatus.Recovered, result.Status);
        Assert.Equal(nameof(ExecutionAssignmentState.Cancelled), Assert.Single(result.AssignmentTargets).Outcome);
    }

    [Fact]
    public async Task Resolved_partial_progress_target_is_left_untouched()
    {
        var world = await SeedNeedsInterventionAsync(secondAssignment: true);
        await using var db = CreateContext();
        var firstTarget = await db.Set<RecoveryTargetRow>()
            .OrderBy(t => t.RequestId)
            .FirstAsync(t => t.OperationId == world.OperationId);
        const string originalEvidence = """{"keep":"original-operator-evidence"}""";
        await db.Set<RecoveryTargetRow>()
            .Where(t => t.Id == firstTarget.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Outcome, nameof(ExecutionAssignmentState.Cancelled))
                .SetProperty(t => t.EvidenceJson, originalEvidence));
        db.ChangeTracker.Clear();

        var result = await ConfirmAsync(db, world, ValidCommand(world));

        Assert.Equal(RecoveryOperationStatus.Recovered, result.Status);
        var kept = await db.Set<RecoveryTargetRow>().SingleAsync(t => t.Id == firstTarget.Id);
        Assert.Equal(nameof(ExecutionAssignmentState.Cancelled), kept.Outcome);
        Assert.Equal(originalEvidence, kept.EvidenceJson);
        Assert.Equal(2, result.AssignmentTargets.Count(t => t.Outcome == nameof(ExecutionAssignmentState.Cancelled)));
    }

    [Fact]
    public async Task Reservation_revision_drift_is_rejected_with_zero_mutation()
    {
        var world = await SeedNeedsInterventionAsync(includeReservation: true);
        await using var db = CreateContext();
        await db.Set<ReservationLeaseRow>()
            .Where(row => row.Id == world.LeaseId)
            .ExecuteUpdateAsync(s => s.SetProperty(row => row.Version, 99));
        db.ChangeTracker.Clear();
        var before = await SnapshotAsync(db, world.OperationId);

        await Assert.ThrowsAsync<RecoveryNotReadyException>(() => ConfirmAsync(db, world, ValidCommand(world)));

        await AssertUnchangedAsync(db, world, before);
        Assert.Equal(nameof(ReservationLeaseState.Active), (await db.Set<ReservationLeaseRow>().SingleAsync()).State);
    }

    [Fact]
    public async Task Oversize_operator_evidence_is_rejected_with_zero_mutation()
    {
        var world = await SeedNeedsInterventionAsync();
        await using var db = CreateContext();
        var before = await SnapshotAsync(db, world.OperationId);
        var command = ValidCommand(world) with
        {
            ProcessStopEvidence = new string('x', ManualRecoveryService.MaxTextLength + 1),
        };

        await Assert.ThrowsAsync<ArgumentException>(() => ConfirmAsync(db, world, command));

        await AssertUnchangedAsync(db, world, before);
        Assert.Null((await db.Set<RecoveryOperationRow>().SingleAsync(o => o.Id == world.OperationId)).EvidenceJson);
    }

    private async Task<Seeded> SeedNeedsInterventionAsync(
        bool includeReservation = false,
        bool includePending = false,
        bool secondAssignment = false)
    {
        await using var db = CreateContext();
        var first = SeedRunningAssignment(db);
        if (secondAssignment)
        {
            SeedRunningAssignment(db, project: first.Project, node: first.Node, binding: first.Binding);
        }


        Guid? leaseId = null;
        if (includeReservation)
        {
            leaseId = Guid.NewGuid();
            db.Set<ReservationLeaseRow>().Add(new ReservationLeaseRow
            {
                Id = leaseId.Value,
                ProjectId = first.Project.Id.Value,
                RequestId = first.Request.Id.Value,
                OwnerSessionId = "session",
                Reason = "write",
                FencingToken = 1,
                State = nameof(ReservationLeaseState.Active),
                AcquiredAtUtcTicks = _clock.GetUtcNow().UtcTicks,
                LastRenewedAtUtcTicks = _clock.GetUtcNow().UtcTicks,
                ExpiresAtUtcTicks = _clock.GetUtcNow().AddMinutes(2).UtcTicks,
                Version = 1,
            });
            db.ProjectFencingTokens.Add(new ProjectFencingTokenRow
            {
                ProjectId = first.Project.Id.Value,
                LastFencingToken = 1,
            });
        }

        if (includePending)
        {
            db.PendingTerminalizations.Add(new PendingTerminalizationRow
            {
                RequestId = first.Request.Id,
                ProjectId = first.Project.Id,
                NodeId = first.Node.Id,
                ClaimToken = ClaimToken,
                Intent = "Complete",
                AcceptedAtUtcTicks = _clock.GetUtcNow().UtcTicks,
                Version = 1,
            });
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var recovery = new ProjectRecoveryService(_clock, db, new ProjectionNotifier());
        var diagnosis = await recovery.GetDiagnosisAsync(first.Project.Id);
        var started = await recovery.StartAsync(
            first.Project.Id,
            new StartProjectRecoveryCommand(diagnosis.InventoryRevision, "stuck", "operator", "start-key"));
        var operation = await db.Set<RecoveryOperationRow>().SingleAsync(o => o.Id == started.Operation!.Id);
        operation.Status = nameof(RecoveryOperationStatus.NeedsIntervention);
        await db.SaveChangesAsync();
        return new Seeded(
            first.Project.Id,
            first.Project.DisplayName,
            started.Operation!.Id,
            started.Operation.Version,
            started.Operation.Attempt,
            leaseId);
    }

    private ConfirmManualProjectRecoveryCommand ValidCommand(Seeded world) =>
        new(
            world.OperationId,
            world.OperationVersion,
            world.Attempt,
            world.ProjectName,
            "local stop",
            "admin",
            "manual-key",
            ConfirmOriginalExecutionCannotResume: true,
            WriterAccessPrevented: true,
            AcknowledgeEvidenceGaps: true,
            ProcessStopEvidence: "stopped assignment trees at 2026-09-06T12:00:00Z; descendants excluded",
            RepositoryStatusSnapshot: "dirty worktree; HEAD main; owning workspace /repo",
            RepositoryStatusSource: "administrator inspection of owning workspace",
            RepositoryCollectedAt: _clock.GetUtcNow(),
            ReservationAndEventGapAccounting: "lease captured; spool gap acknowledged");

    private Task<ProjectRecoveryOperation> ConfirmAsync(
        ControlPlaneDbContext db,
        Seeded world,
        ConfirmManualProjectRecoveryCommand command) =>
        new ManualRecoveryService(_clock, db, new ProjectionNotifier())
            .ConfirmManualAsync(world.ProjectId, command);

    private ControlPlaneDbContext CreateContext() => TestRepositories.CreateContext(_sqlitePath);

    private AssignmentWorld SeedRunningAssignment(
        ControlPlaneDbContext db,
        ExecutionAssignmentState state = ExecutionAssignmentState.Running,
        Project? project = null,
        FleetNode? node = null,
        WorkspaceBinding? binding = null)
    {
        node ??= TestNodes.SeedNode(db, TestNodes.NewNodeId(), _clock);
        project ??= TestNodes.SeedProject(db, _clock, displayName: "RecoverMe");
        if (binding is null)
        {
            var repositoryPath = Path.Combine(
                Path.GetTempPath(),
                "pi-cc-tests",
                Guid.NewGuid().ToString("N"),
                "repo");
            binding = WorkspaceBinding.Designate(project.Id, node.Id, repositoryPath, _clock.GetUtcNow());
            Assert.True(binding.ApplyValidationResult(
                node.Id,
                binding.ValidationRevision,
                WorkspaceBindingStatus.Valid,
                WorkspaceBinding.ValidValidationCode,
                "ready",
                repositoryPath,
                _clock.GetUtcNow()));
            db.WorkspaceBindings.Add(binding);
        }

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
        return new AssignmentWorld(node, project, request, assignment, binding);
    }

    private static async Task<MutationSnapshot> SnapshotAsync(ControlPlaneDbContext db, Guid operationId)
    {
        var operation = await db.Set<RecoveryOperationRow>().AsNoTracking().SingleAsync(o => o.Id == operationId);

        var assignmentStates = await db.ExecutionAssignments.AsNoTracking()
            .OrderBy(a => a.RequestId)
            .Select(a => a.State)
            .ToArrayAsync();
        var requestStatuses = await db.WorkRequests.AsNoTracking()
            .OrderBy(r => r.Id)
            .Select(r => r.Status)
            .ToArrayAsync();
        var holdCount = await db.Set<RecoveryHoldRow>().CountAsync();
        var auditCount = await db.Set<RecoveryAuditFactRow>().CountAsync();
        var pendingCount = await db.PendingTerminalizations.CountAsync();
        var leaseStates = await db.Set<ReservationLeaseRow>().AsNoTracking()
            .OrderBy(l => l.Id)
            .Select(l => l.State)
            .ToArrayAsync();
        return new MutationSnapshot(
            operation.Status,
            operation.Version,
            assignmentStates,
            requestStatuses,
            holdCount,
            auditCount,
            pendingCount,
            leaseStates);
    }

    private static async Task AssertUnchangedAsync(
        ControlPlaneDbContext db,
        Seeded world,
        MutationSnapshot before)
    {
        db.ChangeTracker.Clear();
        var after = await SnapshotAsync(db, world.OperationId);
        Assert.Equal(before, after);
    }

    private sealed record Seeded(
        ProjectId ProjectId,
        string ProjectName,
        Guid OperationId,
        long OperationVersion,
        int Attempt,
        Guid? LeaseId);

    private sealed record MutationSnapshot(
        string Status,
        long Version,
        ExecutionAssignmentState[] AssignmentStates,
        WorkRequestStatus[] RequestStatuses,
        int HoldCount,
        int AuditCount,
        int PendingCount,
        string[] LeaseStates) : IEquatable<MutationSnapshot>
    {
        public bool Equals(MutationSnapshot? other) =>
            other is not null
            && Status == other.Status
            && Version == other.Version
            && HoldCount == other.HoldCount
            && AuditCount == other.AuditCount
            && PendingCount == other.PendingCount
            && AssignmentStates.SequenceEqual(other.AssignmentStates)
            && RequestStatuses.SequenceEqual(other.RequestStatuses)
            && LeaseStates.SequenceEqual(other.LeaseStates);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Status);
            hash.Add(Version);
            hash.Add(HoldCount);
            hash.Add(AuditCount);
            hash.Add(PendingCount);
            foreach (var state in AssignmentStates)
            {
                hash.Add(state);
            }

            foreach (var status in RequestStatuses)
            {
                hash.Add(status);
            }

            foreach (var lease in LeaseStates)
            {
                hash.Add(lease);
            }

            return hash.ToHashCode();
        }
    }
    private sealed record AssignmentWorld(
        FleetNode Node,
        Project Project,
        WorkRequest Request,
        ExecutionAssignment Assignment,
        WorkspaceBinding Binding);
}
