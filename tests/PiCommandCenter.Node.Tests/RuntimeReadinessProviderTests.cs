using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Runtime.Muse;
using PiCommandCenter.Node.RuntimeRouting;

namespace PiCommandCenter.Node.Tests;

public sealed class RuntimeReadinessProviderTests : IDisposable
{
    private static readonly DateTimeOffset LocalObservedAt =
        new(2026, 9, 5, 14, 30, 0, TimeSpan.FromHours(5));

    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "devfleet-readiness-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void Routing_revision_normalizes_role_order_and_preserves_candidate_priority()
    {
        using var firstProvider = CreateProvider(new StubRoutingStore(
            Route("reviewer", "claude-code/default", "codex/default"),
            Route("implementer", "muse/default")));
        using var reorderedRolesProvider = CreateProvider(new StubRoutingStore(
            Route("implementer", "muse/default"),
            Route("reviewer", "claude-code/default", "codex/default")));
        using var reorderedCandidatesProvider = CreateProvider(new StubRoutingStore(
            Route("implementer", "muse/default"),
            Route("reviewer", "codex/default", "claude-code/default")));

        var first = firstProvider.Capture([]);
        var reorderedRoles = reorderedRolesProvider.Capture([]);
        var reorderedCandidates = reorderedCandidatesProvider.Capture([]);

        Assert.Equal(first.RoutingRevision, reorderedRoles.RoutingRevision);
        Assert.NotEqual(first.RoutingRevision, reorderedCandidates.RoutingRevision);
        Assert.Matches("^[0-9a-f]{64}$", first.RoutingRevision);
    }

    [Fact]
    public void Candidates_are_unknown_until_a_native_probe_observes_the_current_revision()
    {
        using var provider = CreateProvider(new StubRoutingStore(
            Route("reviewer", "claude-code/default", "codex/gpt-5.6-sol"),
            Route("architect", "antigravity/default", "muse/default")));

        var snapshot = provider.Capture([]);

        Assert.Equal(LocalObservedAt.ToUniversalTime(), snapshot.ObservedAt);
        Assert.Equal(TimeSpan.Zero, snapshot.ObservedAt.Offset);
        Assert.Equal(
            [
                ("architect", "antigravity/default"),
                ("architect", "muse/default"),
                ("reviewer", "claude-code/default"),
                ("reviewer", "codex/gpt-5.6-sol"),
                ("root", "codex/default"),
            ],
            snapshot.Routes.Select(route => (route.Role, route.CanonicalModel)));
        Assert.All(snapshot.Routes, route =>
        {
            Assert.Equal(RuntimeReadinessStatuses.Unknown, route.Readiness);
            Assert.Equal(
                RuntimeReadinessEvidenceSources.UnsupportedNativeObservation,
                route.EvidenceSource);
            Assert.Equal(snapshot.ObservedAt, route.ObservedAt);
            Assert.Equal(TimeSpan.Zero, route.ObservedAt.Offset);
            Assert.Equal(snapshot.RoutingRevision, route.RoutingRevision);
        });
    }

    [Fact]
    public async Task Routing_change_invalidates_observations_until_the_new_revision_is_probed()
    {
        var routing = new StubRoutingStore(
            Route("root", "codex/default"),
            Route("reviewer", "codex/default"));
        using var provider = CreateProvider(routing);
        await provider.RefreshAsync(CancellationToken.None);
        Assert.All(
            provider.Capture([]).Routes,
            route => Assert.Equal(RuntimeReadinessStatuses.Ready, route.Readiness));

        await routing.UpdateAsync(new UpdateNodeRuntimeConfigurationMessage(
            [
                Route("root", "codex/default"),
                Route("reviewer", "codex/gpt-5.6-sol"),
            ]));

        var changed = provider.Capture([]).Routes.Single(route => route.Role == "reviewer");
        Assert.Equal(RuntimeReadinessStatuses.Unknown, changed.Readiness);
        Assert.Equal(
            RuntimeReadinessEvidenceSources.UnsupportedNativeObservation,
            changed.EvidenceSource);

        await provider.RefreshAsync(CancellationToken.None);
        var refreshed = provider.Capture([]).Routes.Single(route => route.Role == "reviewer");
        Assert.Equal(RuntimeReadinessStatuses.Ready, refreshed.Readiness);
        Assert.Equal(RuntimeReadinessEvidenceSources.RuntimeAdapterProbe, refreshed.EvidenceSource);
    }

    [Fact]
    public void Available_slots_use_distinct_assignment_inventory_and_clamp_at_zero()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        using var provider = CreateProvider(
            new StubRoutingStore(Route("root", "codex/default")),
            maxConcurrentRequests: 2);

        var snapshot = provider.Capture([first, first, second, third]);

        Assert.Equal(0, snapshot.AvailableRequestSlots);
        Assert.Equal([first, second, third], snapshot.ActiveAssignmentIds);
    }

    [Fact]
    public async Task Supported_native_evidence_marks_only_proven_candidates_ready()
    {
        var worker = CreateWorker();
        var runner = new FakeModelRunner
        {
            Handler = (executable, arguments) => executable switch
            {
                "node-test" => Ok(
                    """
                    [{"id":"codex/gpt-5.6-sol","authStatus":"ready"},{"id":"zai/glm-4.7","authStatus":"ready"}]
                    """),
                "claude-test" when arguments.SequenceEqual(["auth", "status"]) => Ok("{}"),
                "agy-test" when arguments.SequenceEqual(["models"]) =>
                    Ok("gemini-3-pro\tGemini 3 Pro\n"),
                _ => Missing(),
            },
        };
        var probe = new RuntimeReadinessProbe(
            Options.Create(worker),
            Options.Create(new ClaudeCodeOptions { Executable = "claude-test" }),
            Options.Create(new AntigravityOptions { Executable = "agy-test" }),
            runner,
            new StubMuseCatalogReader(
                new MuseModelCatalogResult(["muse/muse-spark-1.3"], ["muse/muse-spark-1.3"], null)));
        using var provider = CreateProvider(
            new StubRoutingStore(
                Route("root", "codex/default"),
                Route("architect", "zai/glm-4.7", "claude-code/sonnet"),
                Route(
                    "reviewer",
                    "antigravity/gemini-3-pro",
                    "muse/default",
                    "muse/muse-spark-1.3")),
            probe: probe);

        await provider.RefreshAsync(CancellationToken.None);
        var snapshot = provider.Capture([]);

        var unknown = new HashSet<string>(StringComparer.Ordinal)
        {
            "antigravity/gemini-3-pro",
            "muse/default",
            "muse/muse-spark-1.3",
        };
        Assert.All(snapshot.Routes, route =>
        {
            var expected = unknown.Contains(route.CanonicalModel)
                ? RuntimeReadinessStatuses.Unknown
                : RuntimeReadinessStatuses.Ready;
            Assert.Equal(expected, route.Readiness);
            Assert.Equal(RuntimeReadinessEvidenceSources.RuntimeAdapterProbe, route.EvidenceSource);
            Assert.Equal(snapshot.RoutingRevision, route.RoutingRevision);
        });
        Assert.Contains(runner.Commands, command =>
            command.Executable == "node-test"
            && command.Arguments.SequenceEqual([Path.Combine(
                Path.GetDirectoryName(worker.WorkerPath)!,
                "modelCatalog.ts")]));
        Assert.Contains(runner.Commands, command =>
            command.Executable == "claude-test"
            && command.Arguments.SequenceEqual(["auth", "status"]));
        Assert.Contains(runner.Commands, command =>
            command.Executable == "agy-test"
            && command.Arguments.SequenceEqual(["models"]));
    }

    [Fact]
    public async Task Curated_muse_catalog_without_native_evidence_remains_unknown()
    {
        var worker = CreateWorker();
        var catalog = MuseModelCatalogReader.Parse(JsonSerializer.SerializeToElement(new
        {
            models = Array.Empty<object>(),
        }));
        var runner = new FakeModelRunner
        {
            Handler = (executable, _) => executable == "node-test"
                ? Ok("""[{"id":"codex/default-model","authStatus":"ready"}]""")
                : throw new InvalidOperationException(
                    "Muse readiness must not invoke a model command."),
        };
        var probe = new RuntimeReadinessProbe(
            Options.Create(worker),
            Options.Create(new ClaudeCodeOptions()),
            Options.Create(new AntigravityOptions()),
            runner,
            new StubMuseCatalogReader(catalog));
        using var provider = CreateProvider(
            new StubRoutingStore(
                Route("root", "codex/default"),
                Route(
                    "reviewer",
                    "muse/default",
                    "muse/muse-spark-1.3")),
            probe: probe);

        await provider.RefreshAsync(CancellationToken.None);
        var routes = provider.Capture([]).Routes;

        Assert.Equal(
            RuntimeReadinessStatuses.Ready,
            routes.Single(route => route.Role == "root").Readiness);
        Assert.All(
            routes.Where(route => route.Role == "reviewer"),
            route => Assert.Equal(RuntimeReadinessStatuses.Unknown, route.Readiness));
    }

    [Fact]
    public async Task Native_muse_model_without_auth_observation_remains_unknown()
    {
        var worker = CreateWorker();
        var runner = new FakeModelRunner
        {
            Handler = (_, _) => throw new InvalidOperationException(
                "Muse readiness must not invoke a model command."),
        };
        var probe = new RuntimeReadinessProbe(
            Options.Create(worker),
            Options.Create(new ClaudeCodeOptions()),
            Options.Create(new AntigravityOptions()),
            runner,
            new StubMuseCatalogReader(new MuseModelCatalogResult(
                ["muse/muse-spark-1.3"],
                ["muse/muse-spark-1.3"],
                null)));
        using var provider = CreateProvider(
            new StubRoutingStore(Route("reviewer", "muse/muse-spark-1.3")),
            probe: probe);

        await provider.RefreshAsync(CancellationToken.None);
        var route = Assert.Single(
            provider.Capture([]).Routes,
            route => route.Role == "reviewer"
                && route.CanonicalModel == "muse/muse-spark-1.3");

        Assert.Equal(RuntimeReadinessStatuses.Unknown, route.Readiness);
    }

    [Fact]
    public async Task Missing_executable_auth_config_or_model_remains_unavailable()
    {
        var worker = CreateWorker();
        File.Delete(worker.WorkerPath);
        var runner = new FakeModelRunner
        {
            Handler = (executable, _) => executable switch
            {
                "claude-missing-auth" => Failed(),
                "agy-missing" => Missing(),
                _ => throw new InvalidOperationException(
                    $"Unexpected readiness command for '{executable}'."),
            },
        };
        var probe = new RuntimeReadinessProbe(
            Options.Create(worker),
            Options.Create(new ClaudeCodeOptions { Executable = "claude-missing-auth" }),
            Options.Create(new AntigravityOptions { Executable = "agy-missing" }),
            runner,
            new StubMuseCatalogReader(MuseModelCatalogResult.Failure(
                "Muse model discovery requires local login.")));
        using var provider = CreateProvider(
            new StubRoutingStore(
                Route("root", "codex/default"),
                Route("architect", "claude-code/default"),
                Route("reviewer", "antigravity/default", "muse/default")),
            probe: probe);

        await provider.RefreshAsync(CancellationToken.None);
        var snapshot = provider.Capture([]);

        Assert.All(snapshot.Routes, route =>
        {
            Assert.Equal(RuntimeReadinessStatuses.Unavailable, route.Readiness);
            Assert.Equal(RuntimeReadinessEvidenceSources.RuntimeAdapterProbe, route.EvidenceSource);
        });
        Assert.DoesNotContain(runner.Commands, command => command.Executable == "node-test");
    }

    [Fact]
    public async Task Authenticated_provider_does_not_promote_an_unavailable_or_unsupported_model()
    {
        var worker = CreateWorker();
        var runner = new FakeModelRunner
        {
            Handler = (executable, _) => executable switch
            {
                "node-test" => Ok(
                    """[{"id":"codex/gpt-5.6-sol","authStatus":"ready"}]"""),
                "claude-test" => Ok("{}"),
                _ => Missing(),
            },
        };
        var probe = new RuntimeReadinessProbe(
            Options.Create(worker),
            Options.Create(new ClaudeCodeOptions { Executable = "claude-test" }),
            Options.Create(new AntigravityOptions()),
            runner,
            new StubMuseCatalogReader(new MuseModelCatalogResult([], [], null)));
        using var provider = CreateProvider(
            new StubRoutingStore(
                Route("root", "codex/not-configured"),
                Route("reviewer", "claude-code/undocumented-model-id")),
            probe: probe);

        await provider.RefreshAsync(CancellationToken.None);
        var routes = provider.Capture([]).Routes;

        Assert.Equal(
            RuntimeReadinessStatuses.Unavailable,
            routes.Single(route => route.CanonicalModel == "codex/not-configured").Readiness);
        Assert.Equal(
            RuntimeReadinessStatuses.Unknown,
            routes.Single(route => route.CanonicalModel == "claude-code/undocumented-model-id").Readiness);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private PiWorkerOptions CreateWorker()
    {
        var runtime = Directory.CreateDirectory(Path.Combine(
            _root,
            "runtime-" + Guid.NewGuid().ToString("N"))).FullName;
        var agentData = Directory.CreateDirectory(Path.Combine(
            _root,
            "agent-data-" + Guid.NewGuid().ToString("N"))).FullName;
        var workerPath = Path.Combine(runtime, "index.ts");
        File.WriteAllText(workerPath, string.Empty);
        File.WriteAllText(Path.Combine(runtime, "modelCatalog.ts"), string.Empty);
        return new PiWorkerOptions
        {
            NodeExecutable = "node-test",
            WorkerPath = workerPath,
            AgentDataDirectory = agentData,
        };
    }

    private static RuntimeReadinessProvider CreateProvider(
        INodeRuntimeRoutingStore routing,
        int maxConcurrentRequests = 4,
        IRuntimeReadinessProbe? probe = null)
        => new(
            Options.Create(new NodeOptions
            {
                MaxConcurrentRequests = maxConcurrentRequests,
                HeartbeatSeconds = 10,
            }),
            Options.Create(new PiWorkerOptions()),
            routing,
            probe ?? new StubReadinessProbe(),
            new FixedTimeProvider(LocalObservedAt),
            NullLogger<RuntimeReadinessProvider>.Instance);

    private static RuntimeRoleRouteMessage Route(string role, params string[] candidates)
        => new(
            role,
            candidates.Select(candidate => new RuntimeRouteCandidateMessage(candidate)).ToArray());

    private static ModelCommandResult Ok(string output)
        => new(0, output, string.Empty, TimedOut: false, Truncated: false);

    private static ModelCommandResult Failed()
        => new(1, string.Empty, "not authenticated", TimedOut: false, Truncated: false);

    private static ModelCommandResult Missing()
        => new(null, string.Empty, "executable not found", TimedOut: false, Truncated: false);

    private sealed class StubRoutingStore(params RuntimeRoleRouteMessage[] routes)
        : INodeRuntimeRoutingStore
    {
        public NodeRuntimeConfigurationMessage Current { get; private set; } =
            new(Guid.Empty, [], routes);

        public Task<NodeRuntimeConfigurationMessage> UpdateAsync(
            UpdateNodeRuntimeConfigurationMessage update,
            CancellationToken cancellationToken = default)
        {
            Current = new NodeRuntimeConfigurationMessage(Guid.Empty, [], update.RoleRoutes);
            return Task.FromResult(Current);
        }
    }

    private sealed class StubReadinessProbe : IRuntimeReadinessProbe
    {
        public Task<IReadOnlyDictionary<string, string>> ObserveAsync(
            IReadOnlyCollection<AgentModelSelector> candidates,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(
                candidates.ToDictionary(
                    candidate => candidate.Value,
                    _ => RuntimeReadinessStatuses.Ready,
                    StringComparer.Ordinal));
    }

    private sealed class FakeModelRunner : IRuntimeModelCommandRunner
    {
        public required Func<string, IReadOnlyList<string>, ModelCommandResult> Handler { get; init; }

        public ConcurrentQueue<(string Executable, IReadOnlyList<string> Arguments)> Commands { get; } = new();

        public Task<ModelCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            Commands.Enqueue((executable, arguments));
            return Task.FromResult(Handler(executable, arguments));
        }
    }

    private sealed class StubMuseCatalogReader(MuseModelCatalogResult result)
        : IMuseModelCatalogReader
    {
        public Task<MuseModelCatalogResult> ReadAsync(CancellationToken cancellationToken)
            => Task.FromResult(result);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
