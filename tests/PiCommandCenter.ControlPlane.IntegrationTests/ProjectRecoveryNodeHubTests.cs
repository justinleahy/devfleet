using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Recovery;
using PiCommandCenter.ControlPlane.Hubs;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Recovery;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

/// <summary>
/// SignalR recovery ingress: authenticated node identity is forwarded to the
/// coordinator; payload identity is never trusted; protocol bounds reject
/// malformed evidence before coordination.
/// </summary>
public sealed class ProjectRecoveryNodeHubTests : IClassFixture<ControlPlaneFixture>, IDisposable
{
    private readonly ControlPlaneFixture _fixture;
    private readonly HubConnection _owner;
    private readonly HubConnection _foreign;

    public ProjectRecoveryNodeHubTests(ControlPlaneFixture fixture)
    {
        _fixture = fixture;
        _owner = fixture.CreateNodeHubConnection(fixture.AuthenticatedNodeId);
        _foreign = fixture.CreateNodeHubConnection(fixture.SecondaryNodeId);
        _owner.StartAsync().GetAwaiter().GetResult();
        _foreign.StartAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _foreign.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Owner_progress_reaches_coordinator_and_foreign_proof_is_rejected()
    {
        var seed = await SeedRecoveryAsync();
        var observedAt = DateTimeOffset.UtcNow;
        await _owner.InvokeAsync(
            "ReportRecoveryProgress",
            Progress(seed, observedAt, Stage: "Stopping agents"));

        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var operation = await db.Set<RecoveryOperationRow>().SingleAsync(row => row.Id == seed.RecoveryId);
            Assert.Equal("Stopping agents", operation.Stage);
            var audit = await db.Set<RecoveryAuditFactRow>()
                .Where(row => row.OperationId == seed.RecoveryId)
                .ToListAsync();
            Assert.Contains(audit, row => row.Kind == "progress");
        }

        var foreignDecision = await _foreign.InvokeAsync<RecoveryProofDecisionMessage>(
            "ReportRecoveryProof",
            Proof(seed, observedAt));

        Assert.False(foreignDecision.Accepted);
        Assert.Equal([RecoveryReasonCodes.RecoveryTargetChanged], foreignDecision.MissingRequirements);

        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            Assert.Empty(await db.Set<RecoveryAuditFactRow>()
                .Where(row => row.OperationId == seed.RecoveryId && row.Kind == "proof-accepted")
                .ToListAsync());
            var target = await db.Set<RecoveryTargetRow>().SingleAsync(row => row.OperationId == seed.RecoveryId);
            Assert.Null(target.Outcome);
        }

        var ownerDecision = await _owner.InvokeAsync<RecoveryProofDecisionMessage>(
            "ReportRecoveryProof",
            Proof(seed, observedAt));

        Assert.DoesNotContain(RecoveryReasonCodes.RecoveryTargetChanged, ownerDecision.MissingRequirements);
    }

    [Fact]
    public async Task Malformed_recovery_payloads_are_rejected_before_coordination()
    {
        var seed = await SeedRecoveryAsync();
        var observedAt = DateTimeOffset.UtcNow;
        var validProgress = Progress(seed, observedAt, Stage: "ok");
        var validProof = Proof(seed, observedAt);

        await Assert.ThrowsAnyAsync<HubException>(() =>
            _owner.InvokeAsync("ReportRecoveryProgress", validProgress with { ClaimToken = "" }));
        await Assert.ThrowsAnyAsync<HubException>(() =>
            _owner.InvokeAsync(
                "ReportRecoveryProgress",
                validProgress with
                {
                    ClaimToken = new string('t', NodeTransportLimits.MaxRecoveryClaimTokenLength + 1),
                }));
        await Assert.ThrowsAnyAsync<HubException>(() =>
            _owner.InvokeAsync(
                "ReportRecoveryProgress",
                validProgress with
                {
                    Stage = new string('s', NodeTransportLimits.MaxRecoveryStageLength + 1),
                }));
        await Assert.ThrowsAnyAsync<HubException>(() =>
            _owner.InvokeAsync(
                "ReportRecoveryProgress",
                validProgress with
                {
                    ReasonCodes = Enumerable.Repeat("code", NodeTransportLimits.MaxRecoveryReasonCodes + 1).ToArray(),
                }));
        await Assert.ThrowsAnyAsync<HubException>(() =>
            _owner.InvokeAsync(
                "ReportRecoveryProgress",
                validProgress with
                {
                    ReasonCodes = [new string('c', NodeTransportLimits.MaxRecoveryReasonCodeLength + 1)],
                }));
        await Assert.ThrowsAnyAsync<HubException>(() =>
            _owner.InvokeAsync(
                "ReportRecoveryProgress",
                validProgress with { RecoveryId = Guid.Empty }));
        await Assert.ThrowsAnyAsync<HubException>(() =>
            _owner.InvokeAsync(
                "ReportRecoveryProof",
                validProof with
                {
                    ProcessIdentities = Enumerable.Range(0, NodeTransportLimits.MaxRecoveryProcessIdentities + 1)
                        .Select(i => new RecoveryProcessIdentityMessage(i, observedAt, null, false))
                        .ToArray(),
                }));
        await Assert.ThrowsAnyAsync<HubException>(() =>
            _owner.InvokeAsync(
                "ReportRecoveryProof",
                validProof with
                {
                    ReservationDispositions = Enumerable.Range(0, NodeTransportLimits.MaxRecoveryReservationDispositions + 1)
                        .Select(_ => new RecoveryReservationDispositionMessage(Guid.NewGuid(), "released", null))
                        .ToArray(),
                }));
        await Assert.ThrowsAnyAsync<HubException>(() =>
            _owner.InvokeAsync(
                "ReportRecoveryProof",
                validProof with
                {
                    Repository = new RecoveryRepositoryStatusMessage(
                        true,
                        new string('h', NodeTransportLimits.MaxRecoverySummaryLength + 1),
                        "main",
                        "clean",
                        "clean",
                        Zero(),
                        [],
                        observedAt),
                }));

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        Assert.Empty(await db.Set<RecoveryAuditFactRow>()
            .Where(row => row.OperationId == seed.RecoveryId)
            .ToListAsync());
    }

    private async Task<Seed> SeedRecoveryAsync()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var now = DateTimeOffset.UtcNow;
        var nodeId = new NodeId(_fixture.AuthenticatedNodeId);
        var node = FleetNode.Register(
            nodeId,
            $"recovery-hub-{nodeId.Value:N}",
            "1.0.0",
            "{}",
            now);
        var project = Project.Register(
            "Recovery hub " + Guid.NewGuid().ToString("N")[..6],
            "main",
            enabled: true,
            maxActiveWriteRequests: 2,
            maxReadOnlyRequests: 4,
            maxChildAgentsPerRequest: 2,
            requireCleanStart: false,
            createRequestBranch: false,
            createRequestCommit: false,
            autoMerge: false,
            now);
        var repositoryPath = _fixture.CreateGitRepository();
        var binding = WorkspaceBinding.Designate(project.Id, nodeId, repositoryPath, now);
        Assert.True(binding.ApplyValidationResult(
            nodeId,
            binding.ValidationRevision,
            WorkspaceBindingStatus.Valid,
            WorkspaceBinding.ValidValidationCode,
            "Seeded for recovery hub tests.",
            repositoryPath,
            now));
        var request = WorkRequest.Enqueue(
            project.Id,
            WorkRequestKind.Development,
            RequestPriority.Normal,
            RiskLevel.Standard,
            "Recovery hub",
            "Exercise recovery hub ingress.",
            now);
        request.Start(now);
        request.BeginCancelling(now);
        var claimToken = "recovery-hub-" + Guid.NewGuid().ToString("N");
        var assignment = ExecutionAssignment.Create(
            request.Id,
            project.Id,
            binding.Id,
            nodeId,
            binding.CanonicalRepositoryPath!,
            project.DefaultBranch,
            binding.ValidationRevision,
            claimToken,
            now,
            TimeSpan.FromMinutes(5));
        assignment.BeginCancelling(now);
        var recoveryId = Guid.NewGuid();
        if (!await db.FleetNodes.AnyAsync(candidate => candidate.Id == nodeId))
        {
            db.FleetNodes.Add(node);
        }
        db.Projects.Add(project);
        db.WorkspaceBindings.Add(binding);
        db.WorkRequests.Add(request);
        db.ExecutionAssignments.Add(assignment);
        db.Set<RecoveryOperationRow>().Add(new RecoveryOperationRow
        {
            Id = recoveryId,
            ProjectId = project.Id.Value,
            Status = nameof(RecoveryOperationStatus.Running),
            Attempt = 1,
            InventoryRevision = "rev",
            Reason = "stuck",
            Actor = "operator",
            Stage = "queued",
            CreatedAtUtcTicks = now.UtcTicks,
            UpdatedAtUtcTicks = now.UtcTicks,
            LastProgressUtcTicks = now.AddSeconds(-5).UtcTicks,
            DeadlineUtcTicks = now.AddMinutes(5).UtcTicks,
            Version = 1,
        });
        db.Set<RecoveryTargetRow>().Add(new RecoveryTargetRow
        {
            Id = Guid.NewGuid(),
            OperationId = recoveryId,
            RequestId = request.Id.Value,
            CapturedVersion = 1,
            CapturedState = nameof(ExecutionAssignmentState.Running),
            BindingRevision = assignment.BindingValidationRevisionSnapshot,
        });
        await db.SaveChangesAsync();
        return new Seed(
            recoveryId,
            project.Id.Value,
            request.Id.Value,
            claimToken,
            assignment.BindingValidationRevisionSnapshot);
    }

    private static AssignmentRecoveryProgressMessage Progress(Seed seed, DateTimeOffset observedAt, string? Stage) =>
        new(
            seed.RecoveryId,
            1,
            seed.ProjectId,
            seed.RequestId,
            seed.ClaimToken,
            seed.BindingRevision,
            observedAt,
            Stage,
            Zero(),
            Zero(),
            Zero(),
            Zero(),
            Zero(),
            []);

    private static AssignmentRecoveryProofMessage Proof(Seed seed, DateTimeOffset observedAt) =>
        new(
            seed.RecoveryId,
            1,
            seed.ProjectId,
            seed.RequestId,
            seed.ClaimToken,
            seed.BindingRevision,
            observedAt,
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
            Repository: new RecoveryRepositoryStatusMessage(
                true,
                "abc",
                "main",
                "clean",
                "clean",
                Zero(),
                [],
                observedAt));

    private static RecoveryKnownCountMessage Zero() => new(0, null);

    private sealed record Seed(
        Guid RecoveryId,
        Guid ProjectId,
        Guid RequestId,
        string ClaimToken,
        long BindingRevision);
}
