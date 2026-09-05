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
        var childSessionId = Assert.IsType<string>(result["childSessionId"]);
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
        var childSessionId = (string)Result(spawn.Result)["childSessionId"]!;

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

    private PiChildSessionSupervisor CreateSupervisor(PiWorkerOptions worker)
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
            new FakeMailGateway(),
            _spool,
            TimeProvider.System,
            NullLogger.Instance,
            new Lazy<IAgentRuntimeRegistry>(registry));
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
}
