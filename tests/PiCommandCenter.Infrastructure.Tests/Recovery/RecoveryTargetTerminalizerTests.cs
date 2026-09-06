using System.Text.Json;
using PiCommandCenter.Application.Completion;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Completion;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Recovery;

namespace PiCommandCenter.Infrastructure.Tests.Recovery;

public sealed class RecoveryTargetTerminalizerTests
{
    private const string ClaimToken = "assignment-token-0123456789abcdef0123456789abcdef";
    private const string RootSessionId = "root-session-id";
    private const string FailReason = "agent failed";

    private readonly string _sqlitePath = TestRepositories.CreateSqliteFile();
    private readonly FakeTimeProvider _clock = TestNodes.Clock();

    [Fact]
    public async Task Cancelling_target_confirms_cancel_with_recovery_reason()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Cancelling);
        await db.SaveChangesAsync();
        var recorder = new RecordingTerminalizationService();
        var proof = ValidProof(world);

        var decision = await CreateSut(db, recorder)
            .TerminalizeAsync(proof, TerminalizationIntent.Cancel);

        Assert.True(decision.Accepted);
        var call = Assert.Single(recorder.Confirms);
        Assert.Equal(TerminalizationIntent.Cancel, call.Intent);
        Assert.Equal(RecoveryTargetTerminalizer.RecoveryCancelReason, call.Reason);
        Assert.Null(call.RootSessionId);
        Assert.Null(call.Evidence);
        AssertKnownZero(call.Proof, proof.ObservedAt);
        Assert.Equal(world.Node.Id, call.NodeId);
        Assert.Equal(world.Project.Id, call.ProjectId);
        Assert.Equal(world.Request.Id, call.RequestId);
        Assert.Equal(ClaimToken, call.ClaimToken);
    }

    [Fact]
    public async Task Finalizing_complete_reuses_persisted_root_evidence_and_reason()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Finalizing);
        var evidence = CompleteEvidence();
        SeedPending(db, world, TerminalizationIntent.Complete, evidence, reason: null, RootSessionId);
        await db.SaveChangesAsync();
        var recorder = new RecordingTerminalizationService();
        var proof = ValidProof(world);

        var decision = await CreateSut(db, recorder)
            .TerminalizeAsync(proof, TerminalizationIntent.Complete);

        Assert.True(decision.Accepted);
        var call = Assert.Single(recorder.Confirms);
        Assert.Equal(TerminalizationIntent.Complete, call.Intent);
        Assert.Equal(RootSessionId, call.RootSessionId);
        Assert.Equivalent(evidence, call.Evidence, strict: true);
        Assert.Null(call.Reason);
        AssertKnownZero(call.Proof, proof.ObservedAt);
    }

    [Fact]
    public async Task Finalizing_fail_reuses_persisted_reason_and_root_session()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Finalizing);
        SeedPending(db, world, TerminalizationIntent.Fail, evidence: null, FailReason, RootSessionId);
        await db.SaveChangesAsync();
        var recorder = new RecordingTerminalizationService();
        var proof = ValidProof(world);

        var decision = await CreateSut(db, recorder)
            .TerminalizeAsync(proof, TerminalizationIntent.Fail);

        Assert.True(decision.Accepted);
        var call = Assert.Single(recorder.Confirms);
        Assert.Equal(TerminalizationIntent.Fail, call.Intent);
        Assert.Equal(RootSessionId, call.RootSessionId);
        Assert.Null(call.Evidence);
        Assert.Equal(FailReason, call.Reason);
        AssertKnownZero(call.Proof, proof.ObservedAt);
    }

    [Fact]
    public async Task Missing_pending_intent_rejects_without_confirming()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Finalizing);
        world.Request.BeginPlanning(_clock.GetUtcNow());
        world.Request.BeginExecuting(_clock.GetUtcNow());
        world.Request.BeginReviewing(_clock.GetUtcNow());
        world.Request.BeginVerifying(_clock.GetUtcNow());
        await db.SaveChangesAsync();
        var recorder = new RecordingTerminalizationService();

        var decision = await CreateSut(db, recorder)
            .TerminalizeAsync(ValidProof(world), TerminalizationIntent.Complete);

        Assert.False(decision.Accepted);
        Assert.Equal([RecoveryReasonCodes.RecoveryTargetChanged], decision.MissingRequirements);
        Assert.Empty(recorder.Confirms);
    }

    [Fact]
    public async Task Mismatched_pending_intent_rejects_without_confirming()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Finalizing);
        SeedPending(db, world, TerminalizationIntent.Fail, evidence: null, FailReason, RootSessionId);
        await db.SaveChangesAsync();
        var recorder = new RecordingTerminalizationService();

        var decision = await CreateSut(db, recorder)
            .TerminalizeAsync(ValidProof(world), TerminalizationIntent.Complete);

        Assert.False(decision.Accepted);
        Assert.Empty(recorder.Confirms);
    }

    [Fact]
    public async Task Mismatched_claim_token_on_pending_row_rejects()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Finalizing);
        SeedPending(
            db,
            world,
            TerminalizationIntent.Complete,
            CompleteEvidence(),
            reason: null,
            RootSessionId,
            claimToken: "other-token-0123456789abcdef0123456789abcdefab");
        await db.SaveChangesAsync();
        var recorder = new RecordingTerminalizationService();

        var decision = await CreateSut(db, recorder)
            .TerminalizeAsync(ValidProof(world), TerminalizationIntent.Complete);

        Assert.False(decision.Accepted);
        Assert.Empty(recorder.Confirms);
    }

    private RecoveryTargetTerminalizer CreateSut(
        ControlPlaneDbContext db,
        IAssignmentTerminalizationService terminalization) =>
        new(db, terminalization);

    private ControlPlaneDbContext CreateContext() => TestRepositories.CreateContext(_sqlitePath);

    private World SeedAssignment(ControlPlaneDbContext db, ExecutionAssignmentState state)
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
        return new World(node, project, request, assignment, binding.ValidationRevision);
    }

    private void SeedPending(
        ControlPlaneDbContext db,
        World world,
        TerminalizationIntent intent,
        CompletionEvidence? evidence,
        string? reason,
        string? rootSessionId,
        string? claimToken = null)
    {
        db.PendingTerminalizations.Add(new PendingTerminalizationRow
        {
            RequestId = world.Request.Id,
            ProjectId = world.Project.Id,
            NodeId = world.Node.Id,
            ClaimToken = claimToken ?? ClaimToken,
            RootSessionId = rootSessionId,
            Intent = intent.ToString(),
            CompletionEvidenceJson = evidence is null
                ? null
                : JsonSerializer.Serialize(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Reason = reason,
            AcceptedAtUtcTicks = _clock.GetUtcNow().UtcTicks,
            Version = 1,
        });
    }

    private AssignmentRecoveryProofMessage ValidProof(World world) =>
        new(
            Guid.NewGuid(),
            Attempt: 1,
            world.Project.Id.Value,
            world.Request.Id.Value,
            ClaimToken,
            world.BindingRevision,
            _clock.GetUtcNow(),
            AdmissionClosed: true,
            new RecoveryKnownCountMessage(0, null),
            new RecoveryKnownCountMessage(0, null),
            new RecoveryKnownCountMessage(0, null),
            new RecoveryKnownCountMessage(0, null),
            new RecoveryKnownCountMessage(0, null),
            EventAcknowledgementPosition: 0,
            EventAcknowledgementUnknownReasonCode: null,
            ProcessIdentities: [],
            ReservationDispositions: [],
            Repository: new RecoveryRepositoryStatusMessage(
                true,
                "abc",
                "main",
                "clean",
                "clean",
                new RecoveryKnownCountMessage(0, null),
                [],
                _clock.GetUtcNow()));

    private static CompletionEvidence CompleteEvidence() =>
        new(
            "done",
            ["src/a.cs"],
            [],
            "verified",
            RequestBranch: "feat",
            CheckpointCommitId: "abc123",
            VerificationFingerprint: "fp",
            VerificationPolicyRevision: "rev-1");

    private static void AssertKnownZero(AssignmentQuiescenceProof proof, DateTimeOffset observedAt)
    {
        Assert.True(proof.AdmissionClosed);
        Assert.Equal(0, proof.ActiveChildren);
        Assert.Equal(0, proof.ActiveOperations);
        Assert.Equal(0, proof.ActiveProcesses);
        Assert.Equal(0, proof.PendingEvents);
        Assert.Equal(0, proof.ActiveReservations);
        Assert.True(proof.RepositoryInspected);
        Assert.Equal(observedAt, proof.ObservedAt);
    }

    private sealed class RecordingTerminalizationService : IAssignmentTerminalizationService
    {
        public List<ConfirmCall> Confirms { get; } = [];

        public Task<CompletionGateDecision> BeginAsync(
            NodeId nodeId,
            ProjectId projectId,
            WorkRequestId requestId,
            string claimToken,
            string? rootSessionId,
            TerminalizationIntent intent,
            CompletionEvidence? evidence,
            string? reason,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CompletionGateDecision> ConfirmAsync(
            NodeId nodeId,
            ProjectId projectId,
            WorkRequestId requestId,
            string claimToken,
            string? rootSessionId,
            TerminalizationIntent intent,
            CompletionEvidence? evidence,
            string? reason,
            AssignmentQuiescenceProof proof,
            CancellationToken cancellationToken = default)
        {
            Confirms.Add(new ConfirmCall(
                nodeId, projectId, requestId, claimToken, rootSessionId, intent, evidence, reason, proof));
            return Task.FromResult(new CompletionGateDecision(true, [], null));
        }

        public Task<RequestResultDto?> GetResultAsync(
            WorkRequestId requestId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed record ConfirmCall(
        NodeId NodeId,
        ProjectId ProjectId,
        WorkRequestId RequestId,
        string ClaimToken,
        string? RootSessionId,
        TerminalizationIntent Intent,
        CompletionEvidence? Evidence,
        string? Reason,
        AssignmentQuiescenceProof Proof);

    private sealed record World(
        FleetNode Node,
        Project Project,
        WorkRequest Request,
        ExecutionAssignment Assignment,
        long BindingRevision);
}
