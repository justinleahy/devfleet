using System.Net.Http.Json;
using System.Diagnostics;
using PiCommandCenter.ControlPlane.Security;
using Microsoft.AspNetCore.Hosting;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Nodes;
using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Node;
using PiCommandCenter.Node.Child;
using PiCommandCenter.Node.Repository;

namespace PiCommandCenter.EndToEndTests;

/// <summary>
/// SPEC §38.4 Scenario E (unattributed external change) and Scenario F (Control Plane
/// restart with spool replay). Fake runtimes only — no provider network.
/// Scenarios A–D live in <see cref="CoordinationEndToEndTests"/>.
/// </summary>
public sealed class RecoveryDemoEndToEndTests : IClassFixture<EndToEndFixture>, IDisposable
{
    private readonly EndToEndFixture _fixture;
    private HubConnection? _connection;

    public RecoveryDemoEndToEndTests(EndToEndFixture fixture) => _fixture = fixture;

    public void Dispose()
    {
        if (_connection is not null)
        {
            _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task Scenario_E_unattributed_external_change_blocks_the_request()
    {
        var workspace = CopyFixtureInto(_fixture.CreateGitRepository());
        CommitAll(workspace, "fixture baseline");
        var inspector = new RepositoryInspector();
        var baseline = await inspector.CaptureBaselineAsync(workspace, requireCleanStart: true, allowUntrackedFiles: false, CancellationToken.None);
        Assert.True(baseline.IsClean);

        File.WriteAllText(Path.Combine(workspace, "README.md"), "# tampered by a human\n");

        var lease = new ReservationLeaseInfo(
            Guid.NewGuid(),
            1,
            "Active",
            DateTimeOffset.UtcNow.AddMinutes(5),
            [new ReservationScopeSpec("file", "src/App/HealthEndpoint.cs")],
            "api-implementer");

        var ex = await Assert.ThrowsAsync<ExternalRepositoryModificationException>(
            () => inspector.DetectExternalChangesAsync(workspace, baseline.BaseCommit, [lease], CancellationToken.None));
        Assert.Contains("README.md", ex.Paths);

        var nodeId = _fixture.AuthenticatedNodeId;
        var client = _fixture.CreateClient();
        var hub = await HubAsync();
        _ = await hub.InvokeAsync<NodeDto>("Register", new NodeRegistrationMessage(nodeId, "e2e-external", "1.0.0", "{}"));

        const string sessionId = "session-root-ext";
        Guid projectId;
        Guid requestId;
        string claimToken;
        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var now = DateTimeOffset.UtcNow;
            var project = Project.Register(
                "External-change fixture",
                "main",
                enabled: true, maxActiveWriteRequests: 2, maxReadOnlyRequests: 4,
                maxChildAgentsPerRequest: 1, requireCleanStart: false, createRequestBranch: false,
                createRequestCommit: false, autoMerge: false, now);
            var request = WorkRequest.Enqueue(
                project.Id, WorkRequestKind.Development, RequestPriority.Normal, RiskLevel.Standard,
                "Add a health endpoint and tests.",
                File.ReadAllText(CanonicalRequestPath()),
                now);
            request.Start(now);
            request.BeginPlanning(now);
            request.BeginExecuting(now);
            var assignedNodeId = new NodeId(nodeId);
            var binding = WorkspaceBinding.Designate(project.Id, assignedNodeId, workspace, now);
            Assert.True(binding.ApplyValidationResult(
                assignedNodeId,
                binding.ValidationRevision,
                WorkspaceBindingStatus.Valid,
                WorkspaceBinding.ValidValidationCode,
                "Seeded for the recovery scenario.",
                workspace,
                now));
            claimToken = $"recovery-external-{request.Id.Value:N}";
            var assignment = ExecutionAssignment.Create(
                request.Id,
                project.Id,
                binding.Id,
                assignedNodeId,
                binding.CanonicalRepositoryPath!,
                project.DefaultBranch,
                binding.ValidationRevision,
                claimToken,
                now,
                TimeSpan.FromMinutes(5));
            db.Projects.Add(project);
            db.WorkspaceBindings.Add(binding);
            db.WorkRequests.Add(request);
            db.ExecutionAssignments.Add(assignment);
            db.AgentSessions.Add(new AgentSessionRow
            {
                Id = sessionId,
                ProjectId = project.Id.Value,
                RequestId = request.Id.Value,
                AgentName = "root",
                Role = "root",
                Runtime = "pi",
                Model = "codex/gpt-5.6-sol",
                Liveness = nameof(AgentLiveness.Starting),
                Activity = nameof(AgentActivity.Idle),
                Attention = nameof(AgentAttention.None),
                WorkState = nameof(AgentWorkState.Queued),
                StatusReason = "Awaiting registration",
                StartedAtUtcTicks = now.UtcTicks,
                Version = 1,
            });
            await db.SaveChangesAsync();
            projectId = project.Id.Value;
            requestId = request.Id.Value;
        }

        await hub.InvokeAsync<NodeEventAcknowledgementMessage>("PublishEvents", new NodeEventBatchMessage(
        [
            new NodeEventMessage(
                EventId: $"evt-ext-reg-{requestId:N}",
                NodeId: nodeId,
                ProjectId: projectId,
                RequestId: requestId,
                ClaimToken: claimToken,
                SessionId: sessionId,
                Sequence: 1,
                Type: "session.registered",
                OccurredAt: DateTimeOffset.UtcNow,
                PayloadJson: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["agentName"] = "root",
                    ["role"] = "root",
                    ["model"] = "codex/gpt-5.6-sol",
                })),
            new NodeEventMessage(
                EventId: $"evt-ext-{requestId:N}",
                NodeId: nodeId,
                ProjectId: projectId,
                RequestId: requestId,
                ClaimToken: claimToken,
                SessionId: sessionId,
                Sequence: 2,
                Type: "repository.external_change_detected",
                OccurredAt: DateTimeOffset.UtcNow,
                PayloadJson: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["paths"] = ex.Paths,
                    ["detail"] = "Human modified an unreserved file.",
                })),
            new NodeEventMessage(
                EventId: $"evt-ext-snap-{requestId:N}",
                NodeId: nodeId,
                ProjectId: projectId,
                RequestId: requestId,
                ClaimToken: claimToken,
                SessionId: sessionId,
                Sequence: 3,
                Type: "session.snapshot",
                OccurredAt: DateTimeOffset.UtcNow,
                PayloadJson: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["workState"] = "Blocked",
                    ["attention"] = "InputRequired",
                    ["statusReason"] = "BLOCKED — Unattributed external repository modification",
                })),
        ]));

        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var request = await db.WorkRequests.SingleAsync(r => r.Id == new WorkRequestId(requestId));
            request.Block(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        var page = await (await client.GetAsync($"/requests/{requestId}")).Content.ReadAsStringAsync();
        Assert.Contains("External repository change", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("README.md", page);

        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var request = await db.WorkRequests.AsNoTracking().SingleAsync(r => r.Id == new WorkRequestId(requestId));
            Assert.Equal(WorkRequestStatus.Blocked, request.Status);
            var evt = await db.SessionEvents.AsNoTracking()
                .SingleAsync(e => e.Type == "repository.external_change_detected" && e.RequestId == requestId);
            Assert.Contains("README.md", evt.PayloadJson);
        }
    }

    [Fact]
    public async Task Scenario_F_control_plane_restart_replays_spooled_events_without_duplicates()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "pi-cc-e2e-f", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var sqlitePath = Path.Combine(tempRoot, "controlplane.db");
            var spoolPath = Path.Combine(tempRoot, "spool.db");
            var nodeId = _fixture.AuthenticatedNodeId;
            File.Create(sqlitePath).Dispose();

            NodeEventMessage heartbeat;
            using (var first = CreatePlane(sqlitePath))
            {
                await using var connection = await ConnectAsync(first);
                await connection.InvokeAsync<NodeDto>(
                    "Register", new NodeRegistrationMessage(nodeId, "pi-restart", "1.0.0", "{}"));

                const string sessionId = "session-root-f";
                Guid projectId;
                Guid requestId;
                string claimToken;
                using (var scope = first.Services.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
                    var now = DateTimeOffset.UtcNow;
                    var project = Project.Register(
                        "Restart project",
                        "main",
                        enabled: true, maxActiveWriteRequests: 1, maxReadOnlyRequests: 2,
                        maxChildAgentsPerRequest: 1, requireCleanStart: false, createRequestBranch: false,
                        createRequestCommit: false, autoMerge: false, now);
                    var request = WorkRequest.Enqueue(
                        project.Id, WorkRequestKind.Development, RequestPriority.Normal, RiskLevel.Standard,
                        "Stay alive", "Agents remain active across restart.", now);
                    request.Start(now);
                    var assignedNodeId = new NodeId(nodeId);
                    var binding = WorkspaceBinding.Designate(project.Id, assignedNodeId, tempRoot, now);
                    Assert.True(binding.ApplyValidationResult(
                        assignedNodeId,
                        binding.ValidationRevision,
                        WorkspaceBindingStatus.Valid,
                        WorkspaceBinding.ValidValidationCode,
                        "Seeded for the restart scenario.",
                        tempRoot,
                        now));
                    claimToken = $"recovery-restart-{request.Id.Value:N}";
                    var assignment = ExecutionAssignment.Create(
                        request.Id,
                        project.Id,
                        binding.Id,
                        assignedNodeId,
                        binding.CanonicalRepositoryPath!,
                        project.DefaultBranch,
                        binding.ValidationRevision,
                        claimToken,
                        now,
                        TimeSpan.FromMinutes(5));
                    db.Projects.Add(project);
                    db.WorkspaceBindings.Add(binding);
                    db.WorkRequests.Add(request);
                    db.ExecutionAssignments.Add(assignment);
                    db.AgentSessions.Add(new AgentSessionRow
                    {
                        Id = sessionId,
                        ProjectId = project.Id.Value,
                        RequestId = request.Id.Value,
                        AgentName = "root",
                        Role = "root",
                        Runtime = "pi",
                        Model = "codex/gpt-5.6-sol",
                        Liveness = nameof(AgentLiveness.Online),
                        Activity = nameof(AgentActivity.Idle),
                        Attention = nameof(AgentAttention.None),
                        WorkState = nameof(AgentWorkState.Executing),
                        StatusReason = "Working before restart",
                        StartedAtUtcTicks = now.UtcTicks,
                        Version = 1,
                    });
                    await db.SaveChangesAsync();
                    projectId = project.Id.Value;
                    requestId = request.Id.Value;
                }

                heartbeat = new NodeEventMessage(
                    EventId: "evt-f-hb-1",
                    NodeId: nodeId,
                    ProjectId: projectId,
                    RequestId: requestId,
                    ClaimToken: claimToken,
                    SessionId: sessionId,
                    Sequence: 1,
                    Type: "session.heartbeat",
                    OccurredAt: DateTimeOffset.UtcNow,
                    PayloadJson: "{\"statusReason\":\"still working\"}");

                await using (var spool = new SqliteNodeEventSpool(Options.Create(new NodeOptions { EventSpoolPath = spoolPath })))
                {
                    await spool.AppendAsync(heartbeat, CancellationToken.None);
                    var pending = await spool.PeekPendingAsync(100, CancellationToken.None);
                    var ack = await connection.InvokeAsync<NodeEventAcknowledgementMessage>(
                        "PublishEvents", new NodeEventBatchMessage(pending));
                    Assert.Equal([heartbeat.EventId], ack.EventIds);
                }
            }

            using (var second = CreatePlane(sqlitePath))
            {
                await using var connection = await ConnectAsync(second);
                var registered = await connection.InvokeAsync<NodeDto>(
                    "Register", new NodeRegistrationMessage(nodeId, "pi-restart", "1.0.0", "{}"));
                Assert.Equal(NodeStatus.Online, registered.Status);

                await using (var spool = new SqliteNodeEventSpool(Options.Create(new NodeOptions { EventSpoolPath = spoolPath })))
                {
                    var pending = await spool.PeekPendingAsync(100, CancellationToken.None);
                    var ack = await connection.InvokeAsync<NodeEventAcknowledgementMessage>(
                        "PublishEvents", new NodeEventBatchMessage(pending));
                    Assert.Equal([heartbeat.EventId], ack.EventIds);
                    await spool.DeleteAsync([heartbeat.EventId], CancellationToken.None);
                }

                using var scope = second.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
                var count = await db.SessionEvents.CountAsync(e => e.EventId == heartbeat.EventId);
                Assert.Equal(1, count);
                var request = await db.WorkRequests.AsNoTracking().SingleAsync();
                Assert.Equal("Stay alive", request.Title);
            }
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Fixture_repository_splits_api_tests_and_readme_scopes()
    {
        var root = FixtureRoot();
        Assert.True(File.Exists(Path.Combine(root, "src/App/HealthEndpoint.cs")));
        Assert.True(File.Exists(Path.Combine(root, "tests/App.Tests/HealthEndpointTests.cs")));
        Assert.True(File.Exists(Path.Combine(root, "README.md")));
        var canonical = File.ReadAllText(CanonicalRequestPath());
        Assert.Contains("/health/details", canonical);
    }

    [Fact]
    public async Task Web_project_page_is_the_canonical_submission_surface()
    {
        var client = _fixture.CreateClient();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var workspace = CopyFixtureInto(_fixture.CreateGitRepository());
        CommitAll(workspace, "fixture for web demo");
        var register = await client.PostAsJsonAsync("/api/projects", new
        {
            displayName = "Health details fixture",
            repositoryPath = workspace,
            defaultBranch = "main",
            enabled = true,
            maxActiveWriteRequests = 2,
            maxReadOnlyRequests = 4,
            maxChildAgentsPerRequest = 3,
            requireCleanStart = false,
            createRequestBranch = false,
            createRequestCommit = false,
            autoMerge = false,
        }, json);
        var body = await register.Content.ReadAsStringAsync();
        Assert.True(register.IsSuccessStatusCode, body);
        var projectId = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();

        var page = await (await client.GetAsync($"/projects/{projectId}")).Content.ReadAsStringAsync();
        Assert.Contains("Health details fixture", page);
        Assert.Contains("New request", page);
        Assert.Contains("Queue request", page);

        var script = File.ReadAllText(Path.Combine(FixtureRoot(), "..", "..", "scripts", "demo.sh"));
        Assert.Contains("This is not a completed demonstration.", script);
        Assert.Contains("--smoke ignores RUN_REAL_*", script);
        Assert.DoesNotContain("Demo ready (no provider processes launched).", script);
    }

    private async Task<HubConnection> HubAsync()
    {
        if (_connection is not null)
        {
            return _connection;
        }

        _ = _fixture.CreateClient();
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_fixture.Factory.Server.BaseAddress, "nodeHub"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _fixture.Factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(_fixture.NodeTokenHex);
            })
            .Build();
        await _connection.StartAsync();
        return _connection;
    }

    private WebApplicationFactory<Program> CreatePlane(string sqlitePath)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ControlPlane", $"Data Source={sqlitePath}");
            builder.UseTestAuthFiles(_fixture.PasswordFile, _fixture.CredentialDirectory);
        });
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>().Database.Migrate();
        return factory;
    }

    private async Task<HubConnection> ConnectAsync(WebApplicationFactory<Program> factory)
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
        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        return connection;
    }

    private static string CopyFixtureInto(string gitRepo)
    {
        var source = FixtureRoot();
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = Path.GetRelativePath(source, file);
            var dest = Path.Combine(gitRepo, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }

        return gitRepo;
    }

    private static void CommitAll(string repo, string message)
    {
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-q", "-m", message);
    }

    private static void RunGit(string repo, params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repo,
        };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        process.WaitForExit(30_000);
        if (process.ExitCode != 0)
        {
            Assert.Fail($"git {string.Join(' ', args)} failed: {process.StandardError.ReadToEnd()}");
        }
    }

    private static string FixtureRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "demo", "health-details-fixture");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("demo/health-details-fixture");
    }

    private static string CanonicalRequestPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "TestData", "canonical-request.txt");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("tests/TestData/canonical-request.txt");
    }
}
