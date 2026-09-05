using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Runtime.Muse;
using PiCommandCenter.Node.RuntimeRouting;

namespace PiCommandCenter.Node.Tests;

public sealed class RuntimeRoutingConfigurationTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "devfleet-routing", Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task Update_is_live_and_reloaded_from_private_node_storage()
    {
        var node = Options.Create(new NodeOptions { Id = Guid.NewGuid() });
        var worker = Worker();
        using var store = new NodeRuntimeRoutingStore(node, Options.Create(worker));
        var replacement = new UpdateNodeRuntimeConfigurationMessage(
            store.Current.AllowedRoles.Select(role => new RuntimeRoleRouteMessage(
                role,
                [new RuntimeRouteCandidateMessage(role == "reviewer" ? " codex/provider/reviewer " : "codex/default")]))
            .ToArray());

        var saved = await store.UpdateAsync(replacement);
        using var reloaded = new NodeRuntimeRoutingStore(node, Options.Create(worker));

        Assert.Equal("codex/provider/reviewer", saved.RoleRoutes.Single(route => route.Role == "reviewer").Candidates[0].Model);
        Assert.Equal(
            saved.RoleRoutes.SelectMany(route => route.Candidates.Select(candidate => (route.Role, candidate.Model))),
            reloaded.Current.RoleRoutes.SelectMany(route => route.Candidates.Select(candidate => (route.Role, candidate.Model))));
        var json = File.ReadAllText(Path.Combine(worker.AgentDataDirectory, "role-routes.json"));
        Assert.Contains("\"model\": \"codex/provider/reviewer\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("runtimeProfile", json, StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var mode = File.GetUnixFileMode(Path.Combine(worker.AgentDataDirectory, "role-routes.json"));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
    }

    [Theory]
    [InlineData("browser-runtime/default")]
    [InlineData("opus")]
    [InlineData("")]
    public async Task Invalid_update_does_not_replace_current_routes(string model)
    {
        var node = Options.Create(new NodeOptions { Id = Guid.NewGuid() });
        using var store = new NodeRuntimeRoutingStore(node, Options.Create(Worker()));
        var before = store.Current;
        var invalid = new UpdateNodeRuntimeConfigurationMessage(
            store.Current.AllowedRoles.Select(role => new RuntimeRoleRouteMessage(
                role, [new RuntimeRouteCandidateMessage(model)])).ToArray());

        var error = await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateAsync(invalid));

        Assert.Contains("canonical '<runtime>/<model>' selector", error.Message, StringComparison.Ordinal);

        Assert.Same(before, store.Current);
    }

    [Fact]
    public async Task Duplicate_canonical_selectors_in_one_role_are_rejected()
    {
        var node = Options.Create(new NodeOptions { Id = Guid.NewGuid() });
        using var store = new NodeRuntimeRoutingStore(node, Options.Create(Worker()));
        var duplicate = new UpdateNodeRuntimeConfigurationMessage(
            store.Current.AllowedRoles.Select(role => new RuntimeRoleRouteMessage(
                role,
                [new RuntimeRouteCandidateMessage("claude-code/default"), new RuntimeRouteCandidateMessage(" claude-code/default")]))
            .ToArray());

        var error = await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateAsync(duplicate));

        Assert.Contains("duplicate candidate 'claude-code/default'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_profile_routes_file_is_discarded_for_configured_selectors()
    {
        var worker = Worker();
        worker.RoleRoutes["reviewer"] = [new AgentRoleRouteCandidate { Model = "claude-code/claude-opus-test" }];
        var path = Path.Combine(worker.AgentDataDirectory, "role-routes.json");
        Directory.CreateDirectory(worker.AgentDataDirectory);
        File.WriteAllText(path, """
            {"roleRoutes":[
              {"role":"root","candidates":[{"RuntimeProfile":"codex","model":null}]},
              {"role":"architect","candidates":[{"runtimeProfile":"codex","model":null}]},
              {"role":"implementer","candidates":[{"runtimeProfile":"claude","model":"opus"}]},
              {"role":"reviewer","candidates":[{"runtimeProfile":"provider","model":"reviewer"}]},
              {"role":"verifier","candidates":[{"runtimeProfile":"codex","model":null}]}
            ]}
            """);

        using var store = new NodeRuntimeRoutingStore(
            Options.Create(new NodeOptions { Id = Guid.NewGuid() }), Options.Create(worker));

        Assert.False(File.Exists(path));
        Assert.Equal(
            worker.RoleRoutes.SelectMany(pair => pair.Value.Select(candidate => (pair.Key, candidate.Model))),
            store.Current.RoleRoutes.SelectMany(route => route.Candidates.Select(candidate => (route.Role, candidate.Model))));
    }

    [Fact]
    public void Invalid_model_only_routes_file_still_fails_startup()
    {
        var worker = Worker();
        Directory.CreateDirectory(worker.AgentDataDirectory);
        File.WriteAllText(
            Path.Combine(worker.AgentDataDirectory, "role-routes.json"),
            """{"roleRoutes":[{"role":"root","candidates":[{"model":"opus"}]}]}""");

        var error = Assert.Throws<ArgumentException>(() => new NodeRuntimeRoutingStore(
            Options.Create(new NodeOptions { Id = Guid.NewGuid() }), Options.Create(worker)));

        Assert.Contains("canonical '<runtime>/<model>' selector", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discovery_reports_canonical_selectors_per_runtime()
    {
        var worker = Worker();
        worker.RoleRoutes["reviewer"] =
        [
            new AgentRoleRouteCandidate { Model = "claude-code/claude-opus-test" },
            new AgentRoleRouteCandidate { Model = "claude-code/default" },
            new AgentRoleRouteCandidate { Model = "codex/gpt-test" },
        ];
        using var store = new NodeRuntimeRoutingStore(
            Options.Create(new NodeOptions { Id = Guid.NewGuid() }), Options.Create(worker));
        var runner = new FakeModelRunner();
        var discovery = new RuntimeModelDiscovery(
            Options.Create(worker),
            Options.Create(new AntigravityOptions { Executable = "agy-test" }),
            store,
            runner,
            new FakeMuseCatalogReader());

        var catalogs = await discovery.DiscoverAsync();

        Assert.Equal(AgentModelSelector.Runtimes, catalogs.Select(catalog => catalog.Runtime));
        Assert.Equal(
            ["codex/gpt-test"],
            catalogs.Single(catalog => catalog.Runtime == "codex").Models.Select(model => model.Id));
        Assert.Equal(
            ["antigravity/gemini-test"],
            catalogs.Single(catalog => catalog.Runtime == "antigravity").Models.Select(model => model.Id));
        var claude = catalogs.Single(catalog => catalog.Runtime == "claude-code");
        Assert.Equal(
            [
                "claude-code/claude-opus-test",
                "claude-code/default",
                "claude-code/fable",
                "claude-code/haiku",
                "claude-code/opus",
                "claude-code/sonnet",
            ],
            claude.Models.Select(model => model.Id));
        Assert.Null(claude.Error);
        Assert.All(catalogs.SelectMany(catalog => catalog.Models), model => AgentModelSelector.Parse(model.Id));
        var muse = catalogs.Single(catalog => catalog.Runtime == AgentModelSelector.Muse);
        Assert.Empty(muse.Models);
        Assert.Equal("Muse model discovery returned no models.", muse.Error);
        Assert.Contains(runner.Commands, command => command.Executable == "node-test");
        Assert.Contains(runner.Commands, command => command.Executable == "agy-test" && command.Arguments.SequenceEqual(["models"]));
    }

    private PiWorkerOptions Worker() => new()
    {
        NodeExecutable = "node-test",
        WorkerPath = Path.Combine(_root, "runtime", "index.ts"),
        AgentDataDirectory = Path.Combine(_root, "agent-data"),
    };

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FakeModelRunner : IRuntimeModelCommandRunner
    {
        public List<(string Executable, IReadOnlyList<string> Arguments)> Commands { get; } = [];

        public Task<ModelCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            Commands.Add((executable, arguments));
            var output = executable == "node-test"
                ? "[{\"id\":\"codex/gpt-test\",\"displayName\":\"GPT Test\",\"provider\":\"openai-codex\"},"
                    + "{\"id\":\"anthropic/claude-test\",\"displayName\":\"Claude Test\",\"provider\":\"anthropic\"}]"
                : "gemini-test\tGemini Test\n";
            return Task.FromResult(new ModelCommandResult(0, output, string.Empty, false, false));
        }
    }

    private sealed class FakeMuseCatalogReader : IMuseModelCatalogReader
    {
        public Task<MuseModelCatalogResult> ReadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new MuseModelCatalogResult([], null));
    }
}
