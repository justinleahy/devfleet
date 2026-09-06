using Microsoft.AspNetCore.Hosting;
using PiCommandCenter.ControlPlane.Security;
using PiCommandCenter.Infrastructure.Security;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Node;

namespace PiCommandCenter.EndToEndTests;

/// <summary>
/// A node claims only after reporting its durable inventory, then survives a Control Plane
/// restart with the same assignment fence. Its stranded event is replayed idempotently before
/// another claim is attempted, and project capacity prevents a second writer. Uses two
/// sequential Control Plane hosts over one persisted SQLite database and the real node spool.
/// </summary>
public sealed class NodeReplayEndToEndTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "pi-cc-e2e-replay", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Reconciled_assignment_and_spooled_event_survive_restart_without_duplicates()
    {
        Directory.CreateDirectory(_tempRoot);
        var sqlitePath = Path.Combine(_tempRoot, "controlplane.db");
        var spoolPath = Path.Combine(_tempRoot, "spool.db");
        var auth = AuthTestMaterial.WriteTo(Path.Combine(_tempRoot, "auth"));
        var nodeId = auth.AuthenticatedNodeId;

        ExecutionAssignmentMessage assignment = null!;
        NodeEventMessage message = null!;
        Guid blockedRequestId = default;
        using (var first = CreateControlPlane(sqlitePath, auth))
        {
            await using var connection = await ConnectAsync(first, auth.NodeTokenHex);
            await connection.InvokeAsync<NodeDto>(
                "Register", new NodeRegistrationMessage(nodeId, "pi-replay", "1.0.0", "{}"));

            var seeded = await SeedQueuedRequestsAsync(
                first,
                nodeId,
                Path.Combine(_tempRoot, "workspace"));
            blockedRequestId = seeded.BlockedRequestId;
            await PublishExecutionStatusAsync(connection, nodeId, []);

            var initialReconciliation =
                await connection.InvokeAsync<ReconcileAssignmentsResultMessage>(
                    "ReconcileAssignments",
                    new ReconcileAssignmentsMessage(nodeId, LeaseSeconds: 300, Assignments: []));
            Assert.Empty(initialReconciliation.Assignments);

            var claimed = await connection.InvokeAsync<ExecutionAssignmentMessage?>(
                "ClaimNext",
                new ClaimRequestMessage(nodeId, LeaseSeconds: 300));
            assignment = Assert.IsType<ExecutionAssignmentMessage>(claimed);
            Assert.Equal(seeded.ProjectId, assignment.ProjectId);
            Assert.Equal(seeded.RequestId, assignment.RequestId);
            await SeedRootSessionAsync(first, assignment, seeded.SessionId);

            message = new NodeEventMessage(
                EventId: "evt-replay-1",
                NodeId: nodeId,
                ProjectId: assignment.ProjectId,
                RequestId: assignment.RequestId,
                ClaimToken: assignment.ClaimToken,
                SessionId: seeded.SessionId,
                Sequence: 1,
                Type: "session.log",
                OccurredAt: DateTimeOffset.UtcNow,
                PayloadJson: "{\"line\":\"before restart\"}");

            await using (var spool = CreateSpool(spoolPath))
            {
                await spool.AppendAsync(message, CancellationToken.None);
            }

            // The server acknowledges the event, but the node crashes before deleting its
            // durable spool row.
            await using (var spool = CreateSpool(spoolPath))
            {
                var pending = await spool.PeekPendingAsync(100, CancellationToken.None);
                Assert.Equal(message, Assert.Single(pending));
                var acknowledgement =
                    await connection.InvokeAsync<NodeEventAcknowledgementMessage>(
                        "PublishEvents",
                        new NodeEventBatchMessage(pending));
                Assert.Equal([message.EventId], acknowledgement.EventIds);
            }

            Assert.Equal(1, CountServerEvents(sqlitePath, message.EventId));
            Assert.Equal([message.EventId], await PendingIdsAsync(CreateSpool(spoolPath)));
        }

        using (var second = CreateControlPlane(sqlitePath, auth))
        {
            await using var connection = await ConnectAsync(second, auth.NodeTokenHex);
            var registered = await connection.InvokeAsync<NodeDto>(
                "Register", new NodeRegistrationMessage(nodeId, "pi-replay", "1.0.0", "{}"));
            Assert.Equal(NodeStatus.Online, registered.Status);
            await PublishExecutionStatusAsync(connection, nodeId, [assignment.RequestId]);

            var reconciliation =
                await connection.InvokeAsync<ReconcileAssignmentsResultMessage>(
                    "ReconcileAssignments",
                    new ReconcileAssignmentsMessage(
                        nodeId,
                        LeaseSeconds: 300,
                        Assignments:
                        [
                            new ExecutionAssignmentInventoryItemMessage(
                                assignment,
                                AssignmentSupervisorState.Running,
                                RepositoryKnown: true,
                                PendingEventCount: 1),
                        ]));
            var reconciliationResult = Assert.Single(reconciliation.Assignments);
            Assert.Equal(AssignmentReconciliationDisposition.Resume, reconciliationResult.Disposition);
            var resumed = Assert.IsType<ExecutionAssignmentMessage>(reconciliationResult.Assignment);
            Assert.Equal(assignment.RequestId, resumed.RequestId);
            Assert.Equal(assignment.ProjectId, resumed.ProjectId);
            Assert.Equal(assignment.WorkspaceBindingId, resumed.WorkspaceBindingId);
            Assert.Equal(assignment.AssignedAt, resumed.AssignedAt);
            Assert.Equal(assignment.ClaimToken, resumed.ClaimToken);

            // Reconciliation precedes replay, matching the node reconnect sequence.
            await using (var spool = CreateSpool(spoolPath))
            {
                var pending = await spool.PeekPendingAsync(100, CancellationToken.None);
                Assert.Equal(message, Assert.Single(pending));

                var acknowledgement =
                    await connection.InvokeAsync<NodeEventAcknowledgementMessage>(
                        "PublishEvents",
                        new NodeEventBatchMessage(pending));
                Assert.Equal([message.EventId], acknowledgement.EventIds);
                await spool.DeleteAsync([message.EventId], CancellationToken.None);
            }

            var secondClaim = await connection.InvokeAsync<ExecutionAssignmentMessage?>(
                "ClaimNext",
                new ClaimRequestMessage(nodeId, LeaseSeconds: 300));
            Assert.Null(secondClaim);

            Assert.Empty(await PendingIdsAsync(CreateSpool(spoolPath)));
            Assert.Equal(1, CountServerEvents(sqlitePath, message.EventId));
            using var scope = second.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            Assert.Equal(1, await db.ExecutionAssignments.CountAsync(
                candidate => candidate.ProjectId == new ProjectId(assignment.ProjectId)));
            Assert.Equal(1, await db.AgentSessions.CountAsync(session => session.Id == message.SessionId));
            var blocked = await db.WorkRequests.AsNoTracking().SingleAsync(
                candidate => candidate.Id == new WorkRequestId(blockedRequestId));
            Assert.Equal(WorkRequestStatus.Queued, blocked.Status);
            var eligibility = scope.ServiceProvider.GetRequiredService<IRequestEligibilityEvaluator>();
            var decision = await eligibility.EvaluateAsync(
                new WorkRequestId(blockedRequestId), new NodeId(nodeId));
            Assert.Equal(SchedulingReasonCodes.ProjectConcurrencyUnavailable, decision.Status.Code);
        }
    }

    private static SqliteNodeEventSpool CreateSpool(string path) =>
        new(Options.Create(new NodeOptions { EventSpoolPath = path }));

    private static async Task<IReadOnlyList<string>> PendingIdsAsync(INodeEventSpool spool)
    {
        var pending = await spool.PeekPendingAsync(100, CancellationToken.None);
        return pending.Select(e => e.EventId).ToList();
    }

    private static int CountServerEvents(string sqlitePath, string eventId)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={sqlitePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SessionEvents WHERE EventId = $id";
        command.Parameters.AddWithValue("$id", eventId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static async Task<(
        Guid ProjectId,
        Guid RequestId,
        Guid BlockedRequestId,
        string SessionId)> SeedQueuedRequestsAsync(
        WebApplicationFactory<Program> factory,
        Guid nodeId,
        string repositoryPath)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var now = DateTimeOffset.UtcNow;
        var project = Project.Register(
            "Replay project",
            "main", enabled: true, maxActiveWriteRequests: 1, maxReadOnlyRequests: 2,
            maxChildAgentsPerRequest: 1, requireCleanStart: false, createRequestBranch: false,
            createRequestCommit: false, autoMerge: false, now);
        var assignedNodeId = new NodeId(nodeId);
        Directory.CreateDirectory(repositoryPath);
        var binding = WorkspaceBinding.Designate(project.Id, assignedNodeId, repositoryPath, now);
        Assert.True(binding.ApplyValidationResult(
            assignedNodeId,
            binding.ValidationRevision,
            WorkspaceBindingStatus.Valid,
            WorkspaceBinding.ValidValidationCode,
            "Seeded for the replay scenario.",
            repositoryPath,
            now));
        var request = WorkRequest.Enqueue(
            project.Id,
            WorkRequestKind.Development,
            RequestPriority.High,
            RiskLevel.Standard,
            "Replay request",
            "Prove the replay",
            now);
        var blockedRequest = WorkRequest.Enqueue(
            project.Id,
            WorkRequestKind.Development,
            RequestPriority.Normal,
            RiskLevel.Standard,
            "Second replay request",
            "Wait for the active assignment.",
            now);
        db.Projects.Add(project);
        db.WorkspaceBindings.Add(binding);
        db.WorkRequests.AddRange(request, blockedRequest);
        await db.SaveChangesAsync();
        return (
            project.Id.Value,
            request.Id.Value,
            blockedRequest.Id.Value,
            "session-replay-" + request.Id.Value.ToString("N"));
    }

    private static async Task SeedRootSessionAsync(
        WebApplicationFactory<Program> factory,
        ExecutionAssignmentMessage assignment,
        string sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = sessionId,
            ProjectId = assignment.ProjectId,
            RequestId = assignment.RequestId,
            AgentName = "root",
            Role = "root",
            Runtime = "pi",
            Model = "codex/default",
            Liveness = nameof(AgentLiveness.Online),
            Activity = nameof(AgentActivity.Idle),
            Attention = nameof(AgentAttention.None),
            WorkState = nameof(AgentWorkState.Executing),
            StatusReason = "Working before restart",
            StartedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
            Version = 1,
        });
        await db.SaveChangesAsync();
    }

    private static Task<NodeDto> PublishExecutionStatusAsync(
        HubConnection connection,
        Guid nodeId,
        IReadOnlyList<Guid> activeAssignmentIds)
    {
        var observedAt = DateTimeOffset.UtcNow;
        const string routingRevision = "node-replay-e2e";
        return connection.InvokeAsync<NodeDto>(
            "Heartbeat",
            new NodeHeartbeatMessage(
                nodeId,
                [],
                ExecutionStatus: new NodeExecutionStatusMessage(
                    observedAt,
                    AvailableRequestSlots: 1,
                    ActiveAssignmentIds: activeAssignmentIds,
                    RoutingRevision: routingRevision,
                    Routes:
                    [
                        new RuntimeRouteReadinessMessage(
                            "root",
                            "codex/default",
                            RuntimeReadinessStatuses.Ready,
                            "node-replay-e2e",
                            observedAt,
                            routingRevision),
                    ])));
    }

    private WebApplicationFactory<Program> CreateControlPlane(
        string sqlitePath,
        AuthTestMaterialResult auth)
    {
        if (!File.Exists(sqlitePath))
        {
            File.Create(sqlitePath).Dispose();
        }

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ControlPlane", $"Data Source={sqlitePath}");
            builder.UseSetting("Projects:NodeId", auth.AuthenticatedNodeId.ToString());
            builder.UseTestAuthFiles(auth.PasswordFile, auth.CredentialDirectory);
        });
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>().Database.Migrate();
        return factory;
    }

    private static async Task<HubConnection> ConnectAsync(
        WebApplicationFactory<Program> factory,
        string nodeToken)
    {
        _ = factory.CreateClient();
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "nodeHub"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(nodeToken);
            })
            .Build();
        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        return connection;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
