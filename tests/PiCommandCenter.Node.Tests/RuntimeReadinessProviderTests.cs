using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Runtime.Muse;
using PiCommandCenter.Node.RuntimeRouting;
using PiCommandCenter.Node.Verification;

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
            Route("reviewer", "claude-code/fable-5-1", "codex/gpt-5.6-sol"),
            Route("implementer", "muse/muse-spark-1.3")));
        using var reorderedRolesProvider = CreateProvider(new StubRoutingStore(
            Route("implementer", "muse/muse-spark-1.3"),
            Route("reviewer", "claude-code/fable-5-1", "codex/gpt-5.6-sol")));
        using var reorderedCandidatesProvider = CreateProvider(new StubRoutingStore(
            Route("implementer", "muse/muse-spark-1.3"),
            Route("reviewer", "codex/gpt-5.6-sol", "claude-code/fable-5-1")));

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
            Route("reviewer", "claude-code/fable-5-1", "codex/gpt-5.6-sol"),
            Route("architect", "antigravity/gemini-3-pro", "muse/muse-spark-1.3")));

        var snapshot = provider.Capture([]);

        Assert.Equal(LocalObservedAt.ToUniversalTime(), snapshot.ObservedAt);
        Assert.Equal(TimeSpan.Zero, snapshot.ObservedAt.Offset);
        Assert.Equal(
            [
                ("architect", "antigravity/gemini-3-pro"),
                ("architect", "muse/muse-spark-1.3"),
                ("reviewer", "claude-code/fable-5-1"),
                ("reviewer", "codex/gpt-5.6-sol"),
                ("root", "codex/gpt-5.6-sol"),
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
    public void Capture_includes_baseline_only_verification_policy_when_no_profiles_are_configured()
    {
        using var provider = CreateProvider(new StubRoutingStore(Route("reviewer", "codex/gpt-5.6-sol")));

        var snapshot = provider.Capture([]);

        var policy = snapshot.VerificationPolicy;
        Assert.NotNull(policy);
        Assert.Equal(TimeSpan.Zero, policy.ObservedAt.Offset);
        Assert.True(policy.BaselineAvailable);
        Assert.Equal(VerificationBaselineIds.Version, policy.BaselineVersion);
        Assert.Empty(policy.Profiles);
    }

    [Fact]
    public void Catalog_projects_only_safe_profile_metadata_and_fails_closed_on_invalid_entries()
    {
        var options = new VerificationOptions
        {
            Profiles =
            {
                ["dotnet"] = new VerificationProfileOptions
                {
                    Id = "dotnet",
                    DisplayLabel = "Dotnet checks",
                    Commands =
                    [
                        new VerificationCommandOptions
                        {
                            Id = "test",
                            DisplayLabel = "dotnet test",
                            Executable = "/usr/bin/dotnet",
                            Arguments = ["test"],
                            WorkingDirectory = "src",
                            TimeoutSeconds = 120,
                            Mandatory = true,
                        },
                    ],
                },
                ["dup"] = new VerificationProfileOptions
                {
                    Id = "dotnet",
                    Commands =
                    [
                        new VerificationCommandOptions { Id = "other", Executable = "echo", TimeoutSeconds = 5 },
                    ],
                },
                ["bad"] = new VerificationProfileOptions
                {
                    Id = new string('x', 129),
                    Commands =
                    [
                        new VerificationCommandOptions { Id = "x", Executable = "echo", TimeoutSeconds = 5 },
                    ],
                },
                ["reserved"] = new VerificationProfileOptions
                {
                    Id = "reserved",
                    Commands =
                    [
                        new VerificationCommandOptions
                        {
                            Id = $" {VerificationBaselineIds.WhitespaceCommandId} ",
                            Executable = "dotnet",
                            TimeoutSeconds = 5,
                            Mandatory = true,
                        },
                    ],
                },
                ["empty"] = new VerificationProfileOptions { Id = "empty", Commands = [] },
            },
        };
        var catalog = new VerificationPolicyCatalogProvider(
            Options.Create(options),
            new FixedTimeProvider(LocalObservedAt));

        var captured = catalog.Capture();

        var profile = Assert.Single(captured.Profiles);
        Assert.Equal("dotnet", profile.Id);
        Assert.Equal("Dotnet checks", profile.DisplayLabel);
        Assert.Matches("^[0-9a-f]{64}$", profile.Revision);
        var command = Assert.Single(profile.Commands);
        Assert.Equal("test", command.Id);
        Assert.Equal("dotnet test", command.DisplayLabel);
        Assert.Equal("src", command.WorkingDirectoryLabel);
        Assert.True(command.Mandatory);
        Assert.Equal(120, command.TimeoutSeconds);
        var json = JsonSerializer.Serialize(captured);
        Assert.DoesNotContain("/usr/bin/dotnet", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Executable", json, StringComparison.Ordinal);

        var projectId = Guid.NewGuid();
        var bindingId = Guid.NewGuid();
        var accepted = catalog.ValidateSelection(new VerificationProfileSelectionRequestMessage(
            projectId,
            bindingId,
            3,
            profile.Id,
            profile.Revision));
        Assert.True(accepted.Accepted);
        Assert.Equal(VerificationPolicySelectionCodes.Accepted, accepted.Code);

        var stale = catalog.ValidateSelection(new VerificationProfileSelectionRequestMessage(
            projectId,
            bindingId,
            3,
            profile.Id,
            "not-the-revision"));
        Assert.False(stale.Accepted);
        Assert.Equal(VerificationPolicySelectionCodes.Stale, stale.Code);

        var missing = catalog.ValidateSelection(new VerificationProfileSelectionRequestMessage(
            projectId,
            bindingId,
            3,
            "unknown",
            profile.Revision));
        Assert.False(missing.Accepted);
        Assert.Equal(VerificationPolicySelectionCodes.Missing, missing.Code);

        var cleared = catalog.ValidateSelection(new VerificationProfileSelectionRequestMessage(
            projectId,
            bindingId,
            3,
            null,
            null));
        Assert.True(cleared.Accepted);
        Assert.Equal(VerificationPolicySelectionCodes.Cleared, cleared.Code);

        var malformed = catalog.ValidateSelection(new VerificationProfileSelectionRequestMessage(
            Guid.Empty,
            bindingId,
            3,
            profile.Id,
            profile.Revision));
        Assert.False(malformed.Accepted);
        Assert.Equal(VerificationPolicySelectionCodes.Malformed, malformed.Code);
    }

    [Fact]
    public void Fallback_catalog_revision_changes_with_execution_affecting_fields()
    {
        var baseline = CaptureProfile(Command());
        Assert.Matches("^[0-9a-f]{64}$", baseline.Revision);
        Assert.DoesNotContain("/usr/bin/dotnet", JsonSerializer.Serialize(baseline), StringComparison.Ordinal);

        Assert.NotEqual(baseline.Revision, CaptureProfile(Command(executable: "/usr/bin/dotnet-preview")).Revision);
        Assert.NotEqual(baseline.Revision, CaptureProfile(Command(arguments: ["test", "--filter", "x"])).Revision);
        Assert.NotEqual(baseline.Revision, CaptureProfile(Command(workingDirectory: "tests")).Revision);
        Assert.NotEqual(baseline.Revision, CaptureProfile(Command(timeoutSeconds: 30)).Revision);
        Assert.NotEqual(baseline.Revision, CaptureProfile(Command(mandatory: false)).Revision);
        Assert.Equal(baseline.Revision, CaptureProfile(Command()).Revision);
    }

    [Fact]
    public void Explicit_profile_revision_is_honored_in_the_catalog()
    {
        var options = new VerificationOptions
        {
            Profiles =
            {
                ["dotnet"] = new VerificationProfileOptions
                {
                    Id = "dotnet",
                    Revision = "pinned-revision-1",
                    Commands = [Command()],
                },
            },
        };

        var profile = Assert.Single(CaptureCatalog(options).Profiles);

        Assert.Equal("pinned-revision-1", profile.Revision);
        Assert.DoesNotContain("/usr/bin/dotnet", JsonSerializer.Serialize(profile), StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_omits_profiles_that_cannot_fit_assignment_policy_revision()
    {
        var compositeBudget = 128 - "baseline:1+".Length - 1;
        var fitId = new string('a', 50);
        var fitRevision = new string('r', compositeBudget - 50);
        var overId = new string('b', 51);
        var overRevision = new string('r', compositeBudget - 50);

        var options = new VerificationOptions
        {
            Profiles =
            {
                [fitId] = new VerificationProfileOptions
                {
                    Id = fitId,
                    Revision = fitRevision,
                    Commands = [Command()],
                },
                [overId] = new VerificationProfileOptions
                {
                    Id = overId,
                    Revision = overRevision,
                    Commands = [Command()],
                },
            },
        };

        var captured = CaptureCatalog(options);
        var profile = Assert.Single(captured.Profiles);
        Assert.Equal(fitId, profile.Id);
        Assert.Equal(128, $"baseline:1+{profile.Id}@{profile.Revision}".Length);

        var catalog = new VerificationPolicyCatalogProvider(
            Options.Create(options),
            new FixedTimeProvider(LocalObservedAt));
        var missing = catalog.ValidateSelection(new VerificationProfileSelectionRequestMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            overId,
            overRevision));
        Assert.False(missing.Accepted);
        Assert.Equal(VerificationPolicySelectionCodes.Missing, missing.Code);
    }

    [Fact]
    public void Catalog_omits_profiles_whose_mandatory_command_json_exceeds_4096()
    {
        var fit = new VerificationProfileOptions
        {
            Id = "fit-mandatory",
            Revision = "r1",
            Commands = MandatoryCommands(31, idLength: 128),
        };
        var over = new VerificationProfileOptions
        {
            Id = "over-mandatory",
            Revision = "r1",
            Commands = MandatoryCommands(32, idLength: 128),
        };
        var options = new VerificationOptions
        {
            Profiles =
            {
                ["fit-mandatory"] = fit,
                ["over-mandatory"] = over,
            },
        };

        var captured = CaptureCatalog(options);
        var profile = Assert.Single(captured.Profiles);
        Assert.Equal("fit-mandatory", profile.Id);
    }



    [Fact]
    public async Task Routing_change_invalidates_observations_until_the_new_revision_is_probed()
    {
        var routing = new StubRoutingStore(
            Route("root", "codex/gpt-5.6-sol"),
            Route("reviewer", "codex/gpt-5.6-sol"));
        using var provider = CreateProvider(routing);
        await provider.RefreshAsync(CancellationToken.None);
        Assert.All(
            provider.Capture([]).Routes,
            route => Assert.Equal(RuntimeReadinessStatuses.Ready, route.Readiness));

        await routing.UpdateAsync(new UpdateNodeRuntimeConfigurationMessage(
            [
                Route("root", "codex/gpt-5.6-sol"),
                Route("reviewer", "codex/gpt-reviewer"),
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
            new StubRoutingStore(Route("root", "codex/gpt-5.6-sol")),
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
                Route("root", "codex/gpt-5.6-sol"),
                Route("architect", "zai/glm-4.7", "claude-code/sonnet"),
                Route(
                    "reviewer",
                    "antigravity/gemini-3-pro",
                    "muse/muse-spark-1.3")),
            probe: probe);

        await provider.RefreshAsync(CancellationToken.None);
        var snapshot = provider.Capture([]);

        var unknown = new HashSet<string>(StringComparer.Ordinal)
        {
            "antigravity/gemini-3-pro",
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
                ? Ok("""[{"id":"codex/gpt-5.6-sol","authStatus":"ready"}]""")
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
                Route("root", "codex/gpt-5.6-sol"),
                Route(
                    "reviewer",
                    "muse/muse-spark-1.3")),
            probe: probe);

        await provider.RefreshAsync(CancellationToken.None);
        var routes = provider.Capture([]).Routes;

        Assert.Equal(
            RuntimeReadinessStatuses.Unknown,
            routes.Single(route => route.Role == "reviewer").Readiness);
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
                Route("root", "codex/gpt-5.6-sol"),
                Route("architect", "claude-code/fable-5-1"),
                Route("reviewer", "antigravity/gemini-3-pro", "muse/muse-spark-1.3")),
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

    private static VerificationCommandOptions Command(
        string executable = "/usr/bin/dotnet",
        string[]? arguments = null,
        string workingDirectory = "src",
        int timeoutSeconds = 120,
        bool mandatory = true) =>
        new()
        {
            Id = "test",
            DisplayLabel = "dotnet test",
            Executable = executable,
            Arguments = arguments ?? ["test"],
            WorkingDirectory = workingDirectory,
            TimeoutSeconds = timeoutSeconds,
            Mandatory = mandatory,
        };

    private static List<VerificationCommandOptions> MandatoryCommands(int count, int idLength)
    {
        var commands = new List<VerificationCommandOptions>(count);
        for (var index = 0; index < count; index++)
        {
            var suffix = index.ToString("x2");
            var id = new string('c', idLength - suffix.Length) + suffix;
            commands.Add(new VerificationCommandOptions
            {
                Id = id,
                DisplayLabel = id,
                Executable = "/usr/bin/dotnet",
                Arguments = ["test"],
                WorkingDirectory = "src",
                TimeoutSeconds = 120,
                Mandatory = true,
            });
        }

        return commands;
    }


    private static VerificationPolicyCatalogMessage CaptureCatalog(VerificationOptions options) =>
        new VerificationPolicyCatalogProvider(
            Options.Create(options),
            new FixedTimeProvider(LocalObservedAt)).Capture();

    private static VerificationPolicyProfileMessage CaptureProfile(VerificationCommandOptions command)
    {
        var options = new VerificationOptions
        {
            Profiles =
            {
                ["dotnet"] = new VerificationProfileOptions
                {
                    Id = "dotnet",
                    DisplayLabel = "Dotnet checks",
                    Commands = [command],
                },
            },
        };
        return Assert.Single(CaptureCatalog(options).Profiles);
    }

    private static RuntimeReadinessProvider CreateProvider(
        INodeRuntimeRoutingStore routing,
        int maxConcurrentRequests = 4,
        IRuntimeReadinessProbe? probe = null,
        IVerificationPolicyCatalog? verificationPolicies = null)
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
            NullLogger<RuntimeReadinessProvider>.Instance,
            verificationPolicies ?? new EmptyVerificationPolicyCatalog(LocalObservedAt));

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

    private sealed class EmptyVerificationPolicyCatalog(DateTimeOffset observedAt) : IVerificationPolicyCatalog
    {
        public VerificationPolicyCatalogMessage Capture() =>
            new(
                observedAt.ToUniversalTime(),
                BaselineAvailable: true,
                VerificationBaselineIds.Version,
                []);

        public VerificationProfileSelectionResultMessage ValidateSelection(
            VerificationProfileSelectionRequestMessage request)
            => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
