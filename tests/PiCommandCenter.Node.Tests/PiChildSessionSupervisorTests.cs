using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain.Verification;
using PiCommandCenter.Node;
using PiCommandCenter.Node.Child;
using PiCommandCenter.Node.Runtime;
using PiCommandCenter.Node.Runtime.Antigravity;
using PiCommandCenter.Node.Runtime.Claude;
using PiCommandCenter.Node.Runtime.Muse;
using PiCommandCenter.Node.RuntimeRouting;

using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Verification;
using PiCommandCenter.Node.Repository;
using PiCommandCenter.Node.Verification;
using PiCommandCenter.Node.Quiescence;

namespace PiCommandCenter.Node.Tests;

/// <summary>
/// Child supervisor tests (SPEC §13.3, §18.1): spawn hierarchy and results, role/route/max
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
    private readonly FakeVerificationCoordinator _verification = new();
    private readonly FakeRepositoryInspector _repository = new();
    private readonly FakeCrashRecovery _crash = new();
    private readonly FakeCompletionGateway _completion = new();
    private readonly NodeAssignmentCredentialSource _assignmentCredentials = new();
    private readonly RequestWorkspaceTracker _workspace = new();
    private readonly RequestAdmissionGate _admission = new(TimeProvider.System);
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
            RoleRoutes = TestRoleRoutes(),
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

    private static Dictionary<string, AgentRoleRouteCandidate[]> TestRoleRoutes()
        => new(StringComparer.Ordinal)
        {
            ["root"] = [new() { Model = "codex/gpt-5.6-sol" }],
            ["architect"] = [new() { Model = "codex/gpt-5.6-sol" }],
            ["implementer"] = [new() { Model = "codex/gpt-5.6-sol" }],
            ["reviewer"] = [new() { Model = "codex/gpt-5.6-sol" }],
            ["verifier"] = [new() { Model = "codex/gpt-5.6-sol" }],
        };

    private PiOrchestrationContext RootContext(string requestId)
    {
        var projectId = Guid.NewGuid();
        _assignmentCredentials.Track(new NodeAssignmentCredential(
            Guid.Parse(requestId),
            projectId,
            "child-session-supervisor-test-token"));

        return new PiOrchestrationContext(
            "root-session-1",
            Guid.NewGuid().ToString("D"),
            projectId.ToString("D"),
            requestId,
            ParentSessionId: null,
            EmitAsync: (type, payload, _) =>
            {
                _parentEvents.Add((type, payload));
                return Task.CompletedTask;
            },
            RepositoryRoot: _repoRoot,
            WorkspaceBindingId: Guid.NewGuid(),
            BindingValidationRevision: 17,
            VerificationPolicyRevision: "test-policy-v1",
            BaselineVersion: IBaselineVerification.Version,
            TrustedVerificationProfileId: null,
            TrustedVerificationProfileRevision: null,
            MandatoryVerificationCommandIds: [IBaselineVerification.RepositoryIntegrityCommandId]);
    }

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
            new { agentName = "nested", role = "implementer", prompt = "p" });

        Assert.False(response.Ok);
        Assert.Equal("spawn_not_from_root", response.ErrorCode);
    }

    [Fact]
    public async Task Unknown_roles_are_rejected_and_agent_runtime_fields_are_ignored()
    {
        var context = RootContext(Guid.NewGuid().ToString("D"));

        var badRole = Invoke(_supervisor, context, "agent.spawn",
            new { agentName = "a", role = "shadow-admin", prompt = "p" });
        Assert.Equal("role_not_allowed", Result(badRole.Result)["error"] is Dictionary<string, object?> error
            ? error["code"]
            : null);

        var routed = Invoke(_supervisor, context, "agent.spawn",
            new
            {
                agentName = "b",
                role = "implementer",
                runtimeProfile = "unrestricted",
                model = "claude-code/opus",
                prompt = "p",
            });
        Assert.Equal("codex/gpt-5.6-sol", Result(routed.Result)["model"]);
        Assert.False(Result(routed.Result).ContainsKey("runtimeProfile"));
        await _supervisor.CancelSessionAsync(ExpectChildSessionId(routed), "test cleanup");
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
    public async Task Ordered_route_falls_back_to_the_next_candidate()
    {
        var options = new PiWorkerOptions
        {
            NodeExecutable = "node",
            WorkerPath = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-pi-worker.mjs"),
            AgentDataDirectory = Path.Combine(_root, "agent-data-fallback"),
            RoleRoutes = new(StringComparer.Ordinal)
            {
                ["implementer"] =
                [
                    new() { Model = "claude-code/primary" },
                    new() { Model = "antigravity/secondary" },
                    new() { Model = "codex/final" },
                ],
            },
        };
        await using var supervisor = CreateSupervisor(options);
        var context = RootContext(Guid.NewGuid().ToString("D"));
        _reservations.AcquireError = new GatewayError("conflict", "scope held elsewhere");

        // Claude needs the lease for write access; Antigravity can never write; Pi runs with
        // read-only tools and surfaces the denial.
        var spawn = await supervisor.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "writer",
                role = "implementer",
                prompt = "p",
                requestedWriteScopes = new[] { new { kind = "directory", path = "src/Feature" } },
            }),
            CancellationToken.None);

        Assert.Equal("codex/final", Result(spawn.Result)["model"]);
        Assert.Equal("conflict", Result(spawn.Result)["reservationError"]);
        Assert.False(Result(spawn.Result).ContainsKey("runtimeProfile"));
        var status = Invoke(supervisor, context, "agent.status", new { agentName = "writer" });
        var statusView = Assert.Single(Assert.IsType<Dictionary<string, object?>[]>(status.Result));
        Assert.Equal("codex/final", statusView["model"]);
        Assert.False(statusView.ContainsKey("runtimeProfile"));
        var attemptedModels = _parentEvents
            .Where(entry => entry.Type == "child.requested")
            .Select(entry => entry.Payload["model"] as string)
            .ToArray();
        Assert.Collection(
            attemptedModels,
            model => Assert.Equal("claude-code/primary", model),
            model => Assert.Equal("codex/final", model));
        Assert.All(_parentEvents, entry => Assert.False(entry.Payload.ContainsKey("runtimeProfile")));
        await supervisor.CancelSessionAsync(ExpectChildSessionId(spawn), "test cleanup");
    }

    [Fact]
    public async Task Saved_route_is_used_by_the_next_child_spawn_without_restart()
    {
        NodeRuntimeRoutingStore? routing = null;
        var options = new PiWorkerOptions
        {
            NodeExecutable = "node",
            WorkerPath = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-pi-worker.mjs"),
            AgentDataDirectory = Path.Combine(_root, "agent-data-live-routing"),
        };
        await using var supervisor = CreateSupervisor(options, routingCreated: store => routing = store);
        Assert.NotNull(routing);
        var routes = routing.Current.AllowedRoles.Select(role => new RuntimeRoleRouteMessage(
            role,
            [new RuntimeRouteCandidateMessage(
                role == "reviewer" ? "codex/reviewer-live" : "codex/gpt-5.6-sol")])).ToArray();
        await routing.UpdateAsync(new UpdateNodeRuntimeConfigurationMessage(routes));
        var context = RootContext(Guid.NewGuid().ToString("D"));

        var spawn = await supervisor.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "live-reviewer",
                role = "reviewer",
                prompt = "p",
            }),
            CancellationToken.None);

        Assert.Equal("codex/reviewer-live", Result(spawn.Result)["model"]);
        await supervisor.CancelSessionAsync(ExpectChildSessionId(spawn), "test cleanup");
    }

    [Fact]
    public async Task Exhausted_ordered_route_reports_every_candidate()
    {
        var options = new PiWorkerOptions
        {
            NodeExecutable = "node",
            WorkerPath = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-pi-worker.mjs"),
            AgentDataDirectory = Path.Combine(_root, "agent-data-exhausted"),
            RoleRoutes = new(StringComparer.Ordinal)
            {
                ["implementer"] =
                [
                    new() { Model = "claude-code/only-choice" },
                    new() { Model = "antigravity/never-writes" },
                ],
            },
        };
        await using var supervisor = CreateSupervisor(options);
        var context = RootContext(Guid.NewGuid().ToString("D"));
        _reservations.AcquireError = new GatewayError("conflict", "scope held elsewhere");

        var spawn = await supervisor.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "writer",
                role = "implementer",
                prompt = "p",
                requestedWriteScopes = new[] { new { kind = "directory", path = "src/Feature" } },
            }),
            CancellationToken.None);

        var error = Assert.IsType<Dictionary<string, object?>>(Result(spawn.Result)["error"]);
        Assert.Equal("runtime_route_exhausted", error["code"]);
        var message = Assert.IsType<string>(error["message"]);
        Assert.Contains("claude-code/only-choice: ", message);
        Assert.Contains("antigravity/never-writes: ", message);
        Assert.DoesNotContain(_parentEvents, entry =>
            entry.Type == "child.requested" && entry.Payload["model"] is "antigravity/never-writes");
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
    public void Verification_records_the_exact_requesting_session()
    {
        var requestId = Guid.NewGuid();
        var context = RootContext(requestId.ToString("D")) with
        {
            SessionId = "requesting-root-session",
        };

        var response = Invoke(
            _supervisor,
            context,
            "verification.request",
            new { profileId = "default" });

        Assert.True(response.Ok);
        Assert.Equal(context.SessionId, _verification.LastContext?.RequestingSessionId);
        var recorded = Assert.Single(_completion.Runs);
        Assert.Equal(context.SessionId, recorded.SessionId);
        Assert.Equal(requestId, recorded.Run.RequestId);
    }

    [Fact]
    public void Legacy_child_verification_request_is_intermediate_and_cannot_complete()
    {
        var requestId = Guid.NewGuid();
        var context = RootContext(requestId.ToString("D"));
        var childContext = context with
        {
            SessionId = "pi-child-verify",
            ParentSessionId = context.SessionId,
        };

        var response = Invoke(
            _supervisor,
            childContext,
            "verification.request",
            new { profileId = "default", commandId = "lint" });

        Assert.True(response.Ok);
        Assert.Equal(0, _verification.FinalVerificationCount);
        Assert.Equal(1, _verification.IntermediateVerificationCount);
        Assert.DoesNotContain(_parentEvents, e => e.Type == "verification.started");
        Assert.DoesNotContain(_parentEvents, e => e.Type == "verification.completed");
        Assert.DoesNotContain(_parentEvents, e => e.Type == "verification.failed");
        Assert.Contains(_parentEvents, e => e.Type == "verification.intermediate");
        var recorded = Assert.Single(_completion.Runs);
        Assert.Equal(childContext.SessionId, recorded.SessionId);
        Assert.Equal(VerificationRunKind.Intermediate, recorded.Run.RunKind);
        Assert.Equal(requestId, recorded.Run.RequestId);
    }

    [Fact]
    public void Root_cannot_invoke_child_only_intermediate_verification()
    {
        var context = RootContext(Guid.NewGuid().ToString("D"));

        var response = Invoke(
            _supervisor,
            context,
            "verification.intermediate.request",
            new { profileId = "default" });

        Assert.False(response.Ok);
        Assert.Equal("intermediate_not_from_child", response.ErrorCode);
        Assert.Equal(0, _verification.FinalVerificationCount);
        Assert.Equal(0, _verification.IntermediateVerificationCount);
        Assert.Empty(_completion.Runs);
        Assert.DoesNotContain(_parentEvents, e => e.Type.StartsWith("verification.", StringComparison.Ordinal));
    }

    [Fact]
    public void Child_intermediate_requires_source_reservations_to_be_released_first()
    {
        var requestId = Guid.NewGuid();
        var context = RootContext(requestId.ToString("D"));
        var childContext = context with
        {
            SessionId = "pi-child-source",
            ParentSessionId = context.SessionId,
        };
        var lease = _reservations.GrantLease(
            childContext.SessionId,
            new ReservationScopeSpec("directory", "src/Feature"));

        var response = Invoke(_supervisor, childContext, "verification.request", new { profileId = "default" });

        Assert.False(response.Ok);
        Assert.Equal("source_reservation_active", response.ErrorCode);
        Assert.Contains("Release", response.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(lease.LeaseId, _reservations.Releases);
        Assert.Empty(_reservations.Acquires);
        Assert.Equal(0, _verification.IntermediateVerificationCount);
    }

    [Fact]
    public void Verification_seeds_persisted_runs_before_coordinator_evaluation()
    {
        var requestId = Guid.NewGuid();
        var context = RootContext(requestId.ToString("D"));
        var persisted = new VerificationRunDto(
            Guid.NewGuid(),
            requestId,
            IBaselineVerification.ProfileId,
            IBaselineVerification.RepositoryIntegrityCommandId,
            VerificationRunStatus.Passed,
            0,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "ok",
            "/secret/artifact.log",
            Mandatory: true,
            Fingerprint: "test-fingerprint",
            PolicyRevision: "test-policy-v1",
            RunKind: VerificationRunKind.Baseline,
            AttemptId: Guid.NewGuid());
        _completion.PersistedRuns.Add(persisted);

        var response = Invoke(_supervisor, context, "verification.request", new { profileId = "ignored" });

        Assert.True(response.Ok);
        Assert.Equal(1, _verification.FinalVerificationCount);
        var listed = Assert.Single(_completion.ListedRuns);
        Assert.Equal(context.SessionId, listed.SessionId);
        Assert.Equal(requestId, listed.RequestId);
        Assert.Contains(
            _verification.LastContext!.ExistingRuns,
            run => run.Id == persisted.Id && run.Fingerprint == persisted.Fingerprint);
    }


    [Fact]
    public void Submit_completion_runs_final_verification_without_a_prior_verification_request()
    {
        var requestId = Guid.NewGuid();
        var context = RootContext(requestId.ToString("D"));
        _workspace.SetBaseline(requestId, new RepositoryBaseline("main", "abc", "", true, []));
        _completion.Accept = false;
        _completion.Missing = ["review"];

        var response = Invoke(
            _supervisor,
            context,
            "submit_completion",
            new { summaryMarkdown = "ready" });

        Assert.True(response.Ok);
        Assert.Equal(false, Result(response.Result)["accepted"]);
        Assert.Equal(1, _verification.FinalVerificationCount);
        Assert.Equal(1, _verification.FingerprintCaptureCount);
        Assert.Single(_parentEvents, item => item.Type == "verification.started");
        Assert.Single(_parentEvents, item => item.Type == "verification.completed");
        var evidence = Assert.Single(_completion.Evidence);
        Assert.Equal(_verification.VerificationFingerprint, evidence.VerificationFingerprint);
    }

    [Fact]
    public void Completion_reinspects_the_fingerprint_and_rejects_stale_verification()
    {
        var requestId = Guid.NewGuid();
        var context = RootContext(requestId.ToString("D"));
        _workspace.SetBaseline(requestId, new RepositoryBaseline("main", "abc", "", true, []));
        _verification.CurrentFingerprint = "fingerprint-after-late-edit";

        var response = Invoke(
            _supervisor,
            context,
            "submit_completion",
            new { summaryMarkdown = "ready" });

        Assert.False(response.Ok);
        Assert.Equal("verification_stale", response.ErrorCode);
        Assert.Equal(1, _verification.FinalVerificationCount);
        Assert.Equal(1, _verification.FingerprintCaptureCount);
        Assert.Empty(_completion.Begun);
        Assert.Empty(_completion.Evidence);
    }

    [Fact]
    public async Task Foreign_child_session_id_cannot_be_observed_or_cancelled()
    {
        var owner = RootContext(Guid.NewGuid().ToString("D"));
        var spawn = await _supervisor.HandleAsync(
            owner,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "owned-child",
                role = "implementer",
                prompt = "p",
            }),
            CancellationToken.None);
        Assert.True(spawn.Ok);
        var childSessionId = ExpectChildSessionId(spawn);

        var stranger = RootContext(Guid.NewGuid().ToString("D"));
        var status = await _supervisor.HandleAsync(
            stranger,
            "agent.status",
            JsonSerializer.SerializeToElement(new { childSessionId }),
            CancellationToken.None);
        Assert.True(status.Ok);
        Assert.Empty(Assert.IsType<Dictionary<string, object?>[]>(status.Result));

        var bySessionId = await _supervisor.HandleAsync(
            stranger,
            "agent.status",
            JsonSerializer.SerializeToElement(new { sessionId = childSessionId }),
            CancellationToken.None);
        Assert.True(bySessionId.Ok);
        Assert.Empty(Assert.IsType<Dictionary<string, object?>[]>(bySessionId.Result));

        var cancel = await _supervisor.HandleAsync(
            stranger,
            "agent.cancel",
            JsonSerializer.SerializeToElement(new { childSessionId }),
            CancellationToken.None);
        Assert.False(cancel.Ok);
        Assert.Equal("unknown_agent", cancel.ErrorCode);
    }

    [Fact]
    public void Completion_seals_admission_before_fingerprint_and_reopens_on_rejected_begin()
    {
        var requestId = Guid.NewGuid();
        var context = RootContext(requestId.ToString("D"));
        _workspace.SetBaseline(requestId, new RepositoryBaseline("main", "abc", "", true, []));
        _completion.Accept = false;
        _completion.Missing = ["review"];
        var admittedBeforeBarrier = false;
        var rejectedAfterBarrier = false;
        var fingerprintAfterDrain = false;

        _verification.OnVerifyFinal = () =>
        {
            using var mutation = _admission.TryEnterOperation(requestId, "pre-barrier mutation");
            admittedBeforeBarrier = mutation is not null;
        };
        _verification.OnCaptureFingerprint = () =>
        {
            fingerprintAfterDrain = true;
            rejectedAfterBarrier = _admission.TryEnterOperation(requestId, "post-barrier mutation") is null;
        };

        var response = Invoke(
            _supervisor,
            context,
            "submit_completion",
            new { summaryMarkdown = "ready" });

        Assert.True(response.Ok);
        Assert.Equal(false, Result(response.Result)["accepted"]);
        Assert.True(admittedBeforeBarrier);
        Assert.True(rejectedAfterBarrier);
        Assert.True(fingerprintAfterDrain);
        Assert.False(_admission.IsAdmissionClosed(requestId));
        using var afterReject = Assert.IsType<NodeActivityLease>(
            _admission.TryEnterOperation(requestId, "after-rejected-begin"));
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
    public void Accepted_completion_fences_repeated_completion_and_late_results_until_root_stops()
    {
        var requestId = Guid.NewGuid();
        var context = RootContext(requestId.ToString("D"));
        _workspace.SetBaseline(requestId, new RepositoryBaseline("main", "abc", "", true, []));
        _completion.Accept = true;
        var rootCallbacks = Assert.IsType<RequestCallbackLease>(
            _admission.TryRegisterCallbackSource(requestId, context.SessionId));

        var response = Invoke(
            _supervisor,
            context,
            "request.complete",
            new { summaryMarkdown = "shipped" });

        Assert.True(response.Ok);
        Assert.Equal(true, Result(response.Result)["accepted"]);
        Assert.True(_admission.IsAdmissionClosed(requestId));
        Assert.Single(_parentEvents, e => e.Type == "request.completed");

        var repeated = Invoke(
            _supervisor,
            context,
            "request.complete",
            new { summaryMarkdown = "duplicate" });
        var lateResult = Invoke(
            _supervisor,
            context,
            "child.result.submit",
            new { summaryMarkdown = "late" });

        Assert.Equal("admission_closed", repeated.ErrorCode);
        Assert.Equal("admission_closed", lateResult.ErrorCode);
        Assert.Single(_completion.Begun);
        Assert.Single(_completion.Confirmed);
        Assert.Single(_parentEvents, e => e.Type == "request.completed");

        rootCallbacks.Dispose();
        Assert.False(_admission.IsAdmissionClosed(requestId));
    }

    [Fact]
    public async Task Accepted_root_failure_confirms_with_an_exact_quiescence_proof()
    {
        var requestId = Guid.NewGuid();
        var assignment = NodeWorkerTestHarness.Assignment(
            requestId,
            DateTimeOffset.UtcNow.AddMinutes(1)) with
        {
            CanonicalRepositoryPathSnapshot = _repoRoot,
        };
        _completion.Accept = true;

        var outcome = await _supervisor.FailAsync(
            assignment,
            "root-session-1",
            "worker exited",
            CancellationToken.None);

        Assert.Equal(RootTerminalizationOutcome.Accepted, outcome);
        Assert.True(_admission.IsAdmissionClosed(requestId));
        Assert.Equal(
            [(TerminalizationIntent.Fail, "worker exited")],
            _completion.Begun);
        Assert.Equal(
            [(TerminalizationIntent.Fail, "worker exited")],
            _completion.Confirmed);
        var proof = Assert.Single(_completion.Proofs);
        Assert.True(proof.AdmissionClosed);
        Assert.Equal(0, proof.ActiveChildren);
        Assert.Equal(0, proof.ActiveOperations);
        Assert.Equal(0, proof.ActiveProcesses);
        Assert.Equal(0, proof.PendingEvents);
        Assert.Equal(0, proof.ActiveReservations);
        Assert.True(proof.RepositoryInspected);
    }
    [Fact]
    public async Task Pre_root_cancellation_confirms_quiescence_without_a_session_id()
    {
        var requestId = Guid.NewGuid();
        var assignment = NodeWorkerTestHarness.Assignment(
            requestId,
            DateTimeOffset.UtcNow.AddMinutes(1)) with
        {
            CanonicalRepositoryPathSnapshot = _repoRoot,
        };
        _completion.Accept = true;

        var outcome = await _supervisor.CancelBeforeRootAsync(
            assignment,
            "operator_cancel",
            CancellationToken.None);

        Assert.Equal(RootTerminalizationOutcome.Accepted, outcome);
        Assert.Equal([null, null], _completion.RootSessionIds);
        var proof = Assert.Single(_completion.Proofs);
        Assert.True(proof.AdmissionClosed);
        Assert.Equal(0, proof.ActiveChildren);
        Assert.Equal(0, proof.ActiveOperations);
        Assert.Equal(0, proof.ActiveProcesses);
        Assert.Equal(0, proof.PendingEvents);
        Assert.Equal(0, proof.ActiveReservations);
        Assert.True(proof.RepositoryInspected);
    }


    [Fact]
    public async Task Uncertain_root_failure_keeps_admission_closed_and_withholds_confirmation()
    {
        var requestId = Guid.NewGuid();
        var assignment = NodeWorkerTestHarness.Assignment(
            requestId,
            DateTimeOffset.UtcNow.AddMinutes(1)) with
        {
            CanonicalRepositoryPathSnapshot = _repoRoot,
        };
        using var activity = _admission.TryEnterOperation(requestId, "in-flight mutation");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        _completion.Accept = true;

        var outcome = await _supervisor.FailAsync(
            assignment,
            "root-session-1",
            "worker exited",
            cancelled.Token);

        Assert.Equal(RootTerminalizationOutcome.Uncertain, outcome);
        Assert.True(_admission.IsAdmissionClosed(requestId));
        Assert.Single(_completion.Begun);
        Assert.Empty(_completion.Confirmed);
    }

    [Fact]
    public async Task Rejected_failure_confirmation_keeps_admission_closed()
    {
        var requestId = Guid.NewGuid();
        var assignment = NodeWorkerTestHarness.Assignment(
            requestId,
            DateTimeOffset.UtcNow.AddMinutes(1)) with
        {
            CanonicalRepositoryPathSnapshot = _repoRoot,
        };
        _completion.Accept = true;
        _completion.AcceptConfirm = false;

        var outcome = await _supervisor.FailAsync(
            assignment,
            "root-session-1",
            "worker exited",
            CancellationToken.None);

        Assert.Equal(RootTerminalizationOutcome.Rejected, outcome);
        Assert.True(_admission.IsAdmissionClosed(requestId));
        Assert.Single(_completion.Confirmed);
    }

    [Fact]
    public async Task Configured_completion_checkpoint_persists_git_evidence()
    {
        var requestId = Guid.NewGuid();
        var checkpointCalls = 0;
        PiCheckpointRequest? captured = null;
        var context = RootContext(requestId.ToString("D")) with
        {
            CreateCheckpointAsync = (request, _) =>
            {
                checkpointCalls++;
                captured = request;
                return Task.FromResult(PiCheckpointResult.Committed("abc123", "pi/request"));
            },
        };
        _completion.Accept = true;
        using var rootCallbacks = Assert.IsType<RequestCallbackLease>(
            _admission.TryRegisterCallbackSource(requestId, context.SessionId));

        var response = await _supervisor.HandleAsync(
            context,
            "submit_completion",
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
        var repeated = await _supervisor.HandleAsync(
            context,
            "submit_completion",
            JsonSerializer.SerializeToElement(new
            {
                summaryMarkdown = "duplicate",
                changedFiles = new[] { "src/App.cs" },
            }),
            CancellationToken.None);
        Assert.False(repeated.Ok);
        Assert.Equal("admission_closed", repeated.ErrorCode);
        Assert.Equal(1, checkpointCalls);
        Assert.Single(_completion.Evidence);
        Assert.Single(_completion.Begun);
        Assert.Single(_completion.Confirmed);
        Assert.Single(_parentEvents, item => item.Type == "repository.checkpoint_created");
        Assert.Single(_parentEvents, item => item.Type == "request.completed");
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
    private PiChildSessionSupervisor CreateSupervisor(
        PiWorkerOptions worker,
        FakeMailGateway? mail = null,
        Action<NodeRuntimeRoutingStore>? routingCreated = null)
    {
        var inner = new NoopInnerHandler();
        var node = Options.Create(new NodeOptions { Id = Guid.NewGuid() });
        var pi = new PiRuntimeAdapter(
            node,
            Options.Create(worker),
            new NodeWorkerProcessFactory(),
            inner,
            TimeProvider.System,
            NullLogger<PiRuntimeAdapter>.Instance,
            _admission);
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
        var muse = new MuseCodeRuntimeAdapter(
            node,
            Options.Create(new MuseCodeOptions()),
            new MuseProcessFactory(),
            TimeProvider.System,
            NullLogger<MuseCodeRuntimeAdapter>.Instance);
        var registry = new AgentRuntimeRegistry(pi, claude, antigravity, muse);
        var routing = new NodeRuntimeRoutingStore(node, Options.Create(worker));
        routingCreated?.Invoke(routing);
        return new PiChildSessionSupervisor(
            Options.Create(worker),
            routing,
            inner,
            _reservations,
            mail ?? new FakeMailGateway(),
            _identities,
            _spool,
            _assignmentCredentials,
            TimeProvider.System,
            NullLogger.Instance,
            new Lazy<IAgentRuntimeRegistry>(registry),
            new Lazy<IRootSessionSupervisor>(() => new FakeRootSessionSupervisor()),
            new Lazy<INodeAssignmentTerminalizationOrchestrator>(
                () => new ImmediateAssignmentTerminalizationOrchestrator()),
            _verification,
            _repository,
            _crash,
            _completion,
            _workspace,
            _admission);
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
        Assert.Equal("codex/gpt-5.6-sol", payload["model"].GetString());
        Assert.False(payload.ContainsKey("runtimeProfile"));
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
    public async Task CancelSessionAsync_stops_a_running_child_and_releases_identity()
    {
        var context = RootContext(Guid.NewGuid().ToString("D"));
        var spawn = await _supervisor.HandleAsync(
            context,
            "agent.spawn",
            JsonSerializer.SerializeToElement(new
            {
                agentName = "remote-stopped",
                role = "implementer",
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

    private sealed class FakeVerificationCoordinator : IRequestVerificationCoordinator
    {

        public RequestVerificationContext? LastContext { get; private set; }
        public bool Succeed { get; set; } = true;
        public string RejectCode { get; set; } = "";
        public string VerificationFingerprint { get; set; } = "test-fingerprint";
        public string CurrentFingerprint { get; set; } = "test-fingerprint";
        public int FinalVerificationCount { get; private set; }
        public int IntermediateVerificationCount { get; private set; }
        public int FingerprintCaptureCount { get; private set; }
        public Action? OnVerifyFinal { get; set; }
        public Action? OnCaptureFingerprint { get; set; }

        public Task<RequestVerificationDecision> VerifyFinalAsync(
            RequestVerificationContext context,
            CancellationToken cancellationToken)
        {
            FinalVerificationCount++;
            OnVerifyFinal?.Invoke();
            return VerifyAsync(context, VerificationRunKind.Baseline, intermediate: false, cancellationToken);
        }

        public Task<RequestVerificationDecision> VerifyIntermediateAsync(
            RequestVerificationContext context,
            CancellationToken cancellationToken)
        {
            IntermediateVerificationCount++;
            return VerifyAsync(context, VerificationRunKind.Intermediate, intermediate: true, cancellationToken);
        }

        public Task<string> CaptureFingerprintAsync(
            RequestVerificationContext context,
            CancellationToken cancellationToken)
        {
            LastContext = context;
            FingerprintCaptureCount++;
            OnCaptureFingerprint?.Invoke();
            return Task.FromResult(CurrentFingerprint);
        }

        private async Task<RequestVerificationDecision> VerifyAsync(
            RequestVerificationContext context,
            VerificationRunKind runKind,
            bool intermediate,
            CancellationToken cancellationToken)
        {
            LastContext = context;
            if (!string.IsNullOrEmpty(RejectCode))
            {
                var rejected = new RequestVerificationDecision(
                    RequestVerificationDecisionKind.Rejected,
                    "profile not configured",
                    ErrorCode: RejectCode);
                await context.EmitAsync(
                    intermediate ? "verification.intermediate" : "verification.rejected",
                    EventPayload(rejected),
                    cancellationToken);
                return rejected;
            }

            var policy = context.Policy
                ?? throw new InvalidOperationException("Verification policy snapshot is required.");
            var policyRevision = policy.Revision;
            if (!intermediate)
            {
                await context.EmitAsync(
                    "verification.started",
                    new Dictionary<string, object?>
                    {
                        ["fingerprint"] = VerificationFingerprint,
                        ["policyRevision"] = policyRevision,
                    },
                    cancellationToken);
            }

            var completedAt = DateTimeOffset.UtcNow;
            var run = new VerificationRunDto(
                Guid.NewGuid(),
                context.RequestId,
                runKind == VerificationRunKind.Baseline
                    ? IBaselineVerification.ProfileId
                    : policy.TrustedProfileId ?? "test-project-check",
                runKind == VerificationRunKind.Baseline
                    ? IBaselineVerification.RepositoryIntegrityCommandId
                    : "test-project-check",
                Succeed ? VerificationRunStatus.Passed : VerificationRunStatus.Failed,
                Succeed ? 0 : 1,
                completedAt - TimeSpan.FromMilliseconds(1),
                completedAt,
                Succeed ? "ok" : "fail",
                null,
                Mandatory: true,
                VerificationFingerprint,
                policyRevision,
                runKind,
                Guid.NewGuid());
            await context.PersistRunAsync(run, cancellationToken);

            var decision = new RequestVerificationDecision(
                Succeed ? RequestVerificationDecisionKind.Passed : RequestVerificationDecisionKind.Failed,
                Succeed ? "Verification passed." : "Verification failed.",
                VerificationFingerprint,
                policyRevision,
                Succeed ? null : "verification_failed");
            await context.EmitAsync(
                intermediate
                    ? "verification.intermediate"
                    : Succeed
                        ? "verification.completed"
                        : "verification.failed",
                EventPayload(decision),
                cancellationToken);
            return decision;
        }

        private static Dictionary<string, object?> EventPayload(RequestVerificationDecision decision) => new()
        {
            ["decision"] = decision.Kind.ToString(),
            ["fingerprint"] = decision.Fingerprint,
            ["policyRevision"] = decision.PolicyRevision,
            ["summary"] = decision.Summary,
            ["errorCode"] = decision.ErrorCode,
        };
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
        public bool AcceptConfirm { get; set; } = true;
        public IReadOnlyList<string> Missing { get; set; } = ["verification"];
        public List<(string SessionId, VerificationRunDto Run)> Runs { get; } = [];
        public List<CompletionEvidence> Evidence { get; } = [];
        public List<CompletionEvidence> ConfirmedEvidence { get; } = [];
        public List<PiCommandCenter.Application.Completion.AssignmentQuiescenceProof> Proofs { get; } = [];
        public List<(TerminalizationIntent Intent, string? Reason)> Begun { get; } = [];
        public List<(TerminalizationIntent Intent, string? Reason)> Confirmed { get; } = [];
        public List<string?> RootSessionIds { get; } = [];

        public Task RecordVerificationRunAsync(
            string sessionId,
            VerificationRunDto run,
            CancellationToken cancellationToken)
        {
            Runs.Add((sessionId, run));
            return Task.CompletedTask;
        }

        public List<VerificationRunDto> PersistedRuns { get; } = [];
        public List<(string SessionId, Guid ProjectId, Guid RequestId)> ListedRuns { get; } = [];

        public Task<IReadOnlyList<VerificationRunDto>> ListVerificationRunsAsync(
            string sessionId,
            Guid projectId,
            Guid requestId,
            CancellationToken cancellationToken)
        {
            ListedRuns.Add((sessionId, projectId, requestId));
            return Task.FromResult<IReadOnlyList<VerificationRunDto>>(PersistedRuns);
        }

        public Task<CompletionGateDecision> BeginTerminalizationAsync(
            Guid projectId, Guid requestId, string? rootSessionId, TerminalizationIntent intent,
            CompletionEvidence? evidence, string? reason, CancellationToken cancellationToken)
        {
            RootSessionIds.Add(rootSessionId);
            Begun.Add((intent, reason));
            if (evidence is not null)
            {
                Evidence.Add(evidence);
            }

            return Task.FromResult(Decision(requestId, evidence, Accept));
        }

        public Task<CompletionGateDecision> ConfirmTerminalizationAsync(
            Guid projectId, Guid requestId, string? rootSessionId, TerminalizationIntent intent,
            CompletionEvidence? evidence, string? reason, PiCommandCenter.Application.Completion.AssignmentQuiescenceProof proof,
            CancellationToken cancellationToken)
        {
            RootSessionIds.Add(rootSessionId);
            Confirmed.Add((intent, reason));
            if (evidence is not null)
            {
                ConfirmedEvidence.Add(evidence);
            }

            Proofs.Add(proof);
            return Task.FromResult(Decision(requestId, evidence, Accept && AcceptConfirm));
        }

        private CompletionGateDecision Decision(Guid requestId, CompletionEvidence? evidence, bool accepted)
            => new(
                accepted,
                accepted ? [] : Missing,
                accepted && evidence is not null
                    ? new RequestResultDto(
                        requestId,
                        evidence.SummaryMarkdown,
                        evidence.ChangedFiles ?? [],
                        evidence.ReviewFindings,
                        evidence.VerificationSummary,
                        DateTimeOffset.UtcNow,
                        evidence.RequestBranch,
                        evidence.CheckpointCommitId)
                    : null);
    }

    private sealed class ImmediateAssignmentTerminalizationOrchestrator
        : INodeAssignmentTerminalizationOrchestrator
    {
        public Task<CompletionGateDecision> BeginTerminalizationAsync(
            Guid requestId,
            TerminalizationIntent intent,
            Func<CancellationToken, Task<CompletionGateDecision>> beginAsync,
            CancellationToken cancellationToken)
            => beginAsync(cancellationToken);
    }
}
