using PiCommandCenter.Application.Requests;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Requests;

namespace PiCommandCenter.Infrastructure.Tests.Requests;

public sealed class AssignmentOperationAuthorizerTests : IDisposable
{
    private const string ClaimToken = "assignment-token";
    private const string OwnedSessionId = "pi-root-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset AssignedAt = new(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);

    private readonly string _sqlitePath = TestRepositories.CreateSqliteFile();

    [Fact]
    public async Task Active_operation_accepts_the_matching_assignment_and_session()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Running, [OwnedSessionId]);
        await SaveAsync(db);

        await CreateAuthorizer(db).RequireActiveAsync(
            world.Node.Id,
            world.Request.Id,
            world.Project.Id,
            ClaimToken,
            OwnedSessionId);
    }

    [Theory]
    [InlineData("node", "node_mismatch")]
    [InlineData("token", "token_mismatch")]
    [InlineData("project", "project_mismatch")]
    [InlineData("request", "assignment_missing")]
    [InlineData("session", "session_mismatch")]
    public async Task Active_operation_rejects_foreign_correlations_with_stable_codes(
        string foreignCorrelation,
        string expectedCode)
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Running, [OwnedSessionId]);
        await SaveAsync(db);

        var nodeId = foreignCorrelation == "node" ? NodeId.New() : world.Node.Id;
        var requestId = foreignCorrelation == "request" ? WorkRequestId.New() : world.Request.Id;
        var projectId = foreignCorrelation == "project" ? ProjectId.New() : world.Project.Id;
        var claimToken = foreignCorrelation == "token" ? "foreign-token" : ClaimToken;
        var sessionId = foreignCorrelation == "session" ? "session-foreign" : OwnedSessionId;

        await AssertDeniedAsync(expectedCode, () => CreateAuthorizer(db).RequireActiveAsync(
            nodeId,
            requestId,
            projectId,
            claimToken,
            sessionId));
    }

    [Theory]
    [InlineData(ExecutionAssignmentState.RecoveryRequired)]
    [InlineData(ExecutionAssignmentState.Completed)]
    [InlineData(ExecutionAssignmentState.Failed)]
    [InlineData(ExecutionAssignmentState.Cancelled)]
    public async Task Active_operation_rejects_recovery_and_terminal_assignments(
        ExecutionAssignmentState state)
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, state, [OwnedSessionId]);
        await SaveAsync(db);

        await AssertDeniedAsync("state_forbidden", () => CreateAuthorizer(db).RequireActiveAsync(
            world.Node.Id,
            world.Request.Id,
            world.Project.Id,
            ClaimToken,
            OwnedSessionId));
    }

    [Theory]
    [InlineData("session.closed")]
    [InlineData("session.failed")]
    [InlineData("session.cancelled")]
    [InlineData("session.completed")]
    [InlineData("child.completed")]
    [InlineData("tool.completed")]
    [InlineData("turn.completed")]
    [InlineData("request.completed")]
    [InlineData("request.failed")]
    [InlineData("request.cancelled")]
    public async Task Historical_operation_accepts_a_final_event_from_a_recorded_session(
        string eventType)
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Completed, [OwnedSessionId]);
        await SaveAsync(db);

        await AuthorizeEventAsync(
            db,
            world,
            OwnedSessionId,
            EventId(OwnedSessionId, 2, eventType),
            eventType);
    }

    [Fact]
    public async Task Historical_operation_rejects_a_new_mutating_event_after_terminalization()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Completed, [OwnedSessionId]);
        await SaveAsync(db);

        await AssertDeniedAsync("event_type_forbidden", () =>
            AuthorizeEventAsync(
                db,
                world,
                OwnedSessionId,
                EventId(OwnedSessionId, 2, "tool.started"),
                "tool.started"));
    }

    [Fact]
    public async Task Historical_operation_accepts_an_exact_known_duplicate()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Completed, [OwnedSessionId]);
        SeedEvent(db, world, "event-duplicate", "tool.started", OwnedSessionId);
        await SaveAsync(db);

        await AuthorizeEventAsync(
            db,
            world,
            OwnedSessionId,
            "event-duplicate",
            "tool.started");
    }

    [Fact]
    public async Task Historical_operation_rejects_a_mismatched_known_duplicate()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Completed, [OwnedSessionId]);
        SeedEvent(db, world, "event-duplicate", "tool.started", OwnedSessionId);
        await SaveAsync(db);

        await AssertDeniedAsync("event_mismatch", () =>
            AuthorizeEventAsync(
                db,
                world,
                OwnedSessionId,
                "event-duplicate",
                "session.closed"));
    }

    [Fact]
    public async Task Active_registration_authorizes_root_and_child_follow_up_events_in_the_same_batch()
    {
        const string childSessionId = "pi-child-cccccccccccccccccccccccccccccccc-dddddddddddddddddddddddddddddddd";
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Starting, []);
        await SaveAsync(db);

        await CreateAuthorizer(db).RequireHistoricalEventsAsync(
            world.Node.Id,
            [
                Event(world, OwnedSessionId, 0, "session.registered"),
                Event(world, OwnedSessionId, 1, "turn.started"),
                Event(world, childSessionId, 0, "session.registered"),
                Event(world, childSessionId, 1, "tool.started"),
            ]);
    }

    [Theory]
    [InlineData("node", "node_mismatch")]
    [InlineData("token", "token_mismatch")]
    [InlineData("project", "project_mismatch")]
    [InlineData("request", "assignment_missing")]
    public async Task Active_registration_requires_the_exact_assignment_fence(
        string foreignCorrelation,
        string expectedCode)
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Running, []);
        await SaveAsync(db);
        var requestId = foreignCorrelation == "request" ? WorkRequestId.New() : world.Request.Id;
        var projectId = foreignCorrelation == "project" ? ProjectId.New() : world.Project.Id;
        var claimToken = foreignCorrelation == "token" ? "foreign-token" : ClaimToken;
        var nodeId = foreignCorrelation == "node" ? NodeId.New() : world.Node.Id;

        await AssertDeniedAsync(expectedCode, () =>
            CreateAuthorizer(db).RequireHistoricalEventsAsync(
                nodeId,
                [
                    new AssignmentEventAuthorizationRequest(
                        requestId,
                        projectId,
                        claimToken,
                        OwnedSessionId,
                        EventId(OwnedSessionId, 0, "session.registered"),
                        "session.registered"),
                ]));
    }

    [Fact]
    public async Task Active_registration_rejects_a_session_recorded_for_another_assignment()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Running, []);
        SeedAssignment(db, ExecutionAssignmentState.Running, [OwnedSessionId]);
        await SaveAsync(db);

        await AssertDeniedAsync("session_mismatch", () =>
            CreateAuthorizer(db).RequireHistoricalEventsAsync(
                world.Node.Id,
                [Event(world, OwnedSessionId, 0, "session.registered")]));
    }

    [Fact]
    public async Task Active_registration_rejects_a_same_batch_session_collision()
    {
        await using var db = CreateContext();
        var first = SeedAssignment(db, ExecutionAssignmentState.Running, []);
        var second = SeedAssignment(db, ExecutionAssignmentState.Running, [], first.Node);
        await SaveAsync(db);

        await AssertDeniedAsync("session_mismatch", () =>
            CreateAuthorizer(db).RequireHistoricalEventsAsync(
                first.Node.Id,
                [
                    Event(first, OwnedSessionId, 0, "session.registered"),
                    Event(second, OwnedSessionId, 0, "session.registered"),
                ]));
    }

    [Fact]
    public async Task Event_id_bound_covers_the_maximum_supervisor_format_and_rejects_longer_ids()
    {
        var sessionId = new string('s', 128);
        var eventType = new string('t', 64);
        var producerEventId = EventId(sessionId, long.MaxValue, eventType);
        Assert.True(producerEventId.Length <= AssignmentOperationLimits.MaxEventIdLength);

        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Running, [sessionId]);
        await SaveAsync(db);

        await AuthorizeEventAsync(db, world, sessionId, producerEventId, eventType);
        await AssertDeniedAsync("invalid_input", () =>
            AuthorizeEventAsync(
                db,
                world,
                sessionId,
                new string('e', AssignmentOperationLimits.MaxEventIdLength + 1),
                eventType));
    }

    [Fact]
    public async Task Terminal_history_requires_a_recorded_session()
    {
        await using var db = CreateContext();
        var world = SeedAssignment(db, ExecutionAssignmentState.Completed, []);
        await SaveAsync(db);

        await AssertDeniedAsync("session_mismatch", () =>
            AuthorizeEventAsync(
                db,
                world,
                null,
                EventId(OwnedSessionId, 2, "tool.completed"),
                "tool.completed"));
    }

    [Fact]
    public async Task Heartbeat_filter_returns_only_sessions_on_active_assignments_owned_by_the_node()
    {
        await using var db = CreateContext();
        var owned = SeedAssignment(
            db,
            ExecutionAssignmentState.Running,
            [OwnedSessionId, "session-owned-child"]);
        SeedAssignment(
            db,
            ExecutionAssignmentState.RecoveryRequired,
            ["session-recovery"],
            owned.Node);
        SeedAssignment(
            db,
            ExecutionAssignmentState.Running,
            ["session-foreign-node"]);
        await SaveAsync(db);

        var filtered = await CreateAuthorizer(db).FilterHeartbeatSessionsAsync(
            owned.Node.Id,
            [
                "session-unknown",
                "session-owned-child",
                "session-recovery",
                "session-foreign-node",
                OwnedSessionId,
            ]);

        Assert.Equal(
            new[] { OwnedSessionId, "session-owned-child" },
            filtered.Order(StringComparer.Ordinal).ToArray());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_sqlitePath)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ControlPlaneDbContext CreateContext() => TestRepositories.CreateContext(_sqlitePath);

    private static AssignmentOperationAuthorizer CreateAuthorizer(ControlPlaneDbContext db) => new(db);

    private static Task AuthorizeEventAsync(
        ControlPlaneDbContext db,
        AssignmentWorld world,
        string? sessionId,
        string eventId,
        string eventType) =>
        CreateAuthorizer(db).RequireHistoricalEventsAsync(
            world.Node.Id,
            [
                new AssignmentEventAuthorizationRequest(
                    world.Request.Id,
                    world.Project.Id,
                    ClaimToken,
                    sessionId,
                    eventId,
                    eventType),
            ]);

    private static AssignmentEventAuthorizationRequest Event(
        AssignmentWorld world,
        string sessionId,
        long sequence,
        string eventType) => new(
            world.Request.Id,
            world.Project.Id,
            ClaimToken,
            sessionId,
            EventId(sessionId, sequence, eventType),
            eventType);

    private static string EventId(string sessionId, long sequence, string eventType) =>
        $"{sessionId}-{sequence}-{eventType}";

    private static AssignmentWorld SeedAssignment(
        ControlPlaneDbContext db,
        ExecutionAssignmentState state,
        IReadOnlyCollection<string> sessionIds,
        FleetNode? existingNode = null)
    {
        var nodeId = existingNode?.Id ?? NodeId.New();
        var node = existingNode ?? FleetNode.Register(
            nodeId,
            $"node-{nodeId}",
            "1.0.0",
            "{}",
            AssignedAt);
        if (existingNode is null)
        {
            db.FleetNodes.Add(node);
        }

        var project = Project.Register(
            "Fleet project",
            "main",
            enabled: true,
            maxActiveWriteRequests: 1,
            maxReadOnlyRequests: 1,
            maxChildAgentsPerRequest: 2,
            requireCleanStart: false,
            createRequestBranch: false,
            createRequestCommit: false,
            autoMerge: false,
            AssignedAt);
        var request = WorkRequest.Enqueue(
            project.Id,
            WorkRequestKind.Development,
            RequestPriority.Normal,
            RiskLevel.Standard,
            "Assigned work",
            "Complete the assigned work.",
            AssignedAt);
        request.Start(AssignedAt);
        var repositoryPath = Path.Combine(Path.GetTempPath(), request.Id.ToString());
        var binding = WorkspaceBinding.Designate(project.Id, node.Id, repositoryPath, AssignedAt);
        var terminalAt = state is ExecutionAssignmentState.Completed
            or ExecutionAssignmentState.Failed
            or ExecutionAssignmentState.Cancelled
                ? AssignedAt.AddMinutes(2)
                : (DateTimeOffset?)null;
        var assignment = ExecutionAssignment.Rehydrate(
            request.Id,
            project.Id,
            binding.Id,
            node.Id,
            repositoryPath,
            project.DefaultBranch,
            binding.ValidationRevision,
            state,
            ClaimToken,
            AssignedAt,
            AssignedAt.AddMinutes(5),
            lastRenewedAt: null,
            lastReconciledAt: null,
            terminalAt,
            version: 1);

        db.Projects.Add(project);
        db.WorkspaceBindings.Add(binding);
        db.WorkRequests.Add(request);
        db.ExecutionAssignments.Add(assignment);
        db.AgentSessions.AddRange(sessionIds.Select(sessionId => Session(sessionId, project.Id, request.Id)));
        return new AssignmentWorld(node, project, request);
    }

    private static AgentSessionRow Session(
        string sessionId,
        ProjectId projectId,
        WorkRequestId requestId) => new()
    {
        Id = sessionId,
        ProjectId = projectId.Value,
        RequestId = requestId.Value,
        AgentName = sessionId,
        Role = "implementer",
        Runtime = "pi",
        Model = "model",
        Liveness = "Online",
        Activity = "Responding",
        Attention = "None",
        WorkState = "Executing",
        StatusReason = string.Empty,
        StartedAtUtcTicks = AssignedAt.UtcTicks,
        LastSequence = 1,
        Version = 1,
    };

    private static void SeedEvent(
        ControlPlaneDbContext db,
        AssignmentWorld world,
        string eventId,
        string eventType,
        string sessionId)
    {
        db.SessionEvents.Add(new SessionEvent
        {
            EventId = eventId,
            NodeId = world.Node.Id.Value,
            ProjectId = world.Project.Id.Value,
            RequestId = world.Request.Id.Value,
            SessionId = sessionId,
            Sequence = 1,
            Type = eventType,
            OccurredAtUtcTicks = AssignedAt.UtcTicks,
            ReceivedAtUtcTicks = AssignedAt.UtcTicks,
            PayloadJson = "{}",
        });
    }

    private static async Task SaveAsync(ControlPlaneDbContext db)
    {
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task AssertDeniedAsync(string expectedCode, Func<Task> operation)
    {
        var exception = await Assert.ThrowsAsync<AssignmentAuthorizationException>(operation);
        Assert.Equal(expectedCode, exception.Code);
    }

    private sealed record AssignmentWorld(
        FleetNode Node,
        Project Project,
        WorkRequest Request);
}
