using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.ControlPlane.Hubs;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

/// <summary>
/// Exercises the /nodeHub SignalR contract through a real hub connection (long polling over the
/// test server's message handler): registration, heartbeats, dispatch freeze, lease renewal, and
/// idempotent events.
/// </summary>
public sealed class NodeHubTests : IClassFixture<ControlPlaneFixture>, IDisposable
{
    private readonly ControlPlaneFixture _fixture;
    private readonly HubConnection _connection;
    private readonly Guid _nodeId;

    public NodeHubTests(ControlPlaneFixture fixture)
    {
        _fixture = fixture;
        _nodeId = fixture.AuthenticatedNodeId;
        _connection = fixture.CreateNodeHubConnection();
        _connection.StartAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();


    [Fact]
    public async Task NodeHub_is_not_a_browser_navigable_page()
    {
        var response = await _fixture.CreateAnonymousClient().GetAsync("/nodeHub");
        var body = await response.Content.ReadAsStringAsync();

        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(401, (int)response.StatusCode);
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Register_updates_the_node_and_heartbeat_brings_it_online()
    {
        var registered = await _connection.InvokeAsync<NodeDto>(
            "Register", new NodeRegistrationMessage(_nodeId, "pi-hub-01", "1.0.0", "{}"));

        Assert.Equal(_nodeId, registered.Id);
        Assert.Equal("pi-hub-01", registered.DisplayName);

        var heartbeaten = await _connection.InvokeAsync<NodeDto>(
            "Heartbeat", new NodeHeartbeatMessage(_nodeId, ["session-a"]));

        Assert.Equal(NodeStatus.Online, heartbeaten.Status);
        Assert.Equal(registered.Version + 1, heartbeaten.Version);

        var persisted = await GetNodeAsync(registered.Id);
        Assert.NotNull(persisted);
        Assert.Equal(NodeStatus.Online, persisted.Status);
        Assert.Equal(heartbeaten.Version, persisted.Version);
    }

    [Fact]
    public async Task Heartbeat_maps_every_resource_snapshot_field_onto_the_node_projection()
    {
        await _connection.InvokeAsync<NodeDto>(
            "Register", new NodeRegistrationMessage(_nodeId, "pi-hub-resources", "1.0.0", "{}"));
        var observedAt = new DateTimeOffset(2026, 9, 5, 15, 0, 0, TimeSpan.Zero);
        var resources = new NodeResourceSnapshotMessage(
            observedAt,
            CpuUsagePercent: 12.5,
            MemoryUsedBytes: 1024L * 1024L,
            MemoryTotalBytes: 2L * 1024L * 1024L,
            DiskUsedBytes: 3L * 1024L * 1024L,
            DiskTotalBytes: 4L * 1024L * 1024L,
            LoadAverageOneMinute: 0.5,
            UptimeSeconds: 3661d);

        var heartbeaten = await _connection.InvokeAsync<NodeDto>(
            "Heartbeat", new NodeHeartbeatMessage(_nodeId, ["session-a"], resources));

        Assert.NotNull(heartbeaten.Resources);
        Assert.Equal(observedAt, heartbeaten.Resources.ObservedAt);
        Assert.Equal(12.5, heartbeaten.Resources.CpuUsagePercent);
        Assert.Equal(1024L * 1024L, heartbeaten.Resources.MemoryUsedBytes);
        Assert.Equal(2L * 1024L * 1024L, heartbeaten.Resources.MemoryTotalBytes);
        Assert.Equal(3L * 1024L * 1024L, heartbeaten.Resources.DiskUsedBytes);
        Assert.Equal(4L * 1024L * 1024L, heartbeaten.Resources.DiskTotalBytes);
        Assert.Equal(0.5, heartbeaten.Resources.LoadAverageOneMinute);
        Assert.Equal(3661d, heartbeaten.Resources.UptimeSeconds);

        var cleared = await _connection.InvokeAsync<NodeDto>(
            "Heartbeat", new NodeHeartbeatMessage(_nodeId, ["session-a"], Resources: null));
        Assert.Null(cleared.Resources);
    }

    [Fact]
    public async Task Heartbeat_round_trips_execution_status_separately_from_resources()
    {
        await RegisterNodeAsync();
        var observedAt = new DateTimeOffset(2026, 9, 5, 16, 0, 0, TimeSpan.Zero);
        var assignmentIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        const string routingRevision = "routing-revision-1";
        var resources = new NodeResourceSnapshotMessage(
            observedAt,
            CpuUsagePercent: 25,
            MemoryUsedBytes: null,
            MemoryTotalBytes: null,
            DiskUsedBytes: null,
            DiskTotalBytes: null,
            LoadAverageOneMinute: null,
            UptimeSeconds: null);
        var executionStatus = new NodeExecutionStatusMessage(
            observedAt,
            AvailableRequestSlots: 2,
            assignmentIds,
            routingRevision,
            [
                new RuntimeRouteReadinessMessage(
                    "developer",
                    "codex/default",
                    RuntimeReadinessStatuses.Unknown,
                    RuntimeReadinessEvidenceSources.UnsupportedNativeObservation,
                    observedAt,
                    routingRevision),
            ]);

        var heartbeaten = await _connection.InvokeAsync<NodeDto>(
            "Heartbeat",
            new NodeHeartbeatMessage(_nodeId, ["session-a"], resources, executionStatus));

        Assert.NotNull(heartbeaten.Resources);
        Assert.Equal(25d, heartbeaten.Resources.CpuUsagePercent);
        Assert.NotNull(heartbeaten.ExecutionStatus);
        Assert.Equal(observedAt, heartbeaten.ExecutionStatus.ObservedAt);
        Assert.Equal(2, heartbeaten.ExecutionStatus.AvailableRequestSlots);
        Assert.Equal(assignmentIds, heartbeaten.ExecutionStatus.ActiveAssignmentIds);
        Assert.Equal(routingRevision, heartbeaten.ExecutionStatus.RoutingRevision);
        var route = Assert.Single(heartbeaten.ExecutionStatus.Routes);
        Assert.Equal("developer", route.Role);
        Assert.Equal("codex/default", route.CanonicalModel);
        Assert.Equal(RuntimeReadinessStatuses.Unknown, route.Readiness);
        Assert.Equal(RuntimeReadinessEvidenceSources.UnsupportedNativeObservation, route.EvidenceSource);
        Assert.Equal(observedAt, route.ObservedAt);
        Assert.Equal(routingRevision, route.RoutingRevision);

        var persisted = await GetNodeAsync(_nodeId);
        Assert.NotNull(persisted);
        Assert.Equal(heartbeaten.Resources, persisted.Resources);
        Assert.NotNull(persisted.ExecutionStatus);
        Assert.Equal(observedAt, persisted.ExecutionStatus.ObservedAt);
        Assert.Equal(2, persisted.ExecutionStatus.AvailableRequestSlots);
        Assert.Equal(assignmentIds, persisted.ExecutionStatus.ActiveAssignmentIds);
        Assert.Equal(routingRevision, persisted.ExecutionStatus.RoutingRevision);
        var persistedRoute = Assert.Single(persisted.ExecutionStatus.Routes);
        Assert.Equal(route, persistedRoute);
    }

    [Fact]
    public async Task Heartbeat_rejects_malformed_execution_status()
    {
        var registered = await RegisterNodeAsync();
        var malformed = new NodeExecutionStatusMessage(
            DateTimeOffset.UtcNow,
            AvailableRequestSlots: -1,
            ActiveAssignmentIds: [],
            RoutingRevision: "routing-revision-1",
            Routes: []);

        await Assert.ThrowsAnyAsync<HubException>(() => _connection.InvokeAsync<NodeDto>(
            "Heartbeat",
            new NodeHeartbeatMessage(_nodeId, [], ExecutionStatus: malformed)));

        var persisted = await GetNodeAsync(_nodeId);
        Assert.NotNull(persisted);
        Assert.Equal(registered.Version, persisted.Version);
    }

    [Fact]
    public async Task Unregistered_connection_cannot_invoke_later_hub_methods()
    {
        await using var connection = _fixture.CreateNodeHubConnection(_fixture.SecondaryNodeId);
        await connection.StartAsync();

        await Assert.ThrowsAnyAsync<HubException>(() => connection.InvokeAsync<NodeDto>(
            "Heartbeat", new NodeHeartbeatMessage(_fixture.SecondaryNodeId, [])));
        await Assert.ThrowsAnyAsync<HubException>(() => connection.InvokeAsync<NodeEventAcknowledgementMessage>(
            "PublishEvents", new NodeEventBatchMessage([])));
    }

    [Fact]
    public async Task Heartbeat_joins_only_sessions_owned_by_the_authenticated_node()
    {
        await RegisterNodeAsync();
        var owned = await SeedAssignmentAsync(WorkRequestKind.Development);
        // A dedicated node that never connects: seeded directly so the foreign assignment's
        // binding and node-snapshot foreign keys resolve against a real FleetNodes row.
        var foreignNodeId = Guid.NewGuid();
        var foreign = await SeedAssignmentAsync(WorkRequestKind.Development, foreignNodeId);
        var ownedReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var foreignReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = _connection.On<AgentMailMessage>("ReceiveMail", message =>
        {
            if (message.RecipientSessionId == owned.SessionId)
            {
                ownedReceived.TrySetResult();
            }

            if (message.RecipientSessionId == foreign.SessionId)
            {
                foreignReceived.TrySetResult();
            }
        });

        await _connection.InvokeAsync<NodeDto>(
            "Heartbeat",
            new NodeHeartbeatMessage(_nodeId, [owned.SessionId, foreign.SessionId]));

        using var scope = _fixture.Factory.Services.CreateScope();
        var hub = scope.ServiceProvider.GetRequiredService<IHubContext<NodeHub>>();
        await hub.Clients.Group("session:" + owned.SessionId).SendAsync(
            "ReceiveMail",
            MailFor(owned.ProjectId, owned.RequestId, owned.SessionId));
        await ownedReceived.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await hub.Clients.Group("session:" + foreign.SessionId).SendAsync(
            "ReceiveMail",
            MailFor(foreign.ProjectId, foreign.RequestId, foreign.SessionId));
        await Task.Delay(100);
        Assert.False(foreignReceived.Task.IsCompleted);
    }

    [Fact]
    public async Task Node_id_messages_reject_a_foreign_authenticated_identity()
    {
        var foreignNodeId = _fixture.SecondaryNodeId;

        await Assert.ThrowsAnyAsync<HubException>(() => _connection.InvokeAsync<NodeDto>(
            "Register", new NodeRegistrationMessage(foreignNodeId, "foreign", "1.0.0", "{}")));
        Assert.Null(await GetNodeAsync(foreignNodeId));

        await RegisterNodeAsync();
        await Assert.ThrowsAnyAsync<HubException>(() => _connection.InvokeAsync<NodeDto>(
            "Register", new NodeRegistrationMessage(foreignNodeId, "foreign", "1.0.0", "{}")));

        await Assert.ThrowsAnyAsync<HubException>(() => _connection.InvokeAsync<NodeDto>(
            "Heartbeat", new NodeHeartbeatMessage(foreignNodeId, ["foreign-session"])));
        await Assert.ThrowsAnyAsync<HubException>(() => _connection.InvokeAsync<ExecutionAssignmentMessage?>(
            "ClaimNext", new ClaimRequestMessage(foreignNodeId, LeaseSeconds: 60)));
        await Assert.ThrowsAnyAsync<HubException>(() => _connection.InvokeAsync<DateTimeOffset?>(
            "RenewClaim", new ClaimRenewalMessage(Guid.NewGuid(), foreignNodeId, "foreign-token", LeaseSeconds: 60)));
        await Assert.ThrowsAnyAsync<HubException>(() => _connection.InvokeAsync<NodeEventAcknowledgementMessage>(
            "PublishEvents",
            new NodeEventBatchMessage(
            [
                new NodeEventMessage(
                    "evt-foreign",
                    foreignNodeId,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "foreign-token",
                    "foreign-session",
                    1,
                    "session.log",
                    DateTimeOffset.UtcNow,
                    "{}"),
            ])));

        var heartbeaten = await _connection.InvokeAsync<NodeDto>(
            "Heartbeat", new NodeHeartbeatMessage(_nodeId, []));
        Assert.Equal(_nodeId, heartbeaten.Id);
        Assert.Equal("pi-hub-claim", heartbeaten.DisplayName);
    }

    [Fact]
    public async Task ClaimNext_requires_successful_reconciliation_on_the_connection()
    {
        await RegisterNodeAsync();
        var now = DateTimeOffset.UtcNow;
        await _connection.InvokeAsync<NodeDto>(
            "Heartbeat",
            new NodeHeartbeatMessage(
                _nodeId,
                [],
                ExecutionStatus: new NodeExecutionStatusMessage(
                    now,
                    AvailableRequestSlots: 1024,
                    ActiveAssignmentIds: [],
                    RoutingRevision: "routing-v1",
                    Routes:
                    [
                        new RuntimeRouteReadinessMessage(
                            "implementer",
                            "codex/default",
                            RuntimeReadinessStatuses.Ready,
                            "runtime-adapter",
                            now,
                            "routing-v1"),
                    ])));
        var requestId = await SeedRequestAsync(priority: RequestPriority.High);

        await Assert.ThrowsAnyAsync<HubException>(() =>
            _connection.InvokeAsync<ExecutionAssignmentMessage?>(
                "ClaimNext",
                new ClaimRequestMessage(_nodeId, LeaseSeconds: 60)));

        var reconciled = await _connection.InvokeAsync<ReconcileAssignmentsResultMessage>(
            "ReconcileAssignments",
            new ReconcileAssignmentsMessage(_nodeId, LeaseSeconds: 60, Assignments: []));
        Assert.All(reconciled.Assignments, recovered =>
        {
            Assert.Equal(AssignmentReconciliationDisposition.RecoveryRequired, recovered.Disposition);
            Assert.NotEqual(requestId, recovered.RequestId);
        });

        var assignment = await _connection.InvokeAsync<ExecutionAssignmentMessage?>(
            "ClaimNext",
            new ClaimRequestMessage(_nodeId, LeaseSeconds: 60));

        Assert.NotNull(assignment);
        Assert.Equal(requestId, assignment.RequestId);
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var request = await db.WorkRequests.SingleAsync(
            candidate => candidate.Id == new WorkRequestId(requestId));
        Assert.Equal(WorkRequestStatus.Starting, request.Status);
        var persistedAssignment = await db.ExecutionAssignments.SingleAsync(
            candidate => candidate.RequestId == new WorkRequestId(requestId));
        Assert.Equal(ExecutionAssignmentState.Starting, persistedAssignment.State);
    }

    [Fact]
    public async Task RenewClaim_extends_a_seeded_assignment_lease_and_returns_null_for_a_wrong_token()
    {
        await RegisterNodeAsync();
        var assignment = await SeedAssignmentAsync(WorkRequestKind.Analysis);
        var renewedExpiry = await _connection.InvokeAsync<DateTimeOffset?>(
            "RenewClaim",
            new ClaimRenewalMessage(
                assignment.RequestId,
                _nodeId,
                assignment.ClaimToken,
                LeaseSeconds: 60));

        Assert.True(renewedExpiry >= assignment.LeaseExpiresAt);

        var rejected = await _connection.InvokeAsync<DateTimeOffset?>(
            "RenewClaim",
            new ClaimRenewalMessage(
                assignment.RequestId,
                _nodeId,
                "wrong-token",
                LeaseSeconds: 60));
        Assert.Null(rejected);
    }

    [Fact]
    public async Task PublishEvents_allows_terminal_duplicates_and_completion_tail_but_rejects_new_work()
    {
        await RegisterNodeAsync();
        var assignment = await SeedAssignmentAsync(WorkRequestKind.Development);
        var activeEvent = CreateEvent(assignment, assignment.SessionId, 1, "session.log");
        var batch = new NodeEventBatchMessage([activeEvent]);

        var ack = await _connection.InvokeAsync<NodeEventAcknowledgementMessage>("PublishEvents", batch);
        Assert.Equal(new[] { activeEvent.EventId }, ack.EventIds);

        await CompleteAssignmentAsync(assignment.RequestId);

        var replayAck = await _connection.InvokeAsync<NodeEventAcknowledgementMessage>("PublishEvents", batch);
        Assert.Equal(ack.EventIds, replayAck.EventIds);

        var finalEvents = new[]
        {
            CreateEvent(assignment, assignment.SessionId, 2, "session.closed"),
            CreateEvent(assignment, assignment.SessionId, 3, "tool.completed"),
            CreateEvent(assignment, assignment.SessionId, 4, "turn.completed"),
        };
        var finalAck = await _connection.InvokeAsync<NodeEventAcknowledgementMessage>(
            "PublishEvents",
            new NodeEventBatchMessage(finalEvents));
        Assert.Equal(finalEvents.Select(@event => @event.EventId), finalAck.EventIds);

        var forbiddenEvents = new[]
        {
            CreateEvent(assignment, assignment.SessionId, 5, "tool.started"),
            CreateEvent(assignment, assignment.SessionId, 6, "session.registered"),
        };
        foreach (var forbiddenEvent in forbiddenEvents)
        {
            var error = await Assert.ThrowsAnyAsync<HubException>(() =>
                _connection.InvokeAsync<NodeEventAcknowledgementMessage>(
                    "PublishEvents",
                    new NodeEventBatchMessage([forbiddenEvent])));
            Assert.Contains("event_type_forbidden", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(assignment.ClaimToken, error.Message, StringComparison.Ordinal);
        }

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        Assert.True(await db.SessionEvents.AnyAsync(@event => @event.EventId == activeEvent.EventId));
        Assert.All(
            finalEvents,
            @event => Assert.Contains(
                db.SessionEvents,
                candidate => candidate.EventId == @event.EventId));
        Assert.All(
            forbiddenEvents,
            @event => Assert.DoesNotContain(
                db.SessionEvents,
                candidate => candidate.EventId == @event.EventId));
    }

    [Fact]
    public async Task PublishEvents_registers_root_and_child_before_authorizing_same_batch_follow_up()
    {
        await RegisterNodeAsync();
        var assignment = await SeedAssignmentAsync(WorkRequestKind.Development, seedSession: false);
        var childSessionId = $"pi-child-{assignment.RequestId:N}-{Guid.NewGuid():N}";
        var events = new[]
        {
            CreateEvent(assignment, assignment.SessionId, 0, "session.registered"),
            CreateEvent(assignment, assignment.SessionId, 1, "turn.started"),
            CreateEvent(assignment, childSessionId, 0, "session.registered"),
            CreateEvent(assignment, childSessionId, 1, "tool.started"),
        };

        var ack = await _connection.InvokeAsync<NodeEventAcknowledgementMessage>(
            "PublishEvents",
            new NodeEventBatchMessage(events));

        Assert.Equal(events.Select(@event => @event.EventId), ack.EventIds);
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        Assert.Equal(
            events.Select(@event => @event.EventId).Order(StringComparer.Ordinal),
            await db.SessionEvents
                .Where(@event => @event.RequestId == assignment.RequestId)
                .Select(@event => @event.EventId)
                .OrderBy(eventId => eventId)
                .ToArrayAsync());
        Assert.Equal(
            new[] { assignment.SessionId, childSessionId }.Order(StringComparer.Ordinal),
            await db.AgentSessions
                .Where(session => session.RequestId == assignment.RequestId)
                .Select(session => session.Id)
                .OrderBy(sessionId => sessionId)
                .ToArrayAsync());
    }

    [Fact]
    public async Task PublishEvents_rejects_a_session_registered_to_two_assignments_in_one_batch()
    {
        await RegisterNodeAsync();
        var first = await SeedAssignmentAsync(WorkRequestKind.Development, seedSession: false);
        var second = await SeedAssignmentAsync(WorkRequestKind.Analysis, seedSession: false);
        var sessionId = $"pi-child-{first.RequestId:N}-{Guid.NewGuid():N}";
        var registrations = new[]
        {
            CreateEvent(first, sessionId, 0, "session.registered"),
            CreateEvent(second, sessionId, 0, "session.registered"),
        };

        var error = await Assert.ThrowsAnyAsync<HubException>(() =>
            _connection.InvokeAsync<NodeEventAcknowledgementMessage>(
                "PublishEvents",
                new NodeEventBatchMessage(registrations)));

        Assert.Contains("session_mismatch", error.Message, StringComparison.Ordinal);
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        Assert.False(await db.AgentSessions.AnyAsync(session => session.Id == sessionId));
        Assert.False(await db.SessionEvents.AnyAsync(@event => @event.SessionId == sessionId));
    }

    [Fact]
    public async Task AllocateAgentIdentity_accepts_a_pre_session_exact_fence_and_rejects_mismatches()
    {
        await RegisterNodeAsync();
        var assignment = await SeedAssignmentAsync(WorkRequestKind.Development, seedSession: false);
        var exact = new AllocateAgentIdentityMessage(
            assignment.ProjectId,
            assignment.RequestId,
            assignment.ClaimToken,
            assignment.SessionId,
            "reviewer",
            "reviewer",
            "pi");

        await Assert.ThrowsAnyAsync<HubException>(() =>
            _connection.InvokeAsync<AgentIdentityMessage>(
                "AllocateAgentIdentity",
                exact with { ClaimToken = "wrong-token" }));
        await Assert.ThrowsAnyAsync<HubException>(() =>
            _connection.InvokeAsync<AgentIdentityMessage>(
                "AllocateAgentIdentity",
                exact with { ProjectId = Guid.NewGuid() }));
        await Assert.ThrowsAnyAsync<HubException>(() =>
            _connection.InvokeAsync<AgentIdentityMessage>(
                "AllocateAgentIdentity",
                exact with { RequestId = Guid.NewGuid() }));

        var allocated = await _connection.InvokeAsync<AgentIdentityMessage>(
            "AllocateAgentIdentity",
            exact);

        Assert.Equal(assignment.ProjectId, allocated.ProjectId);
        Assert.Equal(assignment.SessionId, allocated.SessionId);
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        Assert.False(await db.AgentSessions.AnyAsync(session => session.Id == assignment.SessionId));
    }

    [Fact]
    public async Task PublishEvents_rejects_batches_over_the_transport_limit()
    {
        await RegisterNodeAsync();
        var oversized = new NodeEventBatchMessage(Enumerable.Range(0, 501)
            .Select(i => new NodeEventMessage(
                "evt-oversize-" + i, _nodeId, Guid.NewGuid(), Guid.NewGuid(), "oversized-fixture-token", null, i, "t", DateTimeOffset.UtcNow, "{}"))
            .ToList());

        await Assert.ThrowsAnyAsync<HubException>(() =>
            _connection.InvokeAsync<NodeEventAcknowledgementMessage>("PublishEvents", oversized));
    }

    private NodeEventMessage CreateEvent(
        (
            Guid ProjectId,
            Guid RequestId,
            string ClaimToken,
            DateTimeOffset LeaseExpiresAt,
            string SessionId) assignment,
        string sessionId,
        long sequence,
        string type) => new(
            $"{sessionId}-{sequence}-{type}",
            _nodeId,
            assignment.ProjectId,
            assignment.RequestId,
            assignment.ClaimToken,
            sessionId,
            sequence,
            type,
            DateTimeOffset.UtcNow,
            "{}");

    private static AgentMailMessage MailFor(Guid projectId, Guid requestId, string recipientSessionId) => new(
        "mail-" + Guid.NewGuid().ToString("N"),
        projectId,
        requestId,
        "thread-" + requestId.ToString("N"),
        null,
        true,
        recipientSessionId,
        "Heartbeat routing",
        "Routing probe",
        MailImportance.Normal,
        AcknowledgementRequired: false,
        DateTimeOffset.UtcNow,
        ReadAtUtc: null,
        AcknowledgedAtUtc: null);

    private Task<NodeDto> RegisterNodeAsync() => _connection.InvokeAsync<NodeDto>(
        "Register", new NodeRegistrationMessage(_nodeId, "pi-hub-claim", "1.0.0", "{}"));

    private async Task<NodeDto?> GetNodeAsync(Guid id)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<INodeRegistry>();
        return await registry.GetAsync(new NodeId(id));
    }

    private async Task<Guid> SeedRequestAsync(
        RequestPriority priority = RequestPriority.Normal,
        WorkRequestKind kind = WorkRequestKind.Development)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var now = DateTimeOffset.UtcNow;
        var project = CreateProject(now);
        var binding = CreateValidBinding(project, new NodeId(_nodeId), now);
        var request = WorkRequest.Enqueue(project.Id, kind, priority, RiskLevel.Standard,
            "Hub request", "Do hub work", now);
        db.Projects.Add(project);
        db.WorkspaceBindings.Add(binding);
        db.WorkRequests.Add(request);
        await db.SaveChangesAsync();
        return request.Id.Value;
    }

    private async Task<(
        Guid ProjectId,
        Guid RequestId,
        string ClaimToken,
        DateTimeOffset LeaseExpiresAt,
        string SessionId)> SeedAssignmentAsync(
        WorkRequestKind kind,
        Guid? assignedNodeId = null,
        bool seedSession = true)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var now = DateTimeOffset.UtcNow;
        var nodeId = new NodeId(assignedNodeId ?? _nodeId);
        // The binding and the assignment's node snapshot both carry a real foreign key into
        // FleetNodes, so a foreign owner must exist as a node row even though it never connects.
        if (!await db.FleetNodes.AnyAsync(node => node.Id == nodeId))
        {
            db.FleetNodes.Add(FleetNode.Register(nodeId, "hub-foreign-node", "1.0.0", "{}", now));
        }

        var project = CreateProject(now);
        var binding = CreateValidBinding(project, nodeId, now);
        var request = WorkRequest.Enqueue(
            project.Id,
            kind,
            RequestPriority.Normal,
            RiskLevel.Standard,
            "Hub request",
            "Do hub work",
            now);
        request.Start(now);
        var claimToken = "hub-assignment-" + Guid.NewGuid().ToString("N");
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
            TimeSpan.FromSeconds(60));
        var sessionId = $"pi-root-{request.Id.Value:N}-{Guid.NewGuid():N}";
        db.Projects.Add(project);
        db.WorkspaceBindings.Add(binding);
        db.WorkRequests.Add(request);
        db.ExecutionAssignments.Add(assignment);
        if (seedSession)
        {
            db.AgentSessions.Add(new AgentSessionRow
            {
                Id = sessionId,
                ProjectId = project.Id.Value,
                RequestId = request.Id.Value,
                AgentName = sessionId,
                Role = "root",
                Runtime = "pi",
                Model = "codex/default",
                Liveness = nameof(AgentLiveness.Online),
                Activity = nameof(AgentActivity.Idle),
                Attention = "None",
                WorkState = nameof(AgentWorkState.Executing),
                StatusReason = "Seeded for hub assignment tests",
                StartedAtUtcTicks = now.UtcTicks,
                Version = 1,
            });
        }
        await db.SaveChangesAsync();
        return (
            project.Id.Value,
            request.Id.Value,
            claimToken,
            assignment.LeaseExpiresAt,
            sessionId);
    }

    private async Task CompleteAssignmentAsync(Guid requestId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var assignment = await db.ExecutionAssignments.SingleAsync(
            candidate => candidate.RequestId == new WorkRequestId(requestId));
        var now = DateTimeOffset.UtcNow;
        assignment.MarkRunning(now);
        assignment.BeginFinalizing(now);
        assignment.Complete(now);
        await db.SaveChangesAsync();
    }

    private static WorkspaceBinding CreateValidBinding(
        Project project,
        NodeId nodeId,
        DateTimeOffset now)
    {
        var repositoryPath = Path.Combine(
            Path.GetTempPath(),
            "pi-cc-integration",
            Guid.NewGuid().ToString("N"));
        var binding = WorkspaceBinding.Designate(project.Id, nodeId, repositoryPath, now);
        Assert.True(binding.ApplyValidationResult(
            nodeId,
            binding.ValidationRevision,
            WorkspaceBindingStatus.Valid,
            WorkspaceBinding.ValidValidationCode,
            "Seeded for hub assignment tests.",
            repositoryPath,
            now));
        return binding;
    }

    private static Project CreateProject(DateTimeOffset now) => Project.Register(
        "Hub project " + Guid.NewGuid().ToString("N")[..6],
        "main",
        enabled: true,
        maxActiveWriteRequests: 2,
        maxReadOnlyRequests: 4,
        maxChildAgentsPerRequest: 1,
        requireCleanStart: false,
        createRequestBranch: true,
        createRequestCommit: true,
        autoMerge: false,
        now);
}
