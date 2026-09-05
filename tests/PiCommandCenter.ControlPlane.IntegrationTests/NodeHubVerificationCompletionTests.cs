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
using PiCommandCenter.Application.Completion;
using PiCommandCenter.ControlPlane.Hubs;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Completion;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
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
    private readonly Guid _nodeId = Guid.NewGuid();

    public NodeHubVerificationCompletionTests(ControlPlaneFixture fixture)
    {
        _fixture = fixture;
        var factory = fixture.Factory;
        _ = factory.CreateClient();
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "nodeHub"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        _connection.StartAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Fact]
    public async Task RecordVerification_then_EvaluateCompletion_preserves_complete_missing_list()
    {
        await RegisterNodeAsync();
        var (projectId, requestId, rootSessionId) = await SeedRequestAsync(advanceToVerifying: false);

        var fail = await _connection.InvokeAsync<VerificationRunMessage>(
            "RecordVerification",
            Run(projectId, requestId, rootSessionId, VerificationRunStatus.Failed, exitCode: 1, mandatory: true));
        Assert.Equal((int)VerificationRunStatus.Failed, fail.Status);
        Assert.NotEqual(Guid.Empty, fail.Id);

        var pass = await _connection.InvokeAsync<VerificationRunMessage>(
            "RecordVerification",
            Run(projectId, requestId, rootSessionId, VerificationRunStatus.Passed, exitCode: 0, mandatory: true));
        Assert.Equal((int)VerificationRunStatus.Passed, pass.Status);

        var decision = await _connection.InvokeAsync<CompletionGateDecisionMessage>(
            "EvaluateCompletion",
            new EvaluateCompletionMessage(
                Guid.NewGuid(),
                projectId,
                requestId,
                rootSessionId,
                new CompletionEvidenceMessage("", null, [], "")));

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
        var (projectId, requestId, sessionId) = await SeedRequestAsync(advanceToVerifying: false);

        await Assert.ThrowsAnyAsync<HubException>(() => _connection.InvokeAsync<VerificationRunMessage>(
            "RecordVerification",
            Run(projectId, requestId, sessionId, VerificationRunStatus.Passed, 0, true) with { CorrelationId = Guid.Empty }));

        await Assert.ThrowsAnyAsync<HubException>(() => _connection.InvokeAsync<VerificationRunMessage>(
            "RecordVerification",
            Run(projectId, requestId, sessionId, VerificationRunStatus.Passed, 0, true) with
            {
                OutputSummary = new string('x', NodeTransportLimits.MaxVerificationOutputBytes + 1),
            }));
    }

    [Fact]
    public async Task Accepted_result_and_events_survive_host_restart()
    {
        await RegisterNodeAsync();
        var (projectId, requestId, rootSessionId) = await SeedReadyWorldAsync();

        var recorded = await _connection.InvokeAsync<VerificationRunMessage>(
            "RecordVerification",
            Run(projectId, requestId, rootSessionId, VerificationRunStatus.Passed, 0, true));
        Assert.Equal("Passed", recorded.StatusName);

        var decision = await _connection.InvokeAsync<CompletionGateDecisionMessage>(
            "EvaluateCompletion",
            new EvaluateCompletionMessage(
                Guid.NewGuid(),
                projectId,
                requestId,
                rootSessionId,
                new CompletionEvidenceMessage(
                    "Shipped the change.",
                    ["src/a.cs"],
                    [],
                    "all green")));

        Assert.True(decision.Accepted);
        Assert.Empty(decision.MissingRequirements);
        Assert.NotNull(decision.Result);
        Assert.Equal("Shipped the change.", decision.Result.SummaryMarkdown);

        using var client = _fixture.CreateClient();
        var before = await client.GetFromJsonAsync<RequestResultDto>($"/api/requests/{requestId}/result", Json);
        Assert.NotNull(before);
        Assert.Equal(requestId, before.RequestId);

        var eventsBefore = await client.GetFromJsonAsync<JsonElement>($"/api/requests/{requestId}/events", Json);
        Assert.True(eventsBefore.GetProperty("events").GetArrayLength() >= 1);

        await using var restarted = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ControlPlane", $"Data Source={_fixture.SqlitePath}");
            builder.UseSetting("Projects:ApprovedRoots:0", _fixture.ApprovedRoot);
        });
        using var restartedClient = restarted.CreateClient();
        var after = await restartedClient.GetFromJsonAsync<RequestResultDto>($"/api/requests/{requestId}/result", Json);
        Assert.NotNull(after);
        Assert.Equal(before.SummaryMarkdown, after.SummaryMarkdown);
        Assert.Equal(before.CreatedAt, after.CreatedAt);

        var missing = await restartedClient.GetAsync($"/api/requests/{Guid.NewGuid()}/result");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private async Task RegisterNodeAsync() => _ = await _connection.InvokeAsync<NodeDto>(
        "Register", new NodeRegistrationMessage(_nodeId, "pi-hub-verify", "1.0.0", "{}"));

    private static VerificationRunMessage Run(
        Guid projectId,
        Guid requestId,
        string sessionId,
        VerificationRunStatus status,
        int? exitCode,
        bool mandatory) => new(
        Guid.NewGuid(),
        projectId,
        requestId,
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

    private async Task<(Guid ProjectId, Guid RequestId, string RootSessionId)> SeedRequestAsync(bool advanceToVerifying)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var now = DateTimeOffset.UtcNow;
        var nodeId = new NodeId(_nodeId);
        var project = await db.Projects.SingleOrDefaultAsync(p => p.NodeId == nodeId);
        if (project is null)
        {
            project = Project.Register(
                nodeId, "Verify project " + Guid.NewGuid().ToString("N")[..6],
                Path.Combine(_fixture.ApprovedRoot, "repo-" + Guid.NewGuid().ToString("N")[..8]),
                "main", enabled: true, maxActiveWriteRequests: 2, maxReadOnlyRequests: 4,
                maxChildAgentsPerRequest: 2, requireCleanStart: false, createRequestBranch: false,
                createRequestCommit: false, autoMerge: false, now);
            db.Projects.Add(project);
        }

        var request = WorkRequest.Enqueue(project.Id, WorkRequestKind.Development, RequestPriority.Normal,
            RiskLevel.Standard, "Verify request", "Do verify work", now);
        db.WorkRequests.Add(request);
        await db.SaveChangesAsync();

        if (advanceToVerifying)
        {
            var tracked = await db.WorkRequests.SingleAsync(r => r.Id == request.Id);
            tracked.Start(now);
            tracked.BeginPlanning(now);
            tracked.BeginExecuting(now);
            tracked.BeginReviewing(now);
            tracked.BeginVerifying(now);
            await db.SaveChangesAsync();
        }

        return (project.Id.Value, request.Id.Value, "root-" + request.Id.Value.ToString("N")[..8]);
    }

    private async Task<(Guid ProjectId, Guid RequestId, string RootSessionId)> SeedReadyWorldAsync()
    {
        var (projectId, requestId, rootSessionId) = await SeedRequestAsync(advanceToVerifying: true);
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

        SeedSession(db, projectId, requestId, rootSessionId, null, "root", AgentWorkState.Verifying);
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
        return (projectId, requestId, rootSessionId);
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
            RuntimeProfile = "default",
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
