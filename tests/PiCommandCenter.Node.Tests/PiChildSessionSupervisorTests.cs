using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node;
using PiCommandCenter.Node.Child;
using PiCommandCenter.Node.Runtime;
using PiCommandCenter.Node.Runtime.Antigravity;
using PiCommandCenter.Node.Runtime.Claude;

using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Verification;
using PiCommandCenter.Node.Repository;
using PiCommandCenter.Node.Verification;
namespace PiCommandCenter.Node.Tests;

/// <summary>
/// Child supervisor tests (SPEC §13.3, §18.1): spawn hierarchy and results, role/profile/max
/// validation, unique names, reservation-authorized filesystem tools through the full tool
/// surface, and terminal events for cancellation and worker crash. Children run through the
/// real fake worker process (<c>TestData/fake-pi-worker.mjs</c>); the reservation and mail
/// gateways are fakes — no control plane, no provider network.
/// </summary>
public class PiChildSessionSupervisorTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "pi-cc-child-tests", Guid.NewGuid().ToString("N"))).FullName;

    private readonly string _repoRoot;
    private readonly SqliteNodeEventSpool _spool;
    private readonly PiChildSessionSupervisor _supervisor;
    private readonly FakeReservationGateway _reservations = new();
    private readonly FakeIdentityRegistry _identities = new();
    private readonly FakeVerificationRunner _verification = new();
    private readonly FakeRepositoryInspector _repository = new();
    private readonly FakeCrashRecovery _crash = new();
    private readonly FakeCompletionGateway _completion = new();
    private readonly RequestWorkspaceTracker _workspace = new();
    private readonly List<(string Type, IReadOnlyDictionary<string, object?> Payload)> _parentEvents = [];

    public PiChildSessionSupervisorTests()
    {
        _repoRoot = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        _spool = new SqliteNodeEventSpool(Options.Create(new NodeOptions
        {
            Id = Guid.NewGuid(),
            EventSpoolPath = Path.Combine(_root, "spool.db"),
        }));
        var worker = new PiWorkerOptions
        {
            NodeExecutable = "node",
            WorkerPath = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-pi-worker.mjs"),
            AgentDataDirectory = Path.Combine(_root, "agent-data"),
            RequestTimeoutSeconds = 1,
            MaxChildAgentsPerRequest = 2,
        };
        _supervisor = CreateSupervisor(worker);
    }

    public void Dispose()
    {
        _supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private PiOrchestrationContext RootContext(string requestId)
        => new(
            "root-session-1",
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            requestId,
            ParentSessionId: null,
            EmitAsync: (type, payload, _) =>
            {
                _parentEvents.Add((type, payload));
                return Task.CompletedTask;
            },
            RepositoryRoot: _repoRoot);

    private static PiToolResponse Invoke(
        PiChildSessionSupervisor supervisor,
        PiOrchestrationContext context,
        string type,
        object? payload)
        => supervisor.HandleAsync(
            context, type,
            payload is null ? null : JsonSerializer.SerializeToElement(payload),
            CancellationToken.None).GetAwaiter().GetResult();

    private static Dictionary<string, object?> Result(object? result)
        => Assert.IsType<Dictionary<string, object?>>(result);

    private static string ExpectChildSessionId(PiToolResponse response)
    {
        var result = Result(response.Result);
        if (!result.ContainsKey("childSessionId"))
        {
            throw new Xunit.Sdk.XunitException(
                "spawn did not return childSessionId: "
                + JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }

        return (string)result["childSessionId"]!;
    }

    private async Task<NodeEventMessage[]> SpoolAwaitingAsync(Func<NodeEventMessage[], bool> ready)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var pending = (await _spool.PeekPendingAsync(200, CancellationToken.None)).ToArray();
            if (ready(pending))
            {
                return pending;
            }

            await Task.Delay(50);
        }

        return [.. await _spool.PeekPendingAsync(200, CancellationToken.None)];
    }

    [Fact]
    public async Task Spawn_from_a_root_session_creates_hierarchy_and_returns_the_child_result()
    {
        var context = RootContext(Guid.NewGuid().ToString("D"));
        var response = await _supervisor.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "implementer-1",
                role = "implementer",
                runtimeProfile = "local-pi",
                prompt = "Do the work",
                requestedWriteScopes = new[] { new { kind = "directory", path = "src/Feature" } },
            }),
            CancellationToken.None);

        Assert.True(response.Ok, response.ErrorMessage ?? "spawn failed");
        var result = Result(response.Result);
        var childSessionId = ExpectChildSessionId(response);
        Assert.StartsWith("pi-child-", childSessionId);
        Assert.Equal("root-session-1", result["parentSessionId"]);
        Assert.Equal("implementer-1", result["agentName"]);

        // Reservation for the requested write scope was acquired for the child session.
        Assert.Equal(childSessionId, _reservations.LastAcquire?.OwnerSessionId);

        // child.started lands on the parent's durable event stream with the hierarchy link.
        var started = _parentEvents.Single(x => x.Type == "child.started");
        Assert.Equal(childSessionId, started.Payload["childSessionId"]);
        Assert.Equal("root-session-1", started.Payload["parentSessionId"]);
        Assert.Equal("implementer-1", started.Payload["agentName"]);

        var requested = _parentEvents.Single(x => x.Type == "child.requested");
        Assert.Equal(childSessionId, requested.Payload["childSessionId"]);
        Assert.Equal("root-session-1", requested.Payload["parentSessionId"]);
        Assert.Contains(_parentEvents, x => x.Type == "reservation.granted");

        // The child worker's own events are appended to the spool under the child session id.
        var events = await SpoolAwaitingAsync(e =>
            e.Any(x => x.SessionId == childSessionId && x.Type == "turn.started"));
        Assert.Single(events, x => x.SessionId == childSessionId && x.Type == "turn.started");

        // Hierarchy is visible through agent.status.
        var status = Invoke(_supervisor, context, "agent.status", null);
        var statusList = Assert.IsType<Dictionary<string, object?>[]>(status.Result);
        var view = statusList.Single();
        Assert.Equal("implementer-1", view["agentName"]);
        Assert.Equal(ChildAgentStatus.Running, view["status"]);
        Assert.Equal("root-session-1", view["parentSessionId"]);
    }

    [Fact]
    public async Task A_child_session_may_not_spawn_grandchildren()
    {
        var context = RootContext(Guid.NewGuid().ToString("D"));
        var childContext = context with { SessionId = "pi-child-x", ParentSessionId = context.SessionId };

        var response = Invoke(_supervisor, childContext, "agent.spawn",
            new { agentName = "nested", role = "implementer", runtimeProfile = "local-pi", prompt = "p" });

        Assert.False(response.Ok);
        Assert.Equal("spawn_not_from_root", response.ErrorCode);
    }

    [Fact]
    public async Task Spawn_rejects_roles_and_profiles_outside_the_allowlist()
    {
        var context = RootContext(Guid.NewGuid().ToString("D"));

        var badRole = Invoke(_supervisor, context, "agent.spawn",
            new { agentName = "a", role = "shadow-admin", runtimeProfile = "local-pi", prompt = "p" });
        Assert.Equal("role_not_allowed", Result(badRole.Result)["error"] is Dictionary<string, object?> e1
            ? e1["code"]
            : null);

        var badProfile = Invoke(_supervisor, context, "agent.spawn",
            new { agentName = "b", role = "implementer", runtimeProfile = "unrestricted", prompt = "p" });
        Assert.Equal("runtime_profile_not_allowed", Result(badProfile.Result)["error"] is Dictionary<string, object?> e2
            ? e2["code"]
            : null);

        Assert.Empty(_reservations.Acquires);
        Assert.Empty(_parentEvents);
    }

    [Fact]
    public async Task Spawn_enforces_the_maximum_running_children_and_unique_names()
    {
        var context = RootContext(Guid.NewGuid().ToString("D"));

        var first = await _supervisor.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "child-a",
                role = "reviewer",
                runtimeProfile = "local-pi",
                prompt = "p",
            }),
            CancellationToken.None);
        Assert.True(first.Ok);

        var duplicate = await _supervisor.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "child-a",
                role = "reviewer",
                runtimeProfile = "local-pi",
                prompt = "p",
            }),
            CancellationToken.None);
        var dupResult = Result(duplicate.Result);
        Assert.Equal("duplicate_agent_name",
            ((Dictionary<string, object?>)dupResult["error"]!)["code"]);

        // MaxChildAgentsPerRequest is 2 in this fixture; child-a plus a second child saturate it.
        var second = await _supervisor.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "child-b",
                role = "reviewer",
                runtimeProfile = "local-pi",
                prompt = "p",
            }),
            CancellationToken.None);
        Assert.True(second.Ok);

        var third = await _supervisor.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "child-c",
                role = "reviewer",
                runtimeProfile = "local-pi",
                prompt = "p",
            }),
            CancellationToken.None);
        Assert.False(third.Ok);
        Assert.Equal("max_child_agents_exceeded", third.ErrorCode);
    }

    [Fact]
    public async Task Cancelling_a_child_emits_the_terminal_cancelled_event_and_unblocks_await()
    {
        var context = RootContext(Guid.NewGuid().ToString("D"));
        var spawn = await _supervisor.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "worker",
                role = "implementer",
                runtimeProfile = "local-pi",
                prompt = "p",
            }),
            CancellationToken.None);
        Assert.True(spawn.Ok);
        var childSessionId = ExpectChildSessionId(spawn);

        var cancel = await _supervisor.HandleAsync(
            context,
            "agent.cancel",
            JsonSerializer.SerializeToElement(new { agentName = "worker" }),
            CancellationToken.None);
        Assert.True(cancel.Ok, cancel.ErrorMessage ?? "cancel failed");
        var terminal = Result(cancel.Result);
        Assert.Equal(ChildAgentStatus.Cancelled, terminal["status"]);

        // agent.await on the cancelled child returns the terminal state immediately.
        var awaited = await _supervisor.HandleAsync(
            context,
            "agent.await",
            JsonSerializer.SerializeToElement(new { agentName = "worker" }),
            CancellationToken.None);
        Assert.True(awaited.Ok);
        Assert.Equal(ChildAgentStatus.Cancelled, Result(awaited.Result)["status"]);

        var events = await SpoolAwaitingAsync(e => e.Any(x => x.Type == "child.cancelled"));
        var cancelled = events.Single(x => x.Type == "child.cancelled");
        Assert.Equal(childSessionId, cancelled.SessionId);
        Assert.Contains("cancelled_by_request", cancelled.PayloadJson);
    }

    [Fact]
    public async Task A_worker_crash_produces_the_child_failed_terminal_event()
    {
        var crashing = CreateSupervisor(new PiWorkerOptions
        {
            NodeExecutable = "node",
            WorkerPath = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-pi-worker-crash.mjs"),
            AgentDataDirectory = Path.Combine(_root, "agent-data-crash"),
            RequestTimeoutSeconds = 1,
        });
        await using var _ = crashing;

        var context = RootContext(Guid.NewGuid().ToString("D"));
        var spawn = await crashing.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "doomed",
                role = "implementer",
                runtimeProfile = "local-pi",
                prompt = "p",
            }),
            CancellationToken.None);
        Assert.True(spawn.Ok, spawn.ErrorMessage ?? "spawn failed");

        var awaited = await crashing.HandleAsync(
            context,
            "agent.await",
            JsonSerializer.SerializeToElement(new { agentName = "doomed", timeoutSeconds = 20 }),
            CancellationToken.None);
        Assert.True(awaited.Ok, awaited.ErrorMessage ?? "await failed");
        Assert.Equal(ChildAgentStatus.Failed, Result(awaited.Result)["status"]);

        var events = await SpoolAwaitingAsync(e => e.Any(x => x.Type == "child.failed"));
        Assert.Contains("exited", events.Single(x => x.Type == "child.failed").PayloadJson);
    }

    [Fact]
    public async Task Reserved_mutations_go_through_the_tool_surface_and_authority()
    {
        var context = RootContext(Guid.NewGuid().ToString("D"));
        var lease = _reservations.GrantLease();

        var write = await _supervisor.HandleAsync(
            context,
            "reserved_write",
            JsonSerializer.SerializeToElement(new
            {
                leaseId = lease.LeaseId,
                fencingToken = lease.FencingToken,
                path = "src/Feature/New.cs",
                content = "original",
            }),
            CancellationToken.None);
        Assert.True(write.Ok, write.ErrorMessage ?? "write failed");
        Assert.True(File.Exists(Path.Combine(_repoRoot, "src", "Feature", "New.cs")));

        var edit = await _supervisor.HandleAsync(
            context,
            "reserved_edit",
            JsonSerializer.SerializeToElement(new
            {
                leaseId = lease.LeaseId,
                fencingToken = lease.FencingToken,
                path = "src/Feature/New.cs",
                oldText = "original",
                newText = "edited",
            }),
            CancellationToken.None);
        Assert.True(edit.Ok);
        Assert.Contains("edited", File.ReadAllText(Path.Combine(_repoRoot, "src", "Feature", "New.cs")));

        Assert.Contains(("src/Feature/New.cs", "write"), _reservations.Authorizations);
        Assert.Contains(("src/Feature/New.cs", "edit"), _reservations.Authorizations);
    }

    [Fact]
    public void Arbitrary_runtime_profiles_are_rejected()
    {
        var context = RootContext(Guid.NewGuid().ToString("D"));
        var response = Invoke(_supervisor, context, "agent.spawn",
            new { agentName = "x", role = "implementer", runtimeProfile = "agent-picked-bin", prompt = "p" });
        Assert.Equal("runtime_profile_not_allowed", Result(response.Result)["error"] is Dictionary<string, object?> e
            ? e["code"]
            : null);
    }

    [Fact]
    public async Task Claude_reserved_write_without_a_lease_is_refused()
    {
        var context = RootContext(Guid.NewGuid().ToString("D"));
        var spawn = await _supervisor.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "writer",
                role = "implementer",
                runtimeProfile = AgentRuntimeProfiles.ClaudeReservedWrite,
                prompt = "p",
            }),
            CancellationToken.None);
        Assert.Equal("reservation_required", Result(spawn.Result)["error"] is Dictionary<string, object?> e
            ? e["code"]
            : spawn.ErrorCode);
    }

    [Fact]
    public void Completion_ignores_model_accepted_flag_and_returns_missing_requirements()
    {
        var requestId = Guid.NewGuid();
        var context = RootContext(requestId.ToString("D"));
        _workspace.SetBaseline(requestId, new RepositoryBaseline("main", "abc", "", true, []));
        _completion.Accept = false;
        _completion.Missing = ["mandatory_verification"];
        var response = Invoke(_supervisor, context, "request.complete", new
        {
            accepted = true,
            summaryMarkdown = "done",
            verificationSummary = "green",
        });
        Assert.True(response.Ok);
        var result = Result(response.Result);
        Assert.Equal(false, result["accepted"]);
        Assert.DoesNotContain(_parentEvents, e => e.Type == "request.completed");
    }

    [Fact]
    public void Failed_verification_blocks_and_does_not_complete()
    {
        var context = RootContext(Guid.NewGuid().ToString("D"));
        _verification.Succeed = false;
        var response = Invoke(_supervisor, context, "verification.request", new { profileId = "default" });
        Assert.False(response.Ok);
        Assert.Equal("verification_failed", response.ErrorCode);
        Assert.Contains(_parentEvents, e => e.Type == "verification.failed");
    }

    [Fact]
    public void Unknown_profile_id_is_rejected()
    {
        var context = RootContext(Guid.NewGuid().ToString("D"));
        _verification.RejectCode = "unknown_profile";
        var response = Invoke(_supervisor, context, "verification.request", new { profileId = "agent-shell" });
        Assert.Equal("unknown_profile", response.ErrorCode);
    }

    [Fact]
    public void Accepted_completion_emits_request_completed_only_after_gate()
    {
        var requestId = Guid.NewGuid();
        var context = RootContext(requestId.ToString("D"));
        _workspace.SetBaseline(requestId, new RepositoryBaseline("main", "abc", "", true, []));
        _completion.Accept = true;
        var response = Invoke(_supervisor, context, "request.complete", new { summaryMarkdown = "shipped" });
        Assert.True(response.Ok);
        Assert.Equal(true, Result(response.Result)["accepted"]);
        Assert.Contains(_parentEvents, e => e.Type == "request.completed");
    }

    [Fact]
    public async Task Configured_completion_checkpoint_persists_git_evidence()
    {
        var requestId = Guid.NewGuid();
        PiCheckpointRequest? captured = null;
        var context = RootContext(requestId.ToString("D")) with
        {
            CreateCheckpointAsync = (request, _) =>
            {
                captured = request;
                return Task.FromResult(PiCheckpointResult.Committed("abc123", "pi/request"));
            },
        };
        _completion.Accept = true;

        var response = await _supervisor.HandleAsync(
            context,
            "request.complete",
            JsonSerializer.SerializeToElement(new
            {
                summaryMarkdown = "shipped",
                changedFiles = new[] { "src/App.cs" },
            }),
            CancellationToken.None);

        Assert.True(response.Ok);
        Assert.Equal(["src/App.cs"], captured?.Paths);
        var evidence = Assert.Single(_completion.Evidence);
        Assert.Equal("abc123", evidence.CheckpointCommitId);
        Assert.Equal("pi/request", evidence.RequestBranch);
        Assert.Contains(_parentEvents, item => item.Type == "repository.checkpoint_created");
    }

    [Theory]
    [InlineData("project.diff.inspect")]
    [InlineData("verification.request")]
    [InlineData("request.complete")]
    public void Completion_tools_do_not_fall_through_to_the_inner_handler(string requestType)
    {
        var requestId = Guid.NewGuid();
        var context = RootContext(requestId.ToString("D"));
        _workspace.SetBaseline(requestId, new RepositoryBaseline("main", "abc", "", true, []));
        _completion.Accept = true;
        object payload = requestType == "verification.request"
            ? new { profileId = "default" }
            : new { summaryMarkdown = "shipped" };
        var response = Invoke(_supervisor, context, requestType, payload);
        Assert.NotEqual(PiOrchestrationRequestHandler.NotAvailableUntilChildSupervisor, response.ErrorCode);
        Assert.NotEqual("not_handled", response.ErrorCode);
        Assert.True(response.Ok, response.ErrorCode + ": " + response.ErrorMessage);
    }

    [Fact]
    public async Task Worker_crash_marks_leases_recovery_required()
    {
        var crashing = CreateSupervisor(new PiWorkerOptions
        {
            NodeExecutable = "node",
            WorkerPath = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-pi-worker-crash.mjs"),
            AgentDataDirectory = Path.Combine(_root, "agent-data-crash-2"),
            RequestTimeoutSeconds = 1,
        });
        await using var _ = crashing;
        var context = RootContext(Guid.NewGuid().ToString("D"));
        var spawn = await crashing.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "doomed2",
                role = "implementer",
                runtimeProfile = "local-pi",
                prompt = "p",
                requestedWriteScopes = new[] { new { kind = "directory", path = "src" } },
            }),
            CancellationToken.None);
        Assert.True(spawn.Ok, spawn.ErrorMessage ?? "spawn failed");
        await crashing.HandleAsync(
            context,
            "agent.await",
            JsonSerializer.SerializeToElement(new { agentName = "doomed2", timeoutSeconds = 20 }),
            CancellationToken.None);
        Assert.NotEmpty(_crash.Owners);
    }

    private PiChildSessionSupervisor CreateSupervisor(PiWorkerOptions worker, FakeMailGateway? mail = null)
    {
        var inner = new NoopInnerHandler();
        var node = Options.Create(new NodeOptions { Id = Guid.NewGuid() });
        var pi = new PiRuntimeAdapter(
            node,
            Options.Create(worker),
            new NodeWorkerProcessFactory(),
            inner,
            TimeProvider.System,
            NullLogger<PiRuntimeAdapter>.Instance);
        var settings = Path.Combine(_root, $"claude-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(settings, "{}");
        var claude = new ClaudeCodeRuntimeAdapter(
            node,
            Options.Create(new ClaudeCodeOptions { SettingsPath = settings }),
            new OfficialAgentProcessFactory(),
            TimeProvider.System,
            NullLogger<ClaudeCodeRuntimeAdapter>.Instance);
        var antigravity = new AntigravityRuntimeAdapter(
            node,
            Options.Create(new AntigravityOptions()),
            new AntigravityProcessFactory(),
            TimeProvider.System,
            NullLogger<AntigravityRuntimeAdapter>.Instance);
        var registry = new AgentRuntimeRegistry(pi, claude, antigravity);
        return new PiChildSessionSupervisor(
            Options.Create(worker),
            inner,
            _reservations,
            mail ?? new FakeMailGateway(),
            _identities,
            _spool,
            TimeProvider.System,
            NullLogger.Instance,
            new Lazy<IAgentRuntimeRegistry>(registry),
            _verification,
            _repository,
            _crash,
            _completion,
            _workspace);
    }

    [Fact]
    public async Task A_worker_session_completed_event_resolves_await_as_completed()
    {
var completing = CreateSupervisor(new PiWorkerOptions
        {
            NodeExecutable = "node",
            WorkerPath = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-pi-worker-complete.mjs"),
            AgentDataDirectory = Path.Combine(_root, "agent-data-complete"),
            LeaseRenewalSeconds = 3600,
        });
        await using var _ = completing;

        var context = RootContext(Guid.NewGuid().ToString("D"));
        var spawn = await completing.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "finisher",
                role = "implementer",
                runtimeProfile = "local-pi",
                prompt = "p",
            }),
            CancellationToken.None);
        Assert.True(spawn.Ok, spawn.ErrorMessage ?? "spawn failed");

        var awaited = await completing.HandleAsync(
            context,
            "agent.await",
            JsonSerializer.SerializeToElement(new { agentName = "finisher", timeoutSeconds = 20 }),
            CancellationToken.None);
        Assert.True(awaited.Ok, awaited.ErrorMessage ?? "await failed");
        Assert.Equal(ChildAgentStatus.Completed, Result(awaited.Result)["status"]);

        var events = await SpoolAwaitingAsync(e => e.Any(x => x.Type == "child.completed"));
        var completed = events.Single(x => x.Type == "child.completed");
        Assert.Contains("\"runtime\":\"pi\"", completed.PayloadJson);
    }

    [Fact]
    public async Task Provider_auth_blocked_close_never_emits_child_completed()
    {
        var blocked = CreateSupervisor(new PiWorkerOptions
        {
            NodeExecutable = "node",
            WorkerPath = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-pi-worker-blocked.mjs"),
            AgentDataDirectory = Path.Combine(_root, "agent-data-blocked"),
            LeaseRenewalSeconds = 3600,
        });
        await using var _ = blocked;

        var context = RootContext(Guid.NewGuid().ToString("D"));
        var spawn = await blocked.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "needs-login",
                role = "implementer",
                runtimeProfile = "local-pi",
                prompt = "p",
            }),
            CancellationToken.None);
        Assert.True(spawn.Ok, spawn.ErrorMessage ?? "spawn failed");

        var events = await SpoolAwaitingAsync(e => e.Any(x => x.Type == "child.blocked"));
        Assert.Contains(events, x => x.Type == "child.blocked");
        Assert.Contains("\"status\":\"blocked\"", events.Single(x => x.Type == "child.blocked").PayloadJson);
        Assert.DoesNotContain(events, x => x.Type == "child.completed");
    }

    [Fact]
    public async Task Child_events_carry_normalized_identity_metadata()
    {
        var context = RootContext(Guid.NewGuid().ToString("D"));
        var spawn = await _supervisor.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "documented",
                role = "reviewer",
                runtimeProfile = PiCommandCenter.Application.Runtime.AgentRuntimeProfiles.LocalPi,
                prompt = "p",
            }),
            CancellationToken.None);
        Assert.True(spawn.Ok);
        var childSessionId = ExpectChildSessionId(spawn);

        var events = await SpoolAwaitingAsync(e =>
            e.Any(x => x.SessionId == childSessionId && x.Type == "turn.started"));
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            events.Single(x => x.SessionId == childSessionId && x.Type == "turn.started").PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("pi", payload["runtime"].GetString());
        Assert.Equal("root-session-1", payload["parentSessionId"].GetString());
        Assert.Equal("documented", payload["agentName"].GetString());
        Assert.Equal("reviewer", payload["role"].GetString());
        Assert.Equal(PiCommandCenter.Application.Runtime.AgentRuntimeProfiles.LocalPi, payload["runtimeProfile"].GetString());
    }

    [Fact]
    public async Task Spawn_allocates_a_project_scoped_identity_and_terminal_releases_it()
    {
        var context = RootContext(Guid.NewGuid().ToString("D"));
        var spawn = await _supervisor.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "identified",
                role = "implementer",
                runtimeProfile = "local-pi",
                prompt = "p",
            }),
            CancellationToken.None);
        Assert.True(spawn.Ok);
        var childSessionId = ExpectChildSessionId(spawn);

        var allocation = _identities.Allocated.Single(a => a.SessionId == childSessionId);
        Assert.Equal("identified", allocation.AgentName);
        Assert.Equal("implementer", allocation.Role);
        Assert.Equal("pi", allocation.Runtime);

        await _supervisor.HandleAsync(
            context,
            "agent.cancel",
            JsonSerializer.SerializeToElement(new { agentName = "identified" }),
            CancellationToken.None);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!_identities.Released.Contains(childSessionId) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Contains(childSessionId, _identities.Released);
    }

    [Fact]
    public async Task Allocated_collision_safe_name_is_used_by_runtime_and_events()
    {
        _identities.AllocatedNameOverride = "implementer-2";
        var context = RootContext(Guid.NewGuid().ToString("D"));

        var spawn = await _supervisor.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "implementer",
                role = "implementer",
                runtimeProfile = "local-pi",
                prompt = "p",
            }),
            CancellationToken.None);

        Assert.True(spawn.Ok);
        var result = Result(spawn.Result);
        Assert.Equal("implementer-2", result["agentName"]);
        var started = _parentEvents.Single(item => item.Type == "child.started");
        Assert.Equal("implementer-2", started.Payload["agentName"]);
        Assert.Equal("pi", started.Payload["runtime"]);
    }

    [Fact]
    public async Task Active_leases_are_renewed_on_the_configured_interval()
    {
var renewing = CreateSupervisor(new PiWorkerOptions
        {
            NodeExecutable = "node",
            WorkerPath = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-pi-worker.mjs"),
            AgentDataDirectory = Path.Combine(_root, "agent-data-renew"),
            LeaseRenewalSeconds = 1,
        });
        await using var _ = renewing;

        var context = RootContext(Guid.NewGuid().ToString("D"));
        var spawn = await renewing.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "renewed",
                role = "implementer",
                runtimeProfile = "local-pi",
                prompt = "p",
                requestedWriteScopes = new[] { new { kind = "file", path = "src/Renew.txt" } },
            }),
            CancellationToken.None);
        Assert.True(spawn.Ok);
        var childSessionId = ExpectChildSessionId(spawn);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_reservations.Renewals.Count(renewal => renewal.SessionId == childSessionId) < 2
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.True(_reservations.Renewals.Count(renewal => renewal.SessionId == childSessionId) >= 2,
            "Expected at least two lease renewals within the interval window.");
    }

    [Fact]
    public async Task Transient_lease_renewal_failure_is_retried()
    {
        _reservations.RenewFailuresRemaining = 1;
        await using var renewing = CreateSupervisor(new PiWorkerOptions
        {
            NodeExecutable = "node",
            WorkerPath = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-pi-worker.mjs"),
            AgentDataDirectory = Path.Combine(_root, "agent-data-renew-retry"),
            LeaseRenewalSeconds = 1,
        });

        var context = RootContext(Guid.NewGuid().ToString("D"));
        var spawn = await renewing.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "retrying-writer",
                role = "implementer",
                runtimeProfile = "local-pi",
                prompt = "p",
                requestedWriteScopes = new[] { new { kind = "file", path = "src/Retry.txt" } },
            }),
            CancellationToken.None);
        Assert.True(spawn.Ok);
        var childSessionId = ExpectChildSessionId(spawn);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_reservations.Renewals.Count(item => item.SessionId == childSessionId) < 2
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.True(_reservations.Renewals.Count(item => item.SessionId == childSessionId) >= 2);
        Assert.Equal(0, _reservations.RenewFailuresRemaining);
    }

    [Fact]
    public async Task Lease_acquired_after_spawn_is_renewed()
    {
        var context = RootContext(Guid.NewGuid().ToString("D"));
        await using var supervisor = CreateSupervisor(new PiWorkerOptions
        {
            NodeExecutable = "node",
            WorkerPath = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-pi-worker.mjs"),
            AgentDataDirectory = Path.Combine(_root, "agent-data-late-lease"),
            LeaseRenewalSeconds = 1,
        });
        var spawn = await supervisor.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "late-writer",
                role = "implementer",
                runtimeProfile = "local-pi",
                prompt = "p",
            }),
            CancellationToken.None);
        var childSessionId = ExpectChildSessionId(spawn);
        var childContext = context with
        {
            SessionId = childSessionId,
            ParentSessionId = context.SessionId,
        };

        var acquired = await supervisor.HandleAsync(
            childContext,
            "reservation.acquire",
            JsonSerializer.SerializeToElement(new
            {
                scopes = new[] { new { kind = "file", path = "src/Late.cs" } },
                reason = "late scope",
            }),
            CancellationToken.None);
        Assert.True(acquired.Ok);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (_reservations.Renewals.All(item => item.SessionId != childSessionId)
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.Contains(_reservations.Renewals, item => item.SessionId == childSessionId);
    }

    [Fact]
    public async Task Handoff_requires_explicit_accept_by_the_current_owner()
    {
        var context = RootContext(Guid.NewGuid().ToString("D"));
        var target = context with
        {
            SessionId = "reviewer-session",
            ParentSessionId = context.SessionId,
        };
        var owner = context with
        {
            SessionId = "implementer-session",
            ParentSessionId = context.SessionId,
        };
        var lease = _reservations.GrantLease(
            "implementer-session",
            new ReservationScopeSpec("file", "src/Registration.cs"));
        var mail = new FakeMailGateway();
var supervisor = CreateSupervisor(new PiWorkerOptions
        {
            NodeExecutable = "node",
            WorkerPath = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-pi-worker.mjs"),
            AgentDataDirectory = Path.Combine(_root, "agent-data-handoff"),
            LeaseRenewalSeconds = 3600,
        }, mail);
        await using var _ = supervisor;

        // The blocked target requests ownership; the node derives its session identity.
        var request = await supervisor.HandleAsync(
            target,
            "reservation.handoff.request",
            JsonSerializer.SerializeToElement(new
            {
                paths = new[] { "src/Registration.cs" },
                reason = "reviewer needs ownership",
            }),
            CancellationToken.None);
        Assert.True(request.Ok, request.ErrorMessage ?? "handoff request failed");
        Assert.Equal("handoff_requested", Result(request.Result)["state"]);
        Assert.Contains(_parentEvents, x => x.Type == "reservation.handoff_requested");
        var handoffMail = Assert.Single(mail.Sends);
        Assert.Equal(["implementer-session"], handoffMail.Recipients);
        // The desired recipient cannot approve its own takeover.
        var rejected = await supervisor.HandleAsync(
            target,
            "reservation.handoff.accept",
            JsonSerializer.SerializeToElement(new { leaseId = lease.LeaseId }),
            CancellationToken.None);
        Assert.Equal("handoff_not_pending", rejected.ErrorCode);

        // Transfer does not happen before acceptance.
        Assert.DoesNotContain(_reservations.Transfers, t => t.LeaseId == lease.LeaseId);

        // The current owner accepts; ownership moves to the requesting target.
        var accept = await supervisor.HandleAsync(
            owner,
            "reservation.handoff.accept",
            JsonSerializer.SerializeToElement(new { leaseId = lease.LeaseId }),
            CancellationToken.None);
        Assert.True(accept.Ok, accept.ErrorMessage ?? "handoff accept failed");
        var transfer = Assert.Single(_reservations.Transfers);
        Assert.Equal((lease.LeaseId, "implementer-session", "reviewer-session"),
            (transfer.LeaseId, transfer.FromSessionId, transfer.ToSessionId));
    }

    [Fact]
    public async Task Control_plane_cancel_requests_stop_the_child_and_release_lifecycle()
    {
        var context = RootContext(Guid.NewGuid().ToString("D"));
        var spawn = await _supervisor.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "remote-stopped",
                role = "implementer",
                runtimeProfile = "local-pi",
                prompt = "p",
            }),
            CancellationToken.None);
        Assert.True(spawn.Ok);
        var childSessionId = ExpectChildSessionId(spawn);

        var stopped = await _supervisor.CancelSessionAsync(childSessionId, "control_plane_request");
        Assert.True(stopped);

        var events = await SpoolAwaitingAsync(e => e.Any(x => x.Type == "child.cancelled"));
        Assert.Contains("control_plane_request", events.Single(x => x.Type == "child.cancelled").PayloadJson);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!_identities.Released.Contains(childSessionId) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Contains(childSessionId, _identities.Released);
    }

    private sealed class NoopInnerHandler : IPiOrchestrationRequestHandler
    {
        public Task<PiToolResponse> HandleAsync(
            PiOrchestrationContext context,
            string requestType,
            JsonElement? payload,
            CancellationToken cancellationToken)
            => Task.FromResult(PiToolResponse.Failure("not_handled", requestType));
    }

    private sealed class NullLogger : ILogger<PiChildSessionSupervisor>
    {
        public static readonly NullLogger Instance = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class FakeVerificationRunner : IVerificationCommandRunner
    {
        public string? LastProfileId { get; private set; }
        public bool Succeed { get; set; } = true;
        public string RejectCode { get; set; } = "";

        public Task<VerificationProfileRunResult> RunAsync(
            VerificationRunContext context,
            string profileId,
            string? commandId,
            CancellationToken cancellationToken)
        {
            LastProfileId = profileId;
            if (!string.IsNullOrEmpty(RejectCode))
            {
                throw new VerificationRejectedException(RejectCode, "profile not configured");
            }

            var command = new VerificationCommandResult(
                commandId ?? "cmd",
                "true",
                [],
                ".",
                Succeed ? 0 : 1,
                TimeSpan.FromMilliseconds(1),
                Succeed ? "ok" : "fail",
                "",
                false,
                false,
                false,
                false,
                null,
                true);
            return Task.FromResult(new VerificationProfileRunResult(profileId, [command], Succeed));
        }
    }

    private sealed class FakeRepositoryInspector : IRepositoryInspector
    {
        public Task<RepositoryBaseline> CaptureBaselineAsync(
            string repositoryRoot, bool requireCleanStart, bool allowUntrackedFiles, CancellationToken cancellationToken)
            => Task.FromResult(new RepositoryBaseline("main", "abc", "", true, []));

        public Task<RepositoryDiffInspection> InspectDiffAsync(
            string repositoryRoot, string baseCommit, IReadOnlyList<ReservationLeaseInfo> leases, CancellationToken cancellationToken)
            => Task.FromResult(new RepositoryDiffInspection("main", baseCommit, [], []));

        public Task DetectExternalChangesAsync(
            string repositoryRoot, string baseCommit, IReadOnlyList<ReservationLeaseInfo> leases, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class FakeCrashRecovery : IRuntimeCrashRecovery
    {
        public List<string> Owners { get; } = [];

        public Task MarkOwnedLeasesRecoveryRequiredAsync(
            Guid nodeId, Guid projectId, Guid? requestId, string ownerSessionId, string reason, CancellationToken cancellationToken)
        {
            Owners.Add(ownerSessionId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCompletionGateway : INodeCompletionGateway
    {
        public bool Accept { get; set; }
        public IReadOnlyList<string> Missing { get; set; } = ["verification"];
        public List<VerificationRunDto> Runs { get; } = [];
        public List<CompletionEvidence> Evidence { get; } = [];


        public Task RecordVerificationRunAsync(VerificationRunDto run, CancellationToken cancellationToken)
        {
            Runs.Add(run);
            return Task.CompletedTask;
        }

        public Task<CompletionGateDecision> EvaluateCompletionAsync(
            Guid projectId, Guid requestId, string rootSessionId, CompletionEvidence evidence, CancellationToken cancellationToken)
        {
            Evidence.Add(evidence);
            return Task.FromResult(new CompletionGateDecision(
                Accept,
                Accept ? [] : Missing,
                Accept
                    ? new RequestResultDto(
                        requestId,
                        evidence.SummaryMarkdown,
                        evidence.ChangedFiles ?? [],
                        evidence.ReviewFindings,
                        evidence.VerificationSummary,
                        DateTimeOffset.UtcNow,
                        evidence.RequestBranch,
                        evidence.CheckpointCommitId)
                    : null));
        }
    }
}
