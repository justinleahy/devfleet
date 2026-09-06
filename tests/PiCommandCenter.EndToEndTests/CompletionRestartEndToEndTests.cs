using PiCommandCenter.ControlPlane.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Reservations;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Domain.Verification;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Reservations;
using PiCommandCenter.Infrastructure.Verification;

namespace PiCommandCenter.EndToEndTests;

/// <summary>
/// Accepted completion persists RequestResult; a Control Plane restart still serves it.
/// Fake trusted verification rows — no provider/model.
/// </summary>
public sealed class CompletionRestartEndToEndTests : IClassFixture<EndToEndFixture>
{
    private readonly EndToEndFixture _fixture;

    public CompletionRestartEndToEndTests(EndToEndFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Accepted_request_result_survives_control_plane_restart()
    {
        Guid projectId;
        Guid requestId;
        var now = DateTimeOffset.UtcNow;

        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<IProjectCatalog>();
            var queue = scope.ServiceProvider.GetRequiredService<IRequestQueue>();
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var project = await catalog.RegisterAsync(new RegisterProjectCommand(
                "Completion Demo",
                "main",
                true,
                1,
                4,
                4,
                true,
                true,
                false,
                false));
            projectId = project.Id;
            var queued = await queue.EnqueueAsync(
                new ProjectId(projectId),
                new QueueWorkRequestCommand(
                    WorkRequestKind.Development,
                    RequestPriority.Normal,
                    RiskLevel.Standard,
                    "Finish the feature",
                    "Implement, review, verify"));
            requestId = queued.Id;

            var request = await db.WorkRequests.SingleAsync(r => r.Id == new WorkRequestId(requestId));
            request.Start(now);
            request.BeginPlanning(now);
            request.BeginExecuting(now);
            request.BeginReviewing(now);
            request.BeginVerifying(now);

            db.SessionEvents.Add(new SessionEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                NodeId = Guid.NewGuid(),
                ProjectId = projectId,
                RequestId = requestId,
                SessionId = "root",
                Sequence = 1,
                Type = "plan.submit",
                OccurredAtUtcTicks = now.UtcTicks,
                ReceivedAtUtcTicks = now.UtcTicks,
                PayloadJson = "{}",
            });
            db.AgentSessions.Add(Root("root", projectId, requestId, now));
            db.AgentSessions.Add(Child("impl", "implementer", projectId, requestId, now));
            db.AgentSessions.Add(Child("rev", "reviewer", projectId, requestId, now));
            db.VerificationRuns.Add(new VerificationRunRow
            {
                Id = Guid.NewGuid(),
                RequestId = requestId,
                ProfileId = "default",
                CommandId = "true",
                Status = nameof(VerificationRunStatus.Passed),
                ExitCode = 0,
                StartedAtUtcTicks = now.UtcTicks,
                CompletedAtUtcTicks = now.UtcTicks,
                OutputSummary = "true",
                Mandatory = true,
            });
            var leaseId = Guid.NewGuid();
            db.ReservationLeases.Add(new ReservationLeaseRow
            {
                Id = leaseId,
                ProjectId = projectId,
                RequestId = requestId,
                OwnerSessionId = "impl",
                Reason = "done",
                FencingToken = 1,
                State = nameof(ReservationLeaseState.Released),
                AcquiredAtUtcTicks = now.UtcTicks,
                LastRenewedAtUtcTicks = now.UtcTicks,
                ExpiresAtUtcTicks = now.AddMinutes(1).UtcTicks,
                ReleasedAtUtcTicks = now.UtcTicks,
                Version = 1,
            });
            db.ReservationScopes.Add(new ReservationScopeRow
            {
                Id = Guid.NewGuid(),
                LeaseId = leaseId,
                Kind = (int)ReservationScopeKind.File,
                Path = "README.md",
            });
            await db.SaveChangesAsync();

            var nodeId = NodeId.New();
            db.FleetNodes.Add(PiCommandCenter.Domain.Nodes.FleetNode.Register(nodeId, "node-e2e", "1.0.0", "{}", now));
            var repositoryPath = Path.Combine(Path.GetTempPath(), requestId.ToString());
            var binding = PiCommandCenter.Domain.Projects.WorkspaceBinding.Designate(
                new ProjectId(projectId), nodeId, repositoryPath, now);
            db.WorkspaceBindings.Add(binding);
            db.ExecutionAssignments.Add(ExecutionAssignment.Create(
                new WorkRequestId(requestId),
                new ProjectId(projectId),
                binding.Id,
                nodeId,
                repositoryPath,
                "main",
                binding.ValidationRevision,
                "e2e-claim",
                now,
                TimeSpan.FromMinutes(5)));
            await db.SaveChangesAsync();

            var authority = scope.ServiceProvider.GetRequiredService<IAssignmentTerminalizationService>();
            var evidence = new CompletionEvidence("Done.", ["README.md"], [], "true passed");
            var begin = await authority.BeginAsync(
                nodeId, new ProjectId(projectId), new WorkRequestId(requestId),
                "e2e-claim", "root", TerminalizationIntent.Complete, evidence, reason: null);
            Assert.True(begin.Accepted, string.Join(",", begin.MissingRequirements));
            var decision = await authority.ConfirmAsync(
                nodeId, new ProjectId(projectId), new WorkRequestId(requestId),
                "e2e-claim", "root", TerminalizationIntent.Complete, evidence, reason: null,
                new AssignmentQuiescenceProof(true, 0, 0, 0, 0, 0, true, now));
            Assert.True(decision.Accepted, string.Join(",", decision.MissingRequirements));
        }

        using var restarted = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ControlPlane", $"Data Source={_fixture.SqlitePath}");
            builder.UseSetting("Projects:ApprovedRoots:0", _fixture.ApprovedRoot);
            builder.UseTestAuthFiles(_fixture.PasswordFile, _fixture.CredentialDirectory);
        });
        using var restartScope = restarted.Services.CreateScope();
        var restored = restartScope.ServiceProvider.GetRequiredService<IAssignmentTerminalizationService>();
        var result = await restored.GetResultAsync(new WorkRequestId(requestId));
        Assert.NotNull(result);
        Assert.Equal("Done.", result!.SummaryMarkdown);
        Assert.Equal(["README.md"], result.ChangedFiles);

        var dbRestart = restartScope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var completed = await dbRestart.WorkRequests.SingleAsync(r => r.Id == new WorkRequestId(requestId));
        Assert.Equal(WorkRequestStatus.Completed, completed.Status);
    }

    private static AgentSessionRow Root(string id, Guid projectId, Guid requestId, DateTimeOffset now) => new()
    {
        Id = id,
        ProjectId = projectId,
        RequestId = requestId,
        ParentSessionId = null,
        AgentName = id,
        Role = "root",
        Runtime = "pi",
        Model = "codex/default",
        Liveness = nameof(AgentLiveness.Online),
        Activity = nameof(AgentActivity.Idle),
        Attention = nameof(AgentAttention.None),
        WorkState = nameof(AgentWorkState.Verifying),
        StatusReason = "verifying",
        StartedAtUtcTicks = now.UtcTicks,
        LastSequence = 1,
        Version = 1,
    };

    private static AgentSessionRow Child(string id, string role, Guid projectId, Guid requestId, DateTimeOffset now) => new()
    {
        Id = id,
        ProjectId = projectId,
        RequestId = requestId,
        ParentSessionId = "root",
        AgentName = id,
        Role = role,
        Runtime = "pi",
        Model = "codex/default",
        Liveness = nameof(AgentLiveness.Exited),
        Activity = nameof(AgentActivity.Idle),
        Attention = nameof(AgentAttention.None),
        WorkState = nameof(AgentWorkState.Completed),
        StatusReason = "completed",
        StartedAtUtcTicks = now.UtcTicks,
        EndedAtUtcTicks = now.UtcTicks,
        LastSequence = 2,
        Version = 2,
    };
}
