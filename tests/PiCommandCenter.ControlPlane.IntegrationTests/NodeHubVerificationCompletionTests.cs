using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.ControlPlane.Security;
using PiCommandCenter.Application.Completion;
using PiCommandCenter.ControlPlane.Hubs;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Completion;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Reservations;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Domain.Verification;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Reservations;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

/// <summary>
/// SignalR + HTTP contract for verification recording and objective completion.
/// </summary>
public sealed class NodeHubVerificationCompletionTests : IClassFixture<ControlPlaneFixture>, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ControlPlaneFixture _fixture;
    private readonly HubConnection _connection;
    private readonly Guid _nodeId;

    public NodeHubVerificationCompletionTests(ControlPlaneFixture fixture)
    {
        _fixture = fixture;
        _nodeId = fixture.AuthenticatedNodeId;
        _connection = fixture.CreateNodeHubConnection(_nodeId);
        _connection.StartAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Fact]
    public async Task RecordVerification_then_EvaluateCompletion_preserves_complete_missing_list()
    {
        await RegisterNodeAsync();
        var (projectId, requestId, claimToken, rootSessionId) = await SeedRequestAsync(advanceToVerifying: false);

        var fail = await _connection.InvokeAsync<VerificationRunResultMessage>(
            "RecordVerification",
            Run(projectId, requestId, claimToken, rootSessionId, VerificationRunStatus.Failed, exitCode: 1, mandatory: true));
        Assert.Equal((int)VerificationRunStatus.Failed, fail.Status);
        Assert.NotEqual(Guid.Empty, fail.Id);

        var pass = await _connection.InvokeAsync<VerificationRunResultMessage>(
            "RecordVerification",
            Run(projectId, requestId, claimToken, rootSessionId, VerificationRunStatus.Passed, exitCode: 0, mandatory: true));
        Assert.Equal((int)VerificationRunStatus.Passed, pass.Status);

        var decision = await _connection.InvokeAsync<CompletionGateDecisionMessage>(
            "BeginTerminalization",
            new BeginTerminalizationMessage(
                Guid.NewGuid(),
                projectId,
                requestId,
                claimToken,
                rootSessionId,
                TerminalizationIntent.Complete,
                new CompletionEvidenceMessage("", null, [], ""),
                Reason: null));

        Assert.False(decision.Accepted);
        Assert.Null(decision.Result);
        Assert.Contains(CompletionRequirements.ResultSummary, decision.MissingRequirements);
        Assert.Contains(CompletionRequirements.DiffCaptured, decision.MissingRequirements);
        Assert.Contains(CompletionRequirements.PlanEvent, decision.MissingRequirements);
        Assert.Contains(CompletionRequirements.ImplementationChild, decision.MissingRequirements);
        Assert.Contains(CompletionRequirements.IndependentReviewer, decision.MissingRequirements);
        Assert.Contains(CompletionRequirements.MandatoryVerification, decision.MissingRequirements);
        Assert.Equal(
            decision.MissingRequirements.Distinct(StringComparer.Ordinal).Count(),
            decision.MissingRequirements.Count);
    }

    [Fact]
    public async Task RecordVerification_rejects_missing_correlation_and_oversized_output()
    {
        await RegisterNodeAsync();
        var (projectId, requestId, claimToken, sessionId) = await SeedRequestAsync(advanceToVerifying: false);

        await Assert.ThrowsAnyAsync<HubException>(() => _connection.InvokeAsync<VerificationRunResultMessage>(
            "RecordVerification",
            Run(projectId, requestId, claimToken, sessionId, VerificationRunStatus.Passed, 0, true) with { CorrelationId = Guid.Empty }));

        await Assert.ThrowsAnyAsync<HubException>(() => _connection.InvokeAsync<VerificationRunResultMessage>(
            "RecordVerification",
            Run(projectId, requestId, claimToken, sessionId, VerificationRunStatus.Passed, 0, true) with
            {
                OutputSummary = new string('x', NodeTransportLimits.MaxVerificationOutputBytes + 1),
            }));
    }

    [Fact]
    public async Task Finalizing_inventory_reconciles_between_begin_and_confirm()
    {
        await RegisterNodeAsync();
        var (projectId, requestId, claimToken, rootSessionId) = await SeedReadyWorldAsync();
        await _connection.InvokeAsync<VerificationRunResultMessage>(
            "RecordVerification",
            Run(
                projectId,
                requestId,
                claimToken,
                rootSessionId,
                VerificationRunStatus.Passed,
                exitCode: 0,
                mandatory: true));
        var evidence = new CompletionEvidenceMessage(
            "Shipped the change.",
            ["src/a.cs"],
            [],
            "all green");

        var begin = await _connection.InvokeAsync<CompletionGateDecisionMessage>(
            "BeginTerminalization",
            new BeginTerminalizationMessage(
                Guid.NewGuid(),
                projectId,
                requestId,
                claimToken,
                rootSessionId,
                TerminalizationIntent.Complete,
                evidence,
                Reason: null));
        Assert.True(begin.Accepted);

        var finalizing = await LoadAssignmentMessageAsync(requestId);
        Assert.Equal("Finalizing", finalizing.State);
        var reconciliation = await _connection.InvokeAsync<ReconcileAssignmentsResultMessage>(
            "ReconcileAssignments",
            new ReconcileAssignmentsMessage(
                _nodeId,
                LeaseSeconds: 300,
                [
                    new ExecutionAssignmentInventoryItemMessage(
                        finalizing,
                        AssignmentSupervisorState.Running,
                        RepositoryKnown: true,
                        PendingEventCount: 0),
                ]));
        var reconciled = Assert.Single(
            reconciliation.Assignments,
            candidate => candidate.RequestId == requestId);
        Assert.Equal(AssignmentReconciliationDisposition.Resume, reconciled.Disposition);
        Assert.Equal("Finalizing", reconciled.Assignment?.State);

        var confirmed = await _connection.InvokeAsync<CompletionGateDecisionMessage>(
            "ConfirmTerminalization",
            new ConfirmTerminalizationMessage(
                Guid.NewGuid(),
                projectId,
                requestId,
                claimToken,
                rootSessionId,
                TerminalizationIntent.Complete,
                evidence,
                Reason: null,
                Proof: new AssignmentQuiescenceProofMessage(
                    AdmissionClosed: true,
                    ActiveChildren: 0,
                    ActiveOperations: 0,
                    ActiveProcesses: 0,
                    PendingEvents: 0,
                    ActiveReservations: 0,
                    RepositoryInspected: true,
                    ObservedAt: DateTimeOffset.UtcNow)));

        Assert.True(confirmed.Accepted);
        Assert.NotNull(confirmed.Result);
    }

    [Fact]
    public async Task Accepted_result_and_events_survive_host_restart()
    {
        await RegisterNodeAsync();
        var (projectId, requestId, claimToken, rootSessionId) = await SeedReadyWorldAsync();

        var recorded = await _connection.InvokeAsync<VerificationRunResultMessage>(
            "RecordVerification",
            Run(projectId, requestId, claimToken, rootSessionId, VerificationRunStatus.Passed, 0, true));
        Assert.Equal("Passed", recorded.StatusName);

        var begin = await _connection.InvokeAsync<CompletionGateDecisionMessage>(
            "BeginTerminalization",
            new BeginTerminalizationMessage(
                Guid.NewGuid(),
                projectId,
                requestId,
                claimToken,
                rootSessionId,
                TerminalizationIntent.Complete,
                new CompletionEvidenceMessage(
                    "Shipped the change.",
                    ["src/a.cs"],
                    [],
                    "all green",
                    "pi/request-checkpoint",
                    "abc123checkpoint"),
                Reason: null));
        Assert.True(begin.Accepted);
        Assert.Null(begin.Result);

        var decision = await _connection.InvokeAsync<CompletionGateDecisionMessage>(
            "ConfirmTerminalization",
            new ConfirmTerminalizationMessage(
                Guid.NewGuid(),
                projectId,
                requestId,
                claimToken,
                rootSessionId,
                TerminalizationIntent.Complete,
                new CompletionEvidenceMessage(
                    "Shipped the change.",
                    ["src/a.cs"],
                    [],
                    "all green",
                    "pi/request-checkpoint",
                    "abc123checkpoint"),
                Reason: null,
                Proof: new AssignmentQuiescenceProofMessage(
                    AdmissionClosed: true,
                    ActiveChildren: 0,
                    ActiveOperations: 0,
                    ActiveProcesses: 0,
                    PendingEvents: 0,
                    ActiveReservations: 0,
                    RepositoryInspected: true,
                    ObservedAt: DateTimeOffset.UtcNow)));

        // An exact retry returns the persisted terminal result without reopening.
        var retried = await _connection.InvokeAsync<CompletionGateDecisionMessage>(
            "ConfirmTerminalization",
            new ConfirmTerminalizationMessage(
                Guid.NewGuid(),
                projectId,
                requestId,
                claimToken,
                rootSessionId,
                TerminalizationIntent.Complete,
                new CompletionEvidenceMessage(
                    "Shipped the change.",
                    ["src/a.cs"],
                    [],
                    "all green",
                    "pi/request-checkpoint",
                    "abc123checkpoint"),
                Reason: null,
                Proof: new AssignmentQuiescenceProofMessage(
                    AdmissionClosed: true,
                    ActiveChildren: 0,
                    ActiveOperations: 0,
                    ActiveProcesses: 0,
                    PendingEvents: 0,
                    ActiveReservations: 0,
                    RepositoryInspected: true,
                    ObservedAt: DateTimeOffset.UtcNow)));
        Assert.True(retried.Accepted);
        Assert.NotNull(retried.Result);

        Assert.True(decision.Accepted);
        Assert.Empty(decision.MissingRequirements);
        Assert.NotNull(decision.Result);
        Assert.Equal("Shipped the change.", decision.Result.SummaryMarkdown);
        Assert.Equal("pi/request-checkpoint", decision.Result.RequestBranch);
        Assert.Equal("abc123checkpoint", decision.Result.CheckpointCommitId);

        using var client = _fixture.CreateClient();
        var before = await client.GetFromJsonAsync<RequestResultDto>($"/api/requests/{requestId}/result", Json);
        Assert.NotNull(before);
        Assert.Equal(requestId, before.RequestId);
        Assert.Equal("pi/request-checkpoint", before.RequestBranch);
        Assert.Equal("abc123checkpoint", before.CheckpointCommitId);

        var eventsBefore = await client.GetFromJsonAsync<JsonElement>($"/api/requests/{requestId}/events", Json);
        Assert.True(eventsBefore.GetProperty("events").GetArrayLength() >= 1);

        await using var restarted = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ControlPlane", $"Data Source={_fixture.SqlitePath}");
            builder.UseSetting("Projects:ApprovedRoots:0", _fixture.ApprovedRoot);
            builder.UseTestAuthFiles(_fixture.PasswordFile, _fixture.CredentialDirectory);
        });
        using var restartedClient = _fixture.CreateAuthenticatedClient(restarted);
        var after = await restartedClient.GetFromJsonAsync<RequestResultDto>($"/api/requests/{requestId}/result", Json);
        Assert.NotNull(after);
        Assert.Equal(before.SummaryMarkdown, after.SummaryMarkdown);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.RequestBranch, after.RequestBranch);
        Assert.Equal(before.CheckpointCommitId, after.CheckpointCommitId);

        var missing = await restartedClient.GetAsync($"/api/requests/{Guid.NewGuid()}/result");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private async Task<ExecutionAssignmentMessage> LoadAssignmentMessageAsync(Guid requestId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var assignment = await db.ExecutionAssignments.SingleAsync(
            candidate => candidate.RequestId == new WorkRequestId(requestId));
        var request = await db.WorkRequests.SingleAsync(
            candidate => candidate.Id == assignment.RequestId);
        var project = await db.Projects.SingleAsync(
            candidate => candidate.Id == assignment.ProjectId);
        return new ExecutionAssignmentMessage(
            assignment.RequestId.Value,
            assignment.ProjectId.Value,
            assignment.WorkspaceBindingId.Value,
            assignment.NodeIdSnapshot.Value,
            assignment.CanonicalRepositoryPathSnapshot,
            assignment.DefaultBranchSnapshot,
            assignment.BindingValidationRevisionSnapshot,
            assignment.State.ToString(),
            assignment.ClaimToken,
            assignment.AssignedAt,
            assignment.LeaseExpiresAt,
            request.Title,
            request.Prompt,
            request.Kind.ToString(),
            request.RiskLevel.ToString(),
            project.CreateRequestBranch,
            project.CreateRequestCommit);
    }

    private async Task RegisterNodeAsync() => _ = await _connection.InvokeAsync<NodeDto>(
        "Register", new NodeRegistrationMessage(_nodeId, "pi-hub-verify", "1.0.0", "{}"));

    private static VerificationRunMessage Run(
        Guid projectId,
        Guid requestId,
        string claimToken,
        string sessionId,
        VerificationRunStatus status,
        int? exitCode,
        bool mandatory) => new(
        Guid.NewGuid(),
        projectId,
        requestId,
        claimToken,
        sessionId,
        Guid.Empty,
        "default",
        "dotnet-test",
        (int)status,
        status.ToString(),
        exitCode,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        status == VerificationRunStatus.Passed ? "ok" : "fail",
        null,
        mandatory);

    private async Task<(Guid ProjectId, Guid RequestId, string ClaimToken, string RootSessionId)> SeedRequestAsync(
        bool advanceToVerifying)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var now = DateTimeOffset.UtcNow;
        var nodeId = new NodeId(_nodeId);
        var project = Project.Register(
            "Verify project " + Guid.NewGuid().ToString("N")[..6],
            "main", enabled: true, maxActiveWriteRequests: 2, maxReadOnlyRequests: 4,
            maxChildAgentsPerRequest: 2, requireCleanStart: false, createRequestBranch: false,
            createRequestCommit: false, autoMerge: false, now);
        var request = WorkRequest.Enqueue(project.Id, WorkRequestKind.Development, RequestPriority.Normal,
            RiskLevel.Standard, "Verify request", "Do verify work", now);
        request.Start(now);
        if (advanceToVerifying)
        {
            request.BeginPlanning(now);
            request.BeginExecuting(now);
            request.BeginReviewing(now);
            request.BeginVerifying(now);
        }

        var repositoryPath = _fixture.CreateGitRepository();
        var binding = WorkspaceBinding.Designate(project.Id, nodeId, repositoryPath, now);
        Assert.True(binding.ApplyValidationResult(
            nodeId,
            binding.ValidationRevision,
            WorkspaceBindingStatus.Valid,
            WorkspaceBinding.ValidValidationCode,
            "Seeded for verification hub tests.",
            repositoryPath,
            now));
        var claimToken = "verification-hub-" + Guid.NewGuid().ToString("N");
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
        var rootSessionId = "root-" + request.Id.Value.ToString("N")[..8];

        db.Projects.Add(project);
        db.WorkspaceBindings.Add(binding);
        db.WorkRequests.Add(request);
        db.ExecutionAssignments.Add(assignment);
        SeedSession(db, project.Id.Value, request.Id.Value, rootSessionId, null, "root", AgentWorkState.Verifying);
        await db.SaveChangesAsync();
        return (project.Id.Value, request.Id.Value, claimToken, rootSessionId);
    }

    private async Task<(Guid ProjectId, Guid RequestId, string ClaimToken, string RootSessionId)> SeedReadyWorldAsync()
    {
        var (projectId, requestId, claimToken, rootSessionId) =
            await SeedRequestAsync(advanceToVerifying: true);
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var now = DateTimeOffset.UtcNow;

        db.SessionEvents.Add(new SessionEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            NodeId = _nodeId,
            ProjectId = projectId,
            RequestId = requestId,
            SessionId = rootSessionId,
            Sequence = 1,
            Type = "plan.submitted",
            OccurredAtUtcTicks = now.UtcTicks,
            ReceivedAtUtcTicks = now.UtcTicks,
            PayloadJson = "{}",
        });

        SeedSession(db, projectId, requestId, "implementer-" + requestId.ToString("N")[..8], rootSessionId, "implementer", AgentWorkState.Completed);
        SeedSession(db, projectId, requestId, "reviewer-" + requestId.ToString("N")[..8], rootSessionId, "reviewer", AgentWorkState.Completed);

        var leaseId = Guid.NewGuid();
        db.ReservationLeases.Add(new ReservationLeaseRow
        {
            Id = leaseId,
            ProjectId = projectId,
            RequestId = requestId,
            OwnerSessionId = "implementer",
            Reason = "impl",
            FencingToken = 1,
            State = nameof(ReservationLeaseState.Released),
            AcquiredAtUtcTicks = now.UtcTicks,
            LastRenewedAtUtcTicks = now.UtcTicks,
            ExpiresAtUtcTicks = now.AddMinutes(5).UtcTicks,
            ReleasedAtUtcTicks = now.AddMinutes(1).UtcTicks,
            Version = 1,
        });
        db.ReservationScopes.Add(new ReservationScopeRow
        {
            Id = Guid.NewGuid(),
            LeaseId = leaseId,
            Kind = (int)ReservationScopeKind.File,
            Path = "src/a.cs",
        });

        await db.SaveChangesAsync();
        return (projectId, requestId, claimToken, rootSessionId);
    }

    private static void SeedSession(
        ControlPlaneDbContext db,
        Guid projectId,
        Guid requestId,
        string id,
        string? parent,
        string role,
        AgentWorkState work)
    {
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = id,
            ProjectId = projectId,
            RequestId = requestId,
            ParentSessionId = parent,
            AgentName = role + "-" + id,
            Role = role,
            Runtime = "pi",
            Model = "codex/default",
            Liveness = nameof(AgentLiveness.Exited),
            Activity = nameof(AgentActivity.Idle),
            Attention = "None",
            WorkState = work.ToString(),
            StatusReason = "test",
            StartedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
            EndedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
            LastSequence = 1,
            Version = 1,
        });
    }
}
