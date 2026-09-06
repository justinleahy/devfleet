using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using PiCommandCenter.ControlPlane.Security;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Application.Reservations;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Mail;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.EndToEndTests;

/// <summary>
/// End-to-end journeys of SPEC §38.4 scenarios A–D against the full Control Plane:
/// (A) reconciliation enables one eligible dispatch while project capacity fences the next;
/// (B) two concurrent writers hold disjoint reservations; (C) a conflicting acquisition is
/// denied and ownership moves by atomic handoff with a stale token rejected; (D) a crashed
/// owner's lease is recovered and force-released before the scope is grantable again. Human
/// guidance routing (root / specific child / all active agents) is proven through the
/// browser-facing guidance endpoint. No model/provider network.
/// </summary>
public sealed class CoordinationEndToEndTests : IClassFixture<EndToEndFixture>, IDisposable
{
    private readonly EndToEndFixture _fixture;
    private readonly Guid _nodeId;
    private HubConnection? _connection;
    private Guid _projectId;
    private Guid _requestId;
    private Guid _blockedRequestId;
    private readonly string _scope = Guid.NewGuid().ToString("N")[..8];
    private HubConnection? _extraConnection;
    private const string ClaimToken = "coordination-e2e-fixture-token";

    public CoordinationEndToEndTests(EndToEndFixture fixture)
    {
        _fixture = fixture;
        _nodeId = fixture.AuthenticatedNodeId;
    }

    public void Dispose()
    {
        if (_extraConnection is not null)
        {
            _extraConnection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (_connection is not null)
        {
            _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private string RootSession => $"session-root-{_scope}";
    private string ChildSession => $"session-child-{_scope}";
    private string SessionA => $"session-agent-a-{_scope}";
    private string SessionB => $"session-agent-b-{_scope}";

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private async Task<HubConnection> HubAsync()
    {
        if (_connection is not null)
        {
            return _connection;
        }

        var factory = _fixture.Factory;
        _ = factory.CreateClient(); // force server initialization before opening the connection
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "nodeHub"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(_fixture.NodeTokenHex);
            })
            .Build();
        await _connection.StartAsync();
        _ = await _connection.InvokeAsync<NodeDto>("Register",
            new NodeRegistrationMessage(_nodeId, "e2e-node-" + _scope, "1.0.0", "{}"));
        return _connection;
    }

    private async Task<HubConnection> HubForAsync(WebApplicationFactory<Program> factory)
    {
        _ = factory.CreateClient();
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "nodeHub"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(_fixture.NodeTokenHex);
            })
            .Build();
        await connection.StartAsync();
        _ = await connection.InvokeAsync<NodeDto>("Register",
            new NodeRegistrationMessage(_nodeId, "e2e-node-" + _scope, "1.0.0", "{}"));
        return connection;
    }

    /// <summary>Registers project metadata and queues one request.</summary>
    private async Task RegisterProjectAndQueueRequestAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new
        {
            displayName = "Coordination E2E " + _scope,
            defaultBranch = "main",
            enabled = true,
            maxActiveWriteRequests = 2,
            maxReadOnlyRequests = 4,
            maxChildAgentsPerRequest = 1,
            requireCleanStart = false,
            createRequestBranch = false,
            createRequestCommit = false,
            autoMerge = false,
        }, WebJson);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"register failed: {body}");
        _projectId = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();

        var queued = await client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/requests",
            new { kind = 0, priority = 1, riskLevel = 1, title = "Add a health endpoint and tests", prompt = "Delegate to a child." },
            WebJson);
        var queuedBody = await queued.Content.ReadAsStringAsync();
        Assert.True(queued.IsSuccessStatusCode, $"queue failed: {queuedBody}");
        _requestId = JsonDocument.Parse(queuedBody).RootElement.GetProperty("id").GetGuid();
    }

    private async Task SeedActiveAssignmentAndSessionsAsync(WebApplicationFactory<Program>? factory = null)
    {
        using var scope = (factory ?? _fixture.Factory).Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
        var nodeId = new NodeId(_nodeId);
        if (!await db.FleetNodes.AnyAsync(node => node.Id == nodeId))
        {
            db.FleetNodes.Add(FleetNode.Register(nodeId, "e2e-node-" + _scope, "1.0.0", "{}", now));
        }

        var project = await db.Projects.SingleAsync(candidate => candidate.Id == new ProjectId(_projectId));
        var request = await db.WorkRequests.SingleAsync(
            candidate => candidate.Id == new WorkRequestId(_requestId));
        request.Start(now);

        var repositoryPath = _fixture.CreateGitRepository();
        var binding = WorkspaceBinding.Designate(project.Id, nodeId, repositoryPath, now);
        Assert.True(binding.ApplyValidationResult(
            nodeId,
            binding.ValidationRevision,
            WorkspaceBindingStatus.Valid,
            WorkspaceBinding.ValidValidationCode,
            "Seeded for coordination end-to-end tests.",
            repositoryPath,
            now));
        var assignment = ExecutionAssignment.Create(
            request.Id,
            project.Id,
            binding.Id,
            nodeId,
            binding.CanonicalRepositoryPath!,
            project.DefaultBranch,
            binding.ValidationRevision,
            ClaimToken,
            now,
            TimeSpan.FromMinutes(5));
        db.WorkspaceBindings.Add(binding);
        db.ExecutionAssignments.Add(assignment);
        SeedSessions(db, now);

        await db.SaveChangesAsync();
    }

    private void SeedSessions(ControlPlaneDbContext db, DateTimeOffset now)
    {
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = RootSession,
            ProjectId = _projectId,
            RequestId = _requestId,
            AgentName = "root-" + _scope,
            Role = "root",
            Runtime = "pi",
            Model = "codex/gpt-5.6-sol",
            Liveness = "Active",
            Activity = "Idle",
            Attention = "None",
            WorkState = "Working",
            StatusReason = "Orchestrating",
            StartedAtUtcTicks = now.UtcTicks,
            Version = 1,
        });
        foreach (var (id, name) in new[] { (ChildSession, "child-" + _scope), (SessionA, "writer-a"), (SessionB, "writer-b") })
        {
            db.AgentSessions.Add(new AgentSessionRow
            {
                Id = id,
                ProjectId = _projectId,
                RequestId = _requestId,
                ParentSessionId = RootSession,
                AgentName = name,
                Role = "implementer",
                Runtime = "pi",
                Model = "codex/gpt-5.6-sol",
                Liveness = "Active",
                Activity = "Idle",
                Attention = "None",
                WorkState = "Working",
                StatusReason = "Implementing",
                StartedAtUtcTicks = now.UtcTicks,
                Version = 1,
            });
        }
    }

    /// <summary>Seeds one eligible request and another held behind project capacity.</summary>
    private async Task SeedQueuedProjectAsync()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var now = DateTimeOffset.UtcNow;
        var project = Project.Register(
            "Coordination E2E " + _scope, "main",
            enabled: true, maxActiveWriteRequests: 1, maxReadOnlyRequests: 4,
            maxChildAgentsPerRequest: 1, requireCleanStart: false, createRequestBranch: false,
            createRequestCommit: false, autoMerge: false, now);
        var binding = WorkspaceBinding.Designate(
            project.Id, new NodeId(_nodeId), _fixture.CreateGitRepository(), now);
        Assert.True(binding.ApplyValidationResult(
            new NodeId(_nodeId),
            binding.ValidationRevision,
            WorkspaceBindingStatus.Valid,
            WorkspaceBinding.ValidValidationCode,
            "Seeded for reconciled coordination dispatch.",
            binding.RepositoryPath,
            now));
        var request = WorkRequest.Enqueue(project.Id, WorkRequestKind.Development,
            RequestPriority.High, RiskLevel.Standard,
            "Add a health endpoint and tests", "Delegate to a child.", now);
        var blockedRequest = WorkRequest.Enqueue(project.Id, WorkRequestKind.Development,
            RequestPriority.Normal, RiskLevel.Standard,
            "Add a diagnostics endpoint", "Wait for project capacity.", now);
        db.Projects.Add(project);
        db.WorkspaceBindings.Add(binding);
        db.WorkRequests.AddRange(request, blockedRequest);
        await db.SaveChangesAsync();
        _projectId = project.Id.Value;
        _requestId = request.Id.Value;
        _blockedRequestId = blockedRequest.Id.Value;
    }

    [Fact]
    public async Task Scenario_A_reconciled_node_dispatches_once_and_historical_child_result_remains_durable()
    {
        var hub = await HubAsync();
        await SeedQueuedProjectAsync();
        var observedAt = DateTimeOffset.UtcNow;
        const string routingRevision = "coordination-e2e";
        await hub.InvokeAsync<NodeDto>("Heartbeat", new NodeHeartbeatMessage(
            _nodeId,
            [],
            ExecutionStatus: new NodeExecutionStatusMessage(
                observedAt,
                AvailableRequestSlots: 1024,
                ActiveAssignmentIds: [],
                RoutingRevision: routingRevision,
                Routes:
                [
                    new RuntimeRouteReadinessMessage(
                        "root",
                        "codex/gpt-5.6-sol",
                        RuntimeReadinessStatuses.Ready,
                        "coordination-e2e",
                        observedAt,
                        routingRevision),
                ])));

        var reconciled = await hub.InvokeAsync<ReconcileAssignmentsResultMessage>(
            "ReconcileAssignments",
            new ReconcileAssignmentsMessage(_nodeId, LeaseSeconds: 300, Assignments: []));
        Assert.All(reconciled.Assignments, row =>
        {
            Assert.Equal(AssignmentReconciliationDisposition.RecoveryRequired, row.Disposition);
            Assert.NotEqual(_requestId, row.RequestId);
            Assert.NotEqual(_requestId, row.Assignment?.RequestId);
            Assert.NotEqual(_projectId, row.Assignment?.ProjectId);
        });

        var dispatched = await hub.InvokeAsync<ExecutionAssignmentMessage?>(
            "ClaimNext",
            new ClaimRequestMessage(_nodeId, LeaseSeconds: 300));
        var assignment = Assert.IsType<ExecutionAssignmentMessage>(dispatched);
        Assert.Equal(_requestId, assignment.RequestId);
        Assert.Equal(_projectId, assignment.ProjectId);
        Assert.Equal(_nodeId, assignment.NodeIdSnapshot);

        var secondClaim = await hub.InvokeAsync<ExecutionAssignmentMessage?>(
            "ClaimNext",
            new ClaimRequestMessage(_nodeId, LeaseSeconds: 300));
        Assert.Null(secondClaim);

        using (var dispatchScope = _fixture.Factory.Services.CreateScope())
        {
            var dispatchDb = dispatchScope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var blockedRequest = await dispatchDb.WorkRequests.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == new WorkRequestId(_blockedRequestId));
            Assert.Equal(WorkRequestStatus.Queued, blockedRequest.Status);
            Assert.Equal(1, await dispatchDb.ExecutionAssignments.AsNoTracking()
                .CountAsync(candidate => candidate.ProjectId == new ProjectId(_projectId)));
            var eligibility = dispatchScope.ServiceProvider
                .GetRequiredService<IRequestEligibilityEvaluator>();
            var decision = await eligibility.EvaluateAsync(
                new WorkRequestId(_blockedRequestId), new NodeId(_nodeId));
            Assert.Equal(SchedulingReasonCodes.ProjectConcurrencyUnavailable, decision.Status.Code);

            SeedSessions(dispatchDb, observedAt);
            await dispatchDb.SaveChangesAsync();
        }

        var registeredEventId = $"evt-{_scope}-registered";
        var completedEventId = $"evt-{_scope}-completed";
        var acknowledgement = await hub.InvokeAsync<NodeEventAcknowledgementMessage>(
            "PublishEvents",
            new NodeEventBatchMessage(
            [
                new NodeEventMessage(
                    EventId: registeredEventId,
                    NodeId: _nodeId,
                    ProjectId: _projectId,
                    RequestId: _requestId,
                    ClaimToken: assignment.ClaimToken,
                    SessionId: ChildSession,
                    Sequence: 1,
                    Type: "session.registered",
                    OccurredAt: observedAt,
                    PayloadJson: JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["parentSessionId"] = RootSession,
                        ["agentName"] = "child-" + _scope,
                        ["role"] = "implementer",
                        ["model"] = "codex/gpt-5.6-sol",
                    })),
                new NodeEventMessage(
                    EventId: completedEventId,
                    NodeId: _nodeId,
                    ProjectId: _projectId,
                    RequestId: _requestId,
                    ClaimToken: assignment.ClaimToken,
                    SessionId: ChildSession,
                    Sequence: 2,
                    Type: "session.completed",
                    OccurredAt: observedAt.AddSeconds(1),
                    PayloadJson: JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["summary"] = "Added the health endpoint and tests.",
                        ["changedFiles"] = new[] { "src/Health.cs", "tests/HealthTests.cs" },
                    })),
            ]));
        Assert.Equal([registeredEventId, completedEventId], acknowledgement.EventIds);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var child = await db.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == ChildSession);
        Assert.Equal(RootSession, child.ParentSessionId);
        Assert.Equal(_projectId, child.ProjectId);
        Assert.Equal(_requestId, child.RequestId);
        var completed = await db.SessionEvents.AsNoTracking()
            .SingleAsync(e => e.SessionId == ChildSession && e.Type == "session.completed");
        Assert.Contains("Added the health endpoint", completed.PayloadJson);
        Assert.Contains("src/Health.cs", completed.PayloadJson);

        var request = await db.WorkRequests.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == new WorkRequestId(_requestId));
        Assert.Equal(WorkRequestStatus.Starting, request.Status);
        var retainedAssignment = await db.ExecutionAssignments.AsNoTracking().SingleAsync(
            candidate => candidate.RequestId == new WorkRequestId(_requestId));
        Assert.Equal(ExecutionAssignmentState.Starting, retainedAssignment.State);
        Assert.Equal(assignment.ClaimToken, retainedAssignment.ClaimToken);
    }

    [Fact]
    public async Task Scenario_B_two_concurrent_writers_hold_disjoint_reservations()
    {
        await RegisterProjectAndQueueRequestAsync(_fixture.CreateClient());
        await SeedActiveAssignmentAndSessionsAsync();
        var hub = await HubAsync();

        var acquireA = hub.InvokeAsync<ReservationOperationResultMessage>("AcquireReservation",
            new AcquireReservationMessage(_projectId, _requestId, ClaimToken, SessionA,
                [new ReservationScopeMessage(0, "File", "src/App/HealthEndpoint.cs")], "API implementer"));
        var acquireB = hub.InvokeAsync<ReservationOperationResultMessage>("AcquireReservation",
            new AcquireReservationMessage(_projectId, _requestId, ClaimToken, SessionB,
                [new ReservationScopeMessage(0, "File", "tests/HealthEndpointTests.cs")], "test implementer"));
        var resultA = await acquireA;
        var resultB = await acquireB;

        Assert.True(resultA.Lease is not null, resultA.Error?.Message);
        Assert.True(resultB.Lease is not null, resultB.Error?.Message);
        Assert.NotEqual(resultA.Lease!.LeaseId, resultB.Lease!.LeaseId);
    }

    [Fact]
    public async Task Scenario_C_conflict_denies_atomically_then_handoff_transfers_and_rejects_the_stale_token()
    {
        await RegisterProjectAndQueueRequestAsync(_fixture.CreateClient());
        await SeedActiveAssignmentAndSessionsAsync();
        var hub = await HubAsync();

        var granted = await hub.InvokeAsync<ReservationOperationResultMessage>("AcquireReservation",
            new AcquireReservationMessage(_projectId, _requestId, ClaimToken, SessionA,
                [new ReservationScopeMessage(0, "File", "src/App/DependencyInjection.cs")], "implement DI"));
        Assert.True(granted.Lease is not null, granted.Error?.Message);
        var grantedLease = granted.Lease!;

        var denied = await hub.InvokeAsync<ReservationOperationResultMessage>("AcquireReservation",
            new AcquireReservationMessage(_projectId, _requestId, ClaimToken, SessionB,
                [new ReservationScopeMessage(0, "File", "src/App/DependencyInjection.cs")], "same file"));
        Assert.True(denied.Error is not null);
        Assert.Equal(ReservationErrorCodes.Conflict, denied.Error.Code);

        // The conflicting request granted nothing.
        var listed = await hub.InvokeAsync<ReservationLeaseMessage[]>(
            "ListReservations",
            new ListReservationsMessage(_projectId, _requestId, ClaimToken, IncludeReleased: false));
        var lease = Assert.Single(listed);
        Assert.Equal(grantedLease.LeaseId, lease.LeaseId);

        // Atomic handoff through the hub invalidates the old token immediately.
        var handed = await hub.InvokeAsync<ReservationOperationResultMessage>("TransferReservation",
            new TransferReservationMessage(
                _projectId, _requestId, ClaimToken, lease.LeaseId, SessionA, SessionB));
        Assert.True(handed.Lease is not null, handed.Error?.Message);
        var handedLease = handed.Lease!;
        Assert.Equal(SessionB, handedLease.OwnerSessionId);
        Assert.True(handedLease.FencingToken > grantedLease.FencingToken);

        // A former owner's stale decision is simply unauthorized (ownership is checked first).
        var stale = await hub.InvokeAsync<MutationAuthorizationResultMessage>("AuthorizeMutation",
            new MutationAuthorizationMessage(
                _projectId, _requestId, ClaimToken,
                lease.LeaseId, grantedLease.FencingToken, SessionA,
                "src/App/DependencyInjection.cs", Operation: 1, OperationName: "write"));
        Assert.False(stale.Authorized);
        Assert.NotNull(stale.Error);

        // The new owner mutates with the fresh token.
        var fresh = await hub.InvokeAsync<MutationAuthorizationResultMessage>("AuthorizeMutation",
            new MutationAuthorizationMessage(
                _projectId, _requestId, ClaimToken,
                lease.LeaseId, handedLease.FencingToken, SessionB,
                "src/App/DependencyInjection.cs", Operation: 2, OperationName: "edit"));
        Assert.True(fresh.Authorized, fresh.Error?.Message);
    }

    [Fact]
    public async Task Scenario_D_crashed_owners_lease_is_recovered_force_released_and_regranted()
    {
        // A controllable clock proves the real expiry path without sleeping.
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        using var factory = CreateControlPlane(clock);
        var hub = _extraConnection = await HubForAsync(factory);
        var client = _fixture.CreateAuthenticatedClient(factory);
        await RegisterProjectAndQueueRequestAsync(client);
        await SeedActiveAssignmentAndSessionsAsync(factory);

        var leaseResult = await hub.InvokeAsync<ReservationOperationResultMessage>("AcquireReservation",
            new AcquireReservationMessage(_projectId, _requestId, ClaimToken, SessionA,
                [new ReservationScopeMessage(0, "File", "src/App/Crash.cs")], "will crash"));
        Assert.True(leaseResult.Lease is not null, leaseResult.Error?.Message);
        var lease = leaseResult.Lease!;

        // Before expiry the node cannot flag recovery: the owner may simply be slow.
        clock.Advance(TimeSpan.FromSeconds(60));
        var early = await hub.InvokeAsync<ReservationOperationResultMessage>("MarkReservationRecovery",
            new MarkRecoveryMessage(_projectId, _requestId, ClaimToken, lease.LeaseId, "too early"));
        Assert.True(early.Error is not null, "recovery flagging must require an expired lease");

        // The node reports the owner process gone after the deadline: recovery-required.
        clock.Advance(TimeSpan.FromSeconds(70));
        var flagged = await hub.InvokeAsync<ReservationOperationResultMessage>("MarkReservationRecovery",
            new MarkRecoveryMessage(
                _projectId, _requestId, ClaimToken, lease.LeaseId,
                "Owner process crashed; awaiting inspection."));
        Assert.True(flagged.Lease is not null, flagged.Error?.Message);
        Assert.Equal("RecoveryRequired", flagged.Lease!.StateName);

        // The human administrator inspects and force-releases with a snapshot. (The browser
        // force-release endpoint is not yet implemented; the action is the authority call.)
        using (var adminScope = factory.Services.CreateScope())
        {
            var reservations = adminScope.ServiceProvider.GetRequiredService<IReservationService>();
            var released = await reservations.ForceReleaseAsync(new ForceReleaseReservationCommand(
                lease.LeaseId, "Owner confirmed dead", "M src/App/Crash.cs", "human"));
            Assert.Equal("Released", released.StateName);
        }
        // The scope is grantable again by a fresh agent.
        var reacquired = await hub.InvokeAsync<ReservationOperationResultMessage>("AcquireReservation",
            new AcquireReservationMessage(_projectId, _requestId, ClaimToken, SessionB,
                [new ReservationScopeMessage(0, "File", "src/App/Crash.cs")], "resume work"));
        Assert.True(reacquired.Lease is not null, reacquired.Error?.Message);
        Assert.True(reacquired.Lease!.FencingToken > lease.FencingToken);

        // The full lifecycle left a durable audit trail.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var kinds = await db.ReservationAuditFacts.AsNoTracking()
            .Where(f => f.LeaseId == lease.LeaseId)
            .Select(f => f.Kind)
            .ToListAsync();
        Assert.Contains("Expired", kinds);
        Assert.Contains("ForceReleased", kinds);
    }

    private WebApplicationFactory<Program> CreateControlPlane(MutableClock clock)
    {
        var sqlitePath = Path.Combine(
            Path.GetDirectoryName(_fixture.SqlitePath)!,
            "scenario-d-" + _scope + ".db");
        File.Create(sqlitePath).Dispose();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ControlPlane", $"Data Source={sqlitePath}");
            builder.UseSetting("Projects:ApprovedRoots:0", _fixture.ApprovedRoot);
            builder.UseTestAuthFiles(_fixture.PasswordFile, _fixture.CredentialDirectory);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
        });
    }

    [Fact]
    public async Task Human_guidance_routes_to_root_single_child_and_all_active_agents()
    {
        await RegisterProjectAndQueueRequestAsync(_fixture.CreateClient());
        await SeedActiveAssignmentAndSessionsAsync();
        var client = _fixture.CreateClient();

        // To the root session only.
        var toRoot = await client.PostAsJsonAsync($"/api/requests/{_requestId}/guidance",
            new { projectId = _projectId, target = "root", subject = "priority change", bodyMarkdown = "Focus on the API first." }, WebJson);
        var rootBody = await toRoot.Content.ReadAsStringAsync();
        Assert.True(toRoot.IsSuccessStatusCode, $"guidance to root failed: {rootBody}");

        // To one specific child.
        var toChild = await client.PostAsJsonAsync($"/api/requests/{_requestId}/guidance",
            new { projectId = _projectId, target = ChildSession, subject = "note", bodyMarkdown = "Please acknowledge." }, WebJson);
        Assert.True(toChild.IsSuccessStatusCode, await toChild.Content.ReadAsStringAsync());

        // To every active agent of the request.
        var toAll = await client.PostAsJsonAsync($"/api/requests/{_requestId}/guidance",
            new { projectId = _projectId, target = "all", subject = "stand down", bodyMarkdown = "Wrap up current edits." }, WebJson);
        var allBody = await toAll.Content.ReadAsStringAsync();
        Assert.True(toAll.IsSuccessStatusCode, $"guidance to all failed: {allBody}");

        // An unknown target is a 404, never a partial delivery.
        var missing = await client.PostAsJsonAsync($"/api/requests/{_requestId}/guidance",
            new { projectId = _projectId, target = $"ghost-{_scope}", subject = "x", bodyMarkdown = "y" }, WebJson);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);

        // Every guidance message is durable, high importance, human-originated, ack-required.
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var messages = await db.Set<MailMessageRow>()
            .Include(m => m.Recipients)
            .Where(m => m.RequestId == _requestId && m.SenderSessionId == null)
            .ToListAsync();
        Assert.Equal(3, messages.Count);
        Assert.All(messages, m =>
        {
            Assert.Equal("High", m.Importance);
            Assert.True(m.AcknowledgementRequired);
        });
        Assert.Single(messages, m => m.Recipients.Count == 1 && m.Recipients[0].SessionId == RootSession);
        var allMessage = Assert.Single(messages, m => m.Recipients.Count > 1);
        Assert.Equal(4, allMessage.Recipients.Count);
    }
}

/// <summary>Mutable clock used to drive real TTL expiry without sleeping.</summary>
internal sealed class MutableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public void Advance(TimeSpan delta) => _now += delta;

    public override DateTimeOffset GetUtcNow() => _now;
}
