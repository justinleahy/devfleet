using System.Net.Http.Json;
using System.Text.Json;
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
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Mail;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.EndToEndTests;

/// <summary>
/// End-to-end journeys of SPEC §38.4 scenarios A–D against the full Control Plane:
/// (A) a normal delegated change — request claimed, a child session registered under the root
/// with a durable result payload; (B) two concurrent writers hold disjoint reservations;
/// (C) a conflicting acquisition is denied and ownership moves by atomic handoff with a stale
/// token rejected; (D) a crashed owner's lease is recovered and force-released before the
/// scope is grantable again. Human guidance routing (root / specific child / all active
/// agents) is proven through the browser-facing guidance endpoint. No model/provider network.
/// </summary>
public sealed class CoordinationEndToEndTests : IClassFixture<EndToEndFixture>, IDisposable
{
    private readonly EndToEndFixture _fixture;
    private readonly Guid _nodeId = Guid.NewGuid();
    private HubConnection? _connection;
    private Guid _projectId;
    private Guid _requestId;
    private readonly string _scope = Guid.NewGuid().ToString("N")[..8];
    private HubConnection? _extraConnection;

    public CoordinationEndToEndTests(EndToEndFixture fixture) => _fixture = fixture;

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

    private HubConnection Hub()
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
            })
            .Build();
        _connection.StartAsync().GetAwaiter().GetResult();
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
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    /// <summary>Registers a real temporary Git repository project and queues one request.</summary>
    private async Task RegisterProjectAndQueueRequestAsync(HttpClient client)
    {
        var repositoryPath = _fixture.CreateGitRepository();
        var response = await client.PostAsJsonAsync("/api/projects", new
        {
            displayName = "Coordination E2E " + _scope,
            repositoryPath,
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

    private async Task SeedSessionsAsync()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.FleetNodes.Add(FleetNode.Register(new NodeId(_nodeId), "e2e-node-" + _scope, "1.0.0", "{}", now));
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = RootSession, ProjectId = _projectId, RequestId = _requestId,
            AgentName = "root-" + _scope, Role = "root", Runtime = "pi", RuntimeProfile = "root-readonly",
            Liveness = "Active", Activity = "Idle", Attention = "None", WorkState = "Working",
            StatusReason = "Orchestrating", StartedAtUtcTicks = now.UtcTicks, Version = 1,
        });
        foreach (var (id, name) in new[] { (ChildSession, "child-" + _scope), (SessionA, "writer-a"), (SessionB, "writer-b") })
        {
            db.AgentSessions.Add(new AgentSessionRow
            {
                Id = id, ProjectId = _projectId, RequestId = _requestId, ParentSessionId = RootSession,
                AgentName = name, Role = "implementer", Runtime = "pi", RuntimeProfile = "coder",
                Liveness = "Active", Activity = "Idle", Attention = "None", WorkState = "Working",
                StatusReason = "Implementing", StartedAtUtcTicks = now.UtcTicks, Version = 1,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds a project bound to this node over a real temporary Git repository, plus one queued
    /// request, so a live node connection can claim it (the browser API binds projects to a
    /// node it selects itself, so the claim journey seeds the project directly).
    /// </summary>
    private async Task SeedClaimableProjectAsync()
    {
        var repositoryPath = _fixture.CreateGitRepository();
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var now = DateTimeOffset.UtcNow;
        var project = Project.Register(
            new NodeId(_nodeId), "Coordination E2E " + _scope, repositoryPath, "main",
            enabled: true, maxActiveWriteRequests: 2, maxReadOnlyRequests: 4,
            maxChildAgentsPerRequest: 1, requireCleanStart: false, createRequestBranch: false,
            createRequestCommit: false, autoMerge: false, now);
        var request = WorkRequest.Enqueue(project.Id, WorkRequestKind.Development,
            RequestPriority.Normal, RiskLevel.Standard,
            "Add a health endpoint and tests", "Delegate to a child.", now);
        db.Projects.Add(project);
        db.WorkRequests.Add(request);
        await db.SaveChangesAsync();
        _projectId = project.Id.Value;
        _requestId = request.Id.Value;
        Assert.True(Directory.Exists(repositoryPath));
    }

    [Fact]
    public async Task Scenario_A_delegated_change_claims_the_request_and_persists_the_child_result()
    {
        // The node is live before the project exists, so the claim can be routed to it.
        var hub = Hub();
        _ = await hub.InvokeAsync<NodeDto>("Register",
            new NodeRegistrationMessage(_nodeId, "e2e-node-" + _scope, "1.0.0", "{}"));

        await SeedClaimableProjectAsync();

        // The node claims the queued request.
        var claim = await hub.InvokeAsync<RequestClaimMessage?>("ClaimNext",
            new ClaimRequestMessage(_nodeId, LeaseSeconds: 300));
        Assert.NotNull(claim);
        Assert.Equal(_requestId, claim!.RequestId);

        // The root delegates: a child session registers under the root, does the work and
        // completes with a durable result payload.
        await hub.InvokeAsync<NodeEventAcknowledgementMessage>("PublishEvents", new NodeEventBatchMessage(
        [
            new NodeEventMessage(
                EventId: $"evt-{_scope}-registered",
                NodeId: _nodeId,
                ProjectId: _projectId,
                RequestId: _requestId,
                SessionId: ChildSession,
                Sequence: 1,
                Type: "session.registered",
                OccurredAt: DateTimeOffset.UtcNow,
                PayloadJson: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["parentSessionId"] = RootSession,
                    ["agentName"] = "child-" + _scope,
                    ["role"] = "implementer",
                    ["runtimeProfile"] = "coder",
                })),
            new NodeEventMessage(
                EventId: $"evt-{_scope}-completed",
                NodeId: _nodeId,
                ProjectId: _projectId,
                RequestId: _requestId,
                SessionId: ChildSession,
                Sequence: 2,
                Type: "session.completed",
                OccurredAt: DateTimeOffset.UtcNow.AddSeconds(1),
                PayloadJson: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["summary"] = "Added the health endpoint and tests.",
                    ["changedFiles"] = new[] { "src/Health.cs", "tests/HealthTests.cs" },
                })),
        ]));

        // Everything survived into the authoritative store: parent-child link and result.
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

        // The queue no longer hands the claimed request out again.
        var second = await hub.InvokeAsync<RequestClaimMessage?>("ClaimNext",
            new ClaimRequestMessage(_nodeId, LeaseSeconds: 300));
        Assert.True(second is null || second.RequestId != _requestId, "the claimed request must not be re-claimed");
    }

    [Fact]
    public async Task Scenario_B_two_concurrent_writers_hold_disjoint_reservations()
    {
        await RegisterProjectAndQueueRequestAsync(_fixture.CreateClient());
        await SeedSessionsAsync();
        var hub = Hub();

        var acquireA = hub.InvokeAsync<ReservationOperationResultMessage>("AcquireReservation",
            new AcquireReservationMessage(_projectId, _requestId, SessionA,
                [new ReservationScopeMessage(0, "File", "src/App/HealthEndpoint.cs")], "API implementer"));
        var acquireB = hub.InvokeAsync<ReservationOperationResultMessage>("AcquireReservation",
            new AcquireReservationMessage(_projectId, _requestId, SessionB,
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
        await SeedSessionsAsync();
        var hub = Hub();

        var granted = await hub.InvokeAsync<ReservationOperationResultMessage>("AcquireReservation",
            new AcquireReservationMessage(_projectId, _requestId, SessionA,
                [new ReservationScopeMessage(0, "File", "src/App/DependencyInjection.cs")], "implement DI"));
        Assert.True(granted.Lease is not null, granted.Error?.Message);
        var grantedLease = granted.Lease!;

        var denied = await hub.InvokeAsync<ReservationOperationResultMessage>("AcquireReservation",
            new AcquireReservationMessage(_projectId, _requestId, SessionB,
                [new ReservationScopeMessage(0, "File", "src/App/DependencyInjection.cs")], "same file"));
        Assert.True(denied.Error is not null);
        Assert.Equal(ReservationErrorCodes.Conflict, denied.Error.Code);

        // The conflicting request granted nothing.
        var listed = await hub.InvokeAsync<ReservationLeaseMessage[]>(
            "ListReservations", new ListReservationsMessage(_projectId, IncludeReleased: false));
        var lease = Assert.Single(listed);
        Assert.Equal(grantedLease.LeaseId, lease.LeaseId);

        // Atomic handoff through the hub invalidates the old token immediately.
        var handed = await hub.InvokeAsync<ReservationOperationResultMessage>("TransferReservation",
            new TransferReservationMessage(lease.LeaseId, SessionA, SessionB));
        Assert.True(handed.Lease is not null, handed.Error?.Message);
        var handedLease = handed.Lease!;
        Assert.Equal(SessionB, handedLease.OwnerSessionId);
        Assert.True(handedLease.FencingToken > grantedLease.FencingToken);

        // A former owner's stale decision is simply unauthorized (ownership is checked first).
        var stale = await hub.InvokeAsync<MutationAuthorizationResultMessage>("AuthorizeMutation",
            new MutationAuthorizationMessage(
                lease.LeaseId, grantedLease.FencingToken, SessionA,
                "src/App/DependencyInjection.cs", Operation: 1, OperationName: "write"));
        Assert.False(stale.Authorized);
        Assert.NotNull(stale.Error);

        // The new owner mutates with the fresh token.
        var fresh = await hub.InvokeAsync<MutationAuthorizationResultMessage>("AuthorizeMutation",
            new MutationAuthorizationMessage(
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
        var client = factory.CreateClient();
        await RegisterProjectAndQueueRequestAsync(client);
        await SeedSessionsAsync();

        var leaseResult = await hub.InvokeAsync<ReservationOperationResultMessage>("AcquireReservation",
            new AcquireReservationMessage(_projectId, _requestId, SessionA,
                [new ReservationScopeMessage(0, "File", "src/App/Crash.cs")], "will crash"));
        Assert.True(leaseResult.Lease is not null, leaseResult.Error?.Message);
        var lease = leaseResult.Lease!;

        // Before expiry the node cannot flag recovery: the owner may simply be slow.
        clock.Advance(TimeSpan.FromSeconds(60));
        var early = await hub.InvokeAsync<ReservationOperationResultMessage>("MarkReservationRecovery",
            new MarkRecoveryMessage(lease.LeaseId, "too early"));
        Assert.True(early.Error is not null, "recovery flagging must require an expired lease");

        // The node reports the owner process gone after the deadline: recovery-required.
        clock.Advance(TimeSpan.FromSeconds(70));
        var flagged = await hub.InvokeAsync<ReservationOperationResultMessage>("MarkReservationRecovery",
            new MarkRecoveryMessage(lease.LeaseId, "Owner process crashed; awaiting inspection."));
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
            new AcquireReservationMessage(_projectId, _requestId, SessionB,
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

    private WebApplicationFactory<Program> CreateControlPlane(MutableClock clock) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ControlPlane", $"Data Source={_fixture.SqlitePath}");
            builder.UseSetting("Projects:ApprovedRoots:0", _fixture.ApprovedRoot);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
        });

    [Fact]
    public async Task Human_guidance_routes_to_root_single_child_and_all_active_agents()
    {
        await RegisterProjectAndQueueRequestAsync(_fixture.CreateClient());
        await SeedSessionsAsync();
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
