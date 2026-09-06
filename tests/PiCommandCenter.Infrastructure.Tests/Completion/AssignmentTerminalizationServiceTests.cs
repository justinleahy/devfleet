using System.Text.Json;
using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Completion;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Reservations;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Domain.Verification;
using PiCommandCenter.Infrastructure.Completion;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Reservations;
using PiCommandCenter.Infrastructure.Verification;

namespace PiCommandCenter.Infrastructure.Tests.Completion;

public class AssignmentTerminalizationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 21, 0, 0, TimeSpan.Zero);

    private const string ClaimToken = "claim-token";
    private const string RootSessionId = "root-session";
    private const string Fingerprint = "fingerprint-current";
    private const string PolicyRevision = "policy-1";
    private const string BaselineCommandId = "repository-integrity";
    private const string ProjectCommandId = "dotnet-test";

    private sealed class FrozenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static AssignmentQuiescenceProof CleanProof => new(
        AdmissionClosed: true,
        ActiveChildren: 0,
        ActiveOperations: 0,
        ActiveProcesses: 0,
        PendingEvents: 0,
        ActiveReservations: 0,
        RepositoryInspected: true,
        ObservedAt: Now);

    private static AssignmentQuiescenceProof DirtyProof(string field) => field switch
    {
        "AdmissionClosed" => CleanProof with { AdmissionClosed = false },
        "ActiveChildren" => CleanProof with { ActiveChildren = 1 },
        "ActiveOperations" => CleanProof with { ActiveOperations = 1 },
        "ActiveProcesses" => CleanProof with { ActiveProcesses = 1 },
        "PendingEvents" => CleanProof with { PendingEvents = 1 },
        "ActiveReservations" => CleanProof with { ActiveReservations = 1 },
        "RepositoryInspected" => CleanProof with { RepositoryInspected = false },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    [Fact]
    public async Task Empty_evidence_lists_every_mandatory_gap_and_changes_nothing()
    {
        var world = await SeedAsync(happy: false);
        var decision = await BeginAsync(world, new CompletionEvidence(" ", ChangedFiles: null, [], " "));

        Assert.False(decision.Accepted);
        Assert.Null(decision.Result);
        Assert.Contains(CompletionRequirements.ResultSummary, decision.MissingRequirements);
        Assert.Contains(CompletionRequirements.DiffCaptured, decision.MissingRequirements);
        Assert.Contains(CompletionRequirements.PlanEvent, decision.MissingRequirements);
        Assert.Contains(CompletionRequirements.ImplementationChild, decision.MissingRequirements);
        Assert.Contains(CompletionRequirements.IndependentReviewer, decision.MissingRequirements);
        Assert.Contains(CompletionRequirements.VerificationEvidence, decision.MissingRequirements);
        Assert.Equal(ExecutionAssignmentState.Running, Assignment(world).State);
    }

    [Fact]
    public async Task Blocking_unresolved_finding_rejects()
    {
        var world = await SeedAsync(happy: true);
        var evidence = HappyEvidence() with
        {
            ReviewFindings = [new ReviewFinding("f1", "leak", Blocking: true, Resolved: false, UserOverridden: false)],
        };

        var decision = await BeginAsync(world, evidence);

        Assert.False(decision.Accepted);
        Assert.Contains(CompletionRequirements.UnresolvedBlockingFinding, decision.MissingRequirements);
        Assert.Equal(ExecutionAssignmentState.Running, Assignment(world).State);
    }

    [Fact]
    public async Task User_overridden_blocking_finding_is_allowed()
    {
        var world = await SeedAsync(happy: true);
        var evidence = HappyEvidence() with
        {
            ReviewFindings = [new ReviewFinding("f1", "nits", true, false, UserOverridden: true)],
        };

        var decision = await BeginAsync(world, evidence);

        Assert.True(decision.Accepted, string.Join(",", decision.MissingRequirements));
    }

    [Fact]
    public async Task Active_source_reservation_blocks_completion()
    {
        var world = await SeedAsync(happy: true, activeLease: true);
        var decision = await BeginAsync(world, HappyEvidence());

        Assert.False(decision.Accepted);
        Assert.Contains(CompletionRequirements.ActiveReservation, decision.MissingRequirements);
    }

    [Fact]
    public async Task RecoveryRequired_lease_does_not_block_completion()
    {
        var world = await SeedAsync(happy: true, recoveryLease: true);
        var decision = await BeginAsync(world, HappyEvidence());

        Assert.True(decision.Accepted, string.Join(",", decision.MissingRequirements));
    }

    [Fact]
    public async Task Active_mutation_activity_blocks_completion()
    {
        var world = await SeedAsync(happy: true, mutating: true);
        var decision = await BeginAsync(world, HappyEvidence());

        Assert.False(decision.Accepted);
        Assert.Contains(CompletionRequirements.ActiveMutation, decision.MissingRequirements);
    }

    [Fact]
    public async Task Unknown_changed_file_ownership_blocks()
    {
        var world = await SeedAsync(happy: true);
        var evidence = HappyEvidence() with { ChangedFiles = ["src/Unowned.cs"] };
        var decision = await BeginAsync(world, evidence);

        Assert.False(decision.Accepted);
        Assert.Contains(CompletionRequirements.OwnershipKnown, decision.MissingRequirements);
    }

    [Fact]
    public async Task Failed_mandatory_verification_blocks()
    {
        var world = await SeedAsync(happy: true, verificationPassed: false);
        var decision = await BeginAsync(world, HappyEvidence());

        Assert.False(decision.Accepted);
        Assert.Contains(
            CompletionRequirements.VerificationFailed(BaselineCommandId),
            decision.MissingRequirements);
    }

    [Fact]
    public async Task Completed_verifier_child_satisfies_independent_review()
    {
        var world = await SeedAsync(happy: true, reviewRole: "verifier");
        var decision = await BeginAsync(world, HappyEvidence());

        Assert.True(decision.Accepted, string.Join(",", decision.MissingRequirements));
        Assert.DoesNotContain(CompletionRequirements.IndependentReviewer, decision.MissingRequirements);
    }


    [Fact]
    public async Task Baseline_cannot_replace_a_selected_mandatory_project_check()
    {
        var world = await SeedAsync(
            happy: true,
            mandatoryCommandIds: [BaselineCommandId, ProjectCommandId]);

        var decision = await BeginAsync(world, HappyEvidence());

        Assert.False(decision.Accepted);
        Assert.Contains(
            CompletionRequirements.VerificationNotRun(ProjectCommandId),
            decision.MissingRequirements);
        Assert.Equal(ExecutionAssignmentState.Running, Assignment(world).State);
    }

    [Fact]
    public async Task Matching_green_policy_snapshot_survives_service_restart()
    {
        var sqlite = TestRepositories.CreateSqliteFile();
        var seeded = await SeedAsync(
            happy: true,
            sqlitePath: sqlite,
            mandatoryCommandIds: [BaselineCommandId, ProjectCommandId]);
        seeded.Db.VerificationRuns.Add(new VerificationRunRow
        {
            Id = Guid.NewGuid(),
            RequestId = seeded.RequestId.Value,
            ProfileId = "default",
            CommandId = ProjectCommandId,
            Status = nameof(VerificationRunStatus.Passed),
            ExitCode = 0,
            StartedAtUtcTicks = Now.UtcTicks,
            CompletedAtUtcTicks = Now.UtcTicks,
            OutputSummary = "ok",
            Mandatory = true,
            Fingerprint = Fingerprint,
            PolicyRevision = PolicyRevision,
            RunKind = nameof(VerificationRunKind.ProjectCheck),
            AttemptId = Guid.NewGuid(),
        });
        await seeded.Db.SaveChangesAsync();
        await seeded.Db.DisposeAsync();

        await using var restarted = TestRepositories.CreateContext(sqlite, createSchema: false);
        var world = new World(
            restarted,
            new AssignmentTerminalizationService(new FrozenClock(), restarted, new ProjectionNotifier()),
            seeded.ProjectId,
            seeded.RequestId,
            seeded.NodeId);

        var decision = await BeginAsync(world, HappyEvidence());

        Assert.True(decision.Accepted, string.Join(",", decision.MissingRequirements));
    }

    [Fact]
    public async Task Stale_and_intermediate_rows_do_not_satisfy_the_policy_snapshot()
    {
        var stale = await SeedAsync(happy: true, runFingerprint: "fingerprint-stale");
        var intermediate = await SeedAsync(happy: true, runKind: VerificationRunKind.Intermediate);

        var staleDecision = await BeginAsync(stale, HappyEvidence());
        var intermediateDecision = await BeginAsync(intermediate, HappyEvidence());

        Assert.Contains(
            CompletionRequirements.VerificationNotRun(BaselineCommandId),
            staleDecision.MissingRequirements);
        Assert.Contains(
            CompletionRequirements.VerificationNotRun(BaselineCommandId),
            intermediateDecision.MissingRequirements);
        Assert.Equal(ExecutionAssignmentState.Running, Assignment(stale).State);
        Assert.Equal(ExecutionAssignmentState.Running, Assignment(intermediate).State);
    }

    [Fact]
    public async Task Optional_failure_does_not_block_matching_mandatory_success()
    {
        var world = await SeedAsync(happy: true);
        world.Db.VerificationRuns.Add(new VerificationRunRow
        {
            Id = Guid.NewGuid(),
            RequestId = world.RequestId.Value,
            ProfileId = "devfleet-baseline",
            CommandId = "whitespace",
            Status = nameof(VerificationRunStatus.Failed),
            ExitCode = 1,
            StartedAtUtcTicks = Now.UtcTicks,
            CompletedAtUtcTicks = Now.UtcTicks,
            OutputSummary = "trailing whitespace",
            Mandatory = false,
            Fingerprint = Fingerprint,
            PolicyRevision = PolicyRevision,
            RunKind = nameof(VerificationRunKind.Baseline),
            AttemptId = Guid.NewGuid(),
        });
        await world.Db.SaveChangesAsync();

        var decision = await BeginAsync(world, HappyEvidence());

        Assert.True(decision.Accepted, string.Join(",", decision.MissingRequirements));
    }

    [Fact]
    public async Task Stale_policy_evidence_is_rejected_without_closing_admission()
    {
        var world = await SeedAsync(happy: true);
        var evidence = HappyEvidence() with { VerificationPolicyRevision = "policy-stale" };

        var decision = await BeginAsync(world, evidence);

        Assert.False(decision.Accepted);
        Assert.Contains(CompletionRequirements.VerificationStale, decision.MissingRequirements);
        Assert.Equal(ExecutionAssignmentState.Running, Assignment(world).State);
    }

    [Fact]
    public async Task Begin_complete_occupies_finalizing_without_terminalizing()
    {
        var world = await SeedAsync(happy: true);

        var decision = await BeginAsync(world, HappyEvidence());

        Assert.True(decision.Accepted, string.Join(",", decision.MissingRequirements));
        Assert.Null(decision.Result);
        Assert.Equal(ExecutionAssignmentState.Finalizing, Assignment(world).State);
        Assert.Equal(WorkRequestStatus.Verifying, Request(world).Status);
        Assert.Empty(world.Db.RequestResults);
        Assert.NotNull(Pending(world));
        Assert.Equal(nameof(TerminalizationIntent.Complete), Pending(world)!.Intent);
    }


    [Fact]
    public async Task Begin_marks_starting_assignment_running_before_finalizing()
    {
        var world = await SeedAsync(happy: true, markRunning: false);

        var decision = await BeginAsync(world, HappyEvidence());

        Assert.True(decision.Accepted, string.Join(",", decision.MissingRequirements));
        Assert.Equal(ExecutionAssignmentState.Finalizing, Assignment(world).State);
    }

    [Theory]
    [InlineData(TerminalizationIntent.Fail)]
    [InlineData(TerminalizationIntent.Cancel)]
    public async Task Fail_and_cancel_require_a_reason(TerminalizationIntent intent)
    {
        var world = await SeedAsync(happy: true);

        var decision = await world.Service.BeginAsync(
            world.NodeId, new ProjectId(world.ProjectId), world.RequestId,
            ClaimToken, RootSessionId, intent, evidence: null, reason: " ");

        Assert.False(decision.Accepted);
        Assert.Equal([CompletionRequirements.TerminalizationReason], decision.MissingRequirements);
        Assert.Equal(ExecutionAssignmentState.Running, Assignment(world).State);
    }

    [Fact]
    public async Task Complete_requires_evidence()
    {
        var world = await SeedAsync(happy: true);

        var decision = await world.Service.BeginAsync(
            world.NodeId, new ProjectId(world.ProjectId), world.RequestId,
            ClaimToken, RootSessionId, TerminalizationIntent.Complete, evidence: null, reason: null);

        Assert.False(decision.Accepted);
        Assert.Equal([CompletionRequirements.CompletionEvidence], decision.MissingRequirements);
        Assert.Equal(ExecutionAssignmentState.Running, Assignment(world).State);
    }

    [Fact]
    public async Task Begin_cancel_moves_recovery_required_assignment_to_cancelling()
    {
        var world = await SeedAsync(happy: true);
        Assignment(world).MarkRecoveryRequired(Now);
        await world.Db.SaveChangesAsync();

        var decision = await world.Service.BeginAsync(
            world.NodeId, new ProjectId(world.ProjectId), world.RequestId,
            ClaimToken, RootSessionId, TerminalizationIntent.Cancel, evidence: null, reason: "operator stop");

        Assert.True(decision.Accepted, string.Join(",", decision.MissingRequirements));
        Assert.Equal(ExecutionAssignmentState.Cancelling, Assignment(world).State);
        Assert.Equal(WorkRequestStatus.Cancelling, Request(world).Status);
        Assert.Null(Pending(world));
    }

    [Fact]
    public async Task Pre_root_cancel_accepts_null_session_correlation()
    {
        var world = await SeedAsync(happy: true, markRunning: false);

        var begin = await world.Service.BeginAsync(
            world.NodeId,
            new ProjectId(world.ProjectId),
            world.RequestId,
            ClaimToken,
            rootSessionId: null,
            TerminalizationIntent.Cancel,
            evidence: null,
            reason: "operator stop");
        var confirm = await world.Service.ConfirmAsync(
            world.NodeId,
            new ProjectId(world.ProjectId),
            world.RequestId,
            ClaimToken,
            rootSessionId: null,
            TerminalizationIntent.Cancel,
            evidence: null,
            reason: "operator stop",
            CleanProof);

        Assert.True(begin.Accepted, string.Join(",", begin.MissingRequirements));
        Assert.True(confirm.Accepted, string.Join(",", confirm.MissingRequirements));
        Assert.Equal(ExecutionAssignmentState.Cancelled, Assignment(world).State);
        Assert.Equal(WorkRequestStatus.Cancelled, Request(world).Status);
    }


    [Theory]
    [InlineData("AdmissionClosed")]
    [InlineData("ActiveChildren")]
    [InlineData("ActiveOperations")]
    [InlineData("ActiveProcesses")]
    [InlineData("PendingEvents")]
    [InlineData("ActiveReservations")]
    [InlineData("RepositoryInspected")]
    public async Task Confirm_rejects_any_nonzero_or_false_proof_field_without_terminalizing(string field)
    {
        var world = await SeedAsync(happy: true);
        Assert.True((await BeginAsync(world, HappyEvidence())).Accepted);

        var decision = await ConfirmAsync(world, TerminalizationIntent.Complete, HappyEvidence(), null, DirtyProof(field));

        Assert.False(decision.Accepted);
        Assert.Null(decision.Result);
        Assert.Single(decision.MissingRequirements);
        Assert.Equal(ExecutionAssignmentState.Finalizing, Assignment(world).State);
        Assert.Equal(WorkRequestStatus.Verifying, Request(world).Status);
        Assert.Empty(world.Db.RequestResults);
        Assert.NotNull(Pending(world));

    }

    [Theory]
    [InlineData(TerminalizationIntent.Complete)]
    [InlineData(TerminalizationIntent.Fail)]
    [InlineData(TerminalizationIntent.Cancel)]
    public async Task Every_intent_requires_the_same_clean_proof(TerminalizationIntent intent)
    {
        var world = await SeedAsync(happy: true);
        await BeginAsync(world, intent);

        var dirty = await ConfirmAsync(world, intent, HappyEvidence(), "why", DirtyProof("PendingEvents"));

        Assert.False(dirty.Accepted);
        Assert.Equal([CompletionRequirements.QuiescenceEvents], dirty.MissingRequirements);
        Assert.Equal(
            intent == TerminalizationIntent.Cancel
                ? ExecutionAssignmentState.Cancelling
                : ExecutionAssignmentState.Finalizing,
            Assignment(world).State);
        Assert.Equal(
            intent == TerminalizationIntent.Cancel
                ? WorkRequestStatus.Cancelling
                : WorkRequestStatus.Verifying,
            Request(world).Status);
    }

    [Fact]
    public async Task Confirm_complete_terminalizes_request_and_assignment_together()
    {
        var sqlite = TestRepositories.CreateSqliteFile();
        var world = await SeedAsync(happy: true, sqlitePath: sqlite);
        await BeginAsync(world, HappyEvidence());

        var decision = await ConfirmAsync(world, TerminalizationIntent.Complete, HappyEvidence(), null, CleanProof);

        Assert.True(decision.Accepted, string.Join(",", decision.MissingRequirements));
        Assert.NotNull(decision.Result);
        Assert.Equal("Ship it", decision.Result!.SummaryMarkdown);
        Assert.Equal(["src/Feature.cs"], decision.Result.ChangedFiles);

        await using var restarted = TestRepositories.CreateContext(sqlite, createSchema: false);
        var request = restarted.WorkRequests.Single(r => r.Id == world.RequestId);
        var assignment = restarted.ExecutionAssignments.Single(a => a.RequestId == world.RequestId);
        Assert.Equal(WorkRequestStatus.Completed, request.Status);
        Assert.Equal(ExecutionAssignmentState.Completed, assignment.State);
        Assert.Equal(Now, assignment.TerminalAt);

        var service = new AssignmentTerminalizationService(new FrozenClock(), restarted, new ProjectionNotifier());
        var loaded = await service.GetResultAsync(world.RequestId);
        Assert.NotNull(loaded);
        Assert.Equal(decision.Result.SummaryMarkdown, loaded!.SummaryMarkdown);
    }

    [Theory]
    [InlineData(TerminalizationIntent.Fail, WorkRequestStatus.Failed, ExecutionAssignmentState.Failed)]
    [InlineData(TerminalizationIntent.Cancel, WorkRequestStatus.Cancelled, ExecutionAssignmentState.Cancelled)]
    public async Task Confirm_fail_and_cancel_terminalize_together(
        TerminalizationIntent intent,
        WorkRequestStatus requestStatus,
        ExecutionAssignmentState assignmentState)
    {
        var world = await SeedAsync(happy: true);
        await BeginAsync(world, intent);

        var decision = await ConfirmAsync(world, intent, evidence: null, reason: "stop", CleanProof);

        Assert.True(decision.Accepted, string.Join(",", decision.MissingRequirements));
        Assert.Null(decision.Result);
        Assert.Equal(requestStatus, Request(world).Status);
        Assert.Equal(assignmentState, Assignment(world).State);
        Assert.Equal(Now, Assignment(world).TerminalAt);
        if (intent == TerminalizationIntent.Fail)
        {
            Assert.Empty(world.Db.PendingTerminalizations);
        }
        else
        {
            Assert.Null(Pending(world));
        }

        Assert.Empty(world.Db.RequestResults);
    }

    [Fact]
    public async Task Confirm_without_begin_is_rejected_and_changes_nothing()
    {
        var world = await SeedAsync(happy: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConfirmAsync(world, TerminalizationIntent.Complete, HappyEvidence(), null, CleanProof));

        Assert.Equal(ExecutionAssignmentState.Running, Assignment(world).State);
        Assert.Equal(WorkRequestStatus.Verifying, Request(world).Status);
    }

    [Fact]
    public async Task Exact_retry_returns_the_persisted_terminal_result_without_reopening()
    {
        var world = await SeedAsync(happy: true);
        await BeginAsync(world, HappyEvidence());
        var first = await ConfirmAsync(world, TerminalizationIntent.Complete, HappyEvidence(), null, CleanProof);
        Assert.True(first.Accepted);

        var retriedBegin = await BeginAsync(world, HappyEvidence());
        var retriedConfirm = await ConfirmAsync(world, TerminalizationIntent.Complete, HappyEvidence(), null, CleanProof);

        Assert.True(retriedBegin.Accepted);
        Assert.True(retriedConfirm.Accepted);
        Assert.Equal(first.Result!.SummaryMarkdown, retriedConfirm.Result!.SummaryMarkdown);
        Assert.Equal(first.Result.CreatedAt, retriedConfirm.Result.CreatedAt);
        Assert.Equal(WorkRequestStatus.Completed, Request(world).Status);
        Assert.Equal(ExecutionAssignmentState.Completed, Assignment(world).State);
    }

    [Fact]
    public async Task Accepted_complete_intent_survives_process_restart()
    {
        var sqlite = TestRepositories.CreateSqliteFile();
        var world = await SeedAsync(happy: true, sqlitePath: sqlite);
        Assert.True((await BeginAsync(world, HappyEvidence())).Accepted);
        await world.Db.DisposeAsync();

        await using var restarted = TestRepositories.CreateContext(sqlite, createSchema: false);
        var pending = restarted.PendingTerminalizations.Single(r => r.RequestId == world.RequestId);

        Assert.Equal(world.ProjectId, pending.ProjectId.Value);
        Assert.Equal(world.NodeId, pending.NodeId);
        Assert.Equal(ClaimToken, pending.ClaimToken);
        Assert.Equal(RootSessionId, pending.RootSessionId);
        Assert.Equal(nameof(TerminalizationIntent.Complete), pending.Intent);
        Assert.Equal(Now.UtcTicks, pending.AcceptedAtUtcTicks);
        Assert.Equal(1, pending.Version);
        Assert.NotNull(pending.CompletionEvidenceJson);
        var evidence = JsonSerializer.Deserialize<CompletionEvidence>(
            pending.CompletionEvidenceJson!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("Ship it", evidence!.SummaryMarkdown);
        Assert.Equal(["src/Feature.cs"], evidence.ChangedFiles);
    }

    [Fact]
    public async Task Accepted_fail_intent_replaces_pending_row_and_increments_version()
    {
        var world = await SeedAsync(happy: true);
        Assert.True((await BeginAsync(world, HappyEvidence())).Accepted);
        var first = Pending(world)!;
        Assert.Equal(1, first.Version);
        Assert.Equal(nameof(TerminalizationIntent.Complete), first.Intent);

        var replaced = await world.Service.BeginAsync(
            world.NodeId,
            new ProjectId(world.ProjectId),
            world.RequestId,
            ClaimToken,
            RootSessionId,
            TerminalizationIntent.Fail,
            evidence: null,
            reason: "node lost");

        Assert.True(replaced.Accepted, string.Join(",", replaced.MissingRequirements));
        var pending = Pending(world)!;
        Assert.Equal(nameof(TerminalizationIntent.Fail), pending.Intent);
        Assert.Equal("node lost", pending.Reason);
        Assert.Null(pending.CompletionEvidenceJson);
        Assert.Equal(2, pending.Version);
        Assert.Equal(ClaimToken, pending.ClaimToken);
        Assert.Equal(RootSessionId, pending.RootSessionId);
        Assert.Single(world.Db.PendingTerminalizations);
    }

    [Fact]
    public async Task Rejected_confirmation_retains_the_accepted_pending_row()
    {
        var world = await SeedAsync(happy: true);
        Assert.True((await BeginAsync(world, HappyEvidence())).Accepted);
        var before = Pending(world)!;

        var decision = await ConfirmAsync(
            world,
            TerminalizationIntent.Complete,
            HappyEvidence(),
            null,
            DirtyProof("PendingEvents"));

        Assert.False(decision.Accepted);
        var after = Pending(world)!;
        Assert.Equal(before.Intent, after.Intent);
        Assert.Equal(before.CompletionEvidenceJson, after.CompletionEvidenceJson);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.AcceptedAtUtcTicks, after.AcceptedAtUtcTicks);
        Assert.Equal(ExecutionAssignmentState.Finalizing, Assignment(world).State);
    }

    [Fact]
    public async Task Successful_confirmation_deletes_the_pending_row()
    {
        var sqlite = TestRepositories.CreateSqliteFile();
        var world = await SeedAsync(happy: true, sqlitePath: sqlite);
        Assert.True((await BeginAsync(world, HappyEvidence())).Accepted);
        Assert.True((await ConfirmAsync(world, TerminalizationIntent.Complete, HappyEvidence(), null, CleanProof)).Accepted);
        await world.Db.DisposeAsync();

        await using var restarted = TestRepositories.CreateContext(sqlite, createSchema: false);
        Assert.Empty(restarted.PendingTerminalizations.Where(r => r.RequestId == world.RequestId));
    }


    [Fact]
    public async Task Conflicting_intent_after_terminalization_cannot_reopen()
    {
        var world = await SeedAsync(happy: true);
        await BeginAsync(world, HappyEvidence());
        await ConfirmAsync(world, TerminalizationIntent.Complete, HappyEvidence(), null, CleanProof);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BeginAsync(world, TerminalizationIntent.Fail));

        Assert.Equal(WorkRequestStatus.Completed, Request(world).Status);
        Assert.Equal(ExecutionAssignmentState.Completed, Assignment(world).State);
    }

    [Fact]
    public async Task Foreign_claim_token_is_denied()
    {
        var world = await SeedAsync(happy: true);

        var error = await Assert.ThrowsAsync<AssignmentAuthorizationException>(() =>
            world.Service.BeginAsync(
                world.NodeId, new ProjectId(world.ProjectId), world.RequestId,
                "forged-token", RootSessionId, TerminalizationIntent.Cancel, null, "stop"));

        Assert.Equal(AssignmentAuthorizationCodes.TokenMismatch, error.Code);
        Assert.Equal(ExecutionAssignmentState.Running, Assignment(world).State);
    }

    private static Task<CompletionGateDecision> BeginAsync(World world, CompletionEvidence evidence) =>
        world.Service.BeginAsync(
            world.NodeId, new ProjectId(world.ProjectId), world.RequestId,
            ClaimToken, RootSessionId, TerminalizationIntent.Complete, evidence, reason: null);

    private static Task<CompletionGateDecision> BeginAsync(World world, TerminalizationIntent intent) =>
        world.Service.BeginAsync(
            world.NodeId, new ProjectId(world.ProjectId), world.RequestId,
            ClaimToken, RootSessionId, intent,
            intent == TerminalizationIntent.Complete ? HappyEvidence() : null,
            intent == TerminalizationIntent.Complete ? null : "stop");

    private static Task<CompletionGateDecision> ConfirmAsync(
        World world,
        TerminalizationIntent intent,
        CompletionEvidence? evidence,
        string? reason,
        AssignmentQuiescenceProof proof) =>
        world.Service.ConfirmAsync(
            world.NodeId, new ProjectId(world.ProjectId), world.RequestId,
            ClaimToken, RootSessionId, intent, evidence, reason, proof);

    private static ExecutionAssignment Assignment(World world) =>
        world.Db.ExecutionAssignments.Single(a => a.RequestId == world.RequestId);

    private static PendingTerminalizationRow? Pending(World world) =>
        world.Db.PendingTerminalizations.SingleOrDefault(r => r.RequestId == world.RequestId);

    private static WorkRequest Request(World world) =>
        world.Db.WorkRequests.Single(r => r.Id == world.RequestId);

    private static CompletionEvidence HappyEvidence() => new(
        "Ship it",
        ["src/Feature.cs"],
        [],
        "dotnet-test passed",
        VerificationFingerprint: Fingerprint,
        VerificationPolicyRevision: PolicyRevision);

    private static async Task<World> SeedAsync(
        bool happy,
        bool activeLease = false,
        bool recoveryLease = false,
        bool mutating = false,
        bool verificationPassed = true,
        bool markRunning = true,
        string? sqlitePath = null,
        IReadOnlyList<string>? mandatoryCommandIds = null,
        string runFingerprint = Fingerprint,
        VerificationRunKind runKind = VerificationRunKind.Baseline,
        string reviewRole = "reviewer")
    {
        var context = TestRepositories.CreateContext(sqlitePath ?? TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context);
        var queue = TestRepositories.CreateQueue(context);
        var project = await catalog.RegisterAsync(new RegisterProjectCommand(
            DisplayName: "Fleet",
            DefaultBranch: "main",
            Enabled: true,
            MaxActiveWriteRequests: 2,
            MaxReadOnlyRequests: 4,
            MaxChildAgentsPerRequest: 4,
            RequireCleanStart: true,
            CreateRequestBranch: true,
            CreateRequestCommit: false,
            AutoMerge: false));
        var queued = await queue.EnqueueAsync(
            new ProjectId(project.Id),
            new QueueWorkRequestCommand(
                Kind: WorkRequestKind.Development,
                Priority: RequestPriority.Normal,
                RiskLevel: RiskLevel.Standard,
                Title: "Add feature",
                Prompt: "Implement the feature"));

        var request = context.WorkRequests.Single(r => r.Id == new WorkRequestId(queued.Id));
        request.Start(Now);
        request.BeginPlanning(Now);
        request.BeginExecuting(Now);
        request.BeginReviewing(Now);
        request.BeginVerifying(Now);

        var nodeId = NodeId.New();
        context.FleetNodes.Add(FleetNode.Register(nodeId, "node-term", "1.0.0", "{}", Now));
        var repositoryPath = Path.Combine(Path.GetTempPath(), queued.Id.ToString());
        var binding = WorkspaceBinding.Designate(new ProjectId(project.Id), nodeId, repositoryPath, Now);
        context.WorkspaceBindings.Add(binding);
        var assignment = ExecutionAssignment.Create(
            new WorkRequestId(queued.Id),
            new ProjectId(project.Id),
            binding.Id,
            nodeId,
            repositoryPath,
            project.DefaultBranch,
            binding.ValidationRevision,
            ClaimToken,
            Now,
            TimeSpan.FromMinutes(5));
        assignment.CaptureVerificationPolicy(
            PolicyRevision,
            baselineVersion: "1",
            trustedVerificationProfileId: mandatoryCommandIds?.Contains(ProjectCommandId) == true ? "default" : null,
            trustedVerificationProfileRevision: mandatoryCommandIds?.Contains(ProjectCommandId) == true ? "profile-1" : null,
            JsonSerializer.Serialize(mandatoryCommandIds ?? [BaselineCommandId]));
        if (markRunning)
        {
            assignment.MarkRunning(Now);
        }

        context.ExecutionAssignments.Add(assignment);

        if (happy)
        {
            context.SessionEvents.Add(new SessionEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                NodeId = Guid.NewGuid(),
                ProjectId = project.Id,
                RequestId = queued.Id,
                SessionId = RootSessionId,
                Sequence = 1,
                Type = "request.phase_changed",
                OccurredAtUtcTicks = Now.UtcTicks,
                ReceivedAtUtcTicks = Now.UtcTicks,
                PayloadJson = """{"phase":"plan"}""",
            });

            AddSession(context, project.Id, queued.Id, RootSessionId, parent: null, role: "root", work: AgentWorkState.Verifying, activity: AgentActivity.Idle);
            AddSession(context, project.Id, queued.Id, "impl-1", parent: RootSessionId, role: "implementer", work: AgentWorkState.Completed, activity: AgentActivity.Idle);
            AddSession(
                context,
                project.Id,
                queued.Id,
                "rev-1",
                parent: RootSessionId,
                role: reviewRole,
                work: AgentWorkState.Completed,
                activity: mutating ? AgentActivity.RunningTool : AgentActivity.Idle);

            context.VerificationRuns.Add(new VerificationRunRow
            {
                Id = Guid.NewGuid(),
                RequestId = queued.Id,
                ProfileId = "devfleet-baseline",
                CommandId = BaselineCommandId,
                Status = verificationPassed
                    ? nameof(VerificationRunStatus.Passed)
                    : nameof(VerificationRunStatus.Failed),
                ExitCode = verificationPassed ? 0 : 1,
                StartedAtUtcTicks = Now.UtcTicks,
                CompletedAtUtcTicks = Now.UtcTicks,
                OutputSummary = "ok",
                Mandatory = true,
                Fingerprint = runFingerprint,
                PolicyRevision = PolicyRevision,
                RunKind = runKind.ToString(),
                AttemptId = Guid.NewGuid(),
            });

            var leaseId = Guid.NewGuid();
            var state = activeLease
                ? nameof(ReservationLeaseState.Active)
                : recoveryLease
                    ? nameof(ReservationLeaseState.RecoveryRequired)
                    : nameof(ReservationLeaseState.Released);
            context.ReservationLeases.Add(new ReservationLeaseRow
            {
                Id = leaseId,
                ProjectId = project.Id,
                RequestId = queued.Id,
                OwnerSessionId = "impl-1",
                Reason = "implement",
                FencingToken = 1,
                State = state,
                AcquiredAtUtcTicks = Now.UtcTicks,
                LastRenewedAtUtcTicks = Now.UtcTicks,
                ExpiresAtUtcTicks = Now.AddMinutes(2).UtcTicks,
                ReleasedAtUtcTicks = state == nameof(ReservationLeaseState.Released) ? Now.UtcTicks : null,
                Version = 1,
            });
            context.ReservationScopes.Add(new ReservationScopeRow
            {
                Id = Guid.NewGuid(),
                LeaseId = leaseId,
                Kind = (int)ReservationScopeKind.File,
                Path = "src/Feature.cs",
            });
        }

        await context.SaveChangesAsync();
        return new World(
            context,
            new AssignmentTerminalizationService(new FrozenClock(), context, new ProjectionNotifier()),
            project.Id,
            new WorkRequestId(queued.Id),
            nodeId);
    }

    private static void AddSession(
        ControlPlaneDbContext db,
        Guid projectId,
        Guid requestId,
        string id,
        string? parent,
        string role,
        AgentWorkState work,
        AgentActivity activity)
    {
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = id,
            ProjectId = projectId,
            RequestId = requestId,
            ParentSessionId = parent,
            AgentName = id,
            Role = role,
            Runtime = "pi",
            Model = "codex/gpt-5.6-sol",
            Liveness = nameof(AgentLiveness.Online),
            Activity = activity.ToString(),
            Attention = nameof(AgentAttention.None),
            WorkState = work.ToString(),
            StatusReason = "seed",
            StartedAtUtcTicks = Now.UtcTicks,
            LastSequence = 1,
            Version = 1,
        });
    }

    private sealed record World(
        ControlPlaneDbContext Db,
        AssignmentTerminalizationService Service,
        Guid ProjectId,
        WorkRequestId RequestId,
        NodeId NodeId);
}
