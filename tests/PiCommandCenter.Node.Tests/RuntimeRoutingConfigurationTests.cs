using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;
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
                [new RuntimeRouteCandidateMessage("local-pi", role == "reviewer" ? "provider/reviewer" : null)]))
            .ToArray());

        var saved = await store.UpdateAsync(replacement);
        using var reloaded = new NodeRuntimeRoutingStore(node, Options.Create(worker));

        Assert.Equal("provider/reviewer", saved.RoleRoutes.Single(route => route.Role == "reviewer").Candidates[0].Model);
        Assert.Equal(
            saved.RoleRoutes.SelectMany(route => route.Candidates.Select(candidate => (route.Role, candidate.RuntimeProfile, candidate.Model))),
            reloaded.Current.RoleRoutes.SelectMany(route => route.Candidates.Select(candidate => (route.Role, candidate.RuntimeProfile, candidate.Model))));
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var mode = File.GetUnixFileMode(Path.Combine(worker.AgentDataDirectory, "role-routes.json"));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
    }

    [Fact]
    public async Task Invalid_update_does_not_replace_current_routes()
    {
        var node = Options.Create(new NodeOptions { Id = Guid.NewGuid() });
        using var store = new NodeRuntimeRoutingStore(node, Options.Create(Worker()));
        var before = store.Current;
        var invalid = new UpdateNodeRuntimeConfigurationMessage(
            [new RuntimeRoleRouteMessage("reviewer", [new RuntimeRouteCandidateMessage("browser-runtime", null)])]);

        await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateAsync(invalid));

        Assert.Same(before, store.Current);
    }

    [Fact]
    public async Task Discovery_queries_Pi_and_Antigravity_and_reports_Claude_limitation()
    {
        var worker = Worker();
        using var store = new NodeRuntimeRoutingStore(
            Options.Create(new NodeOptions { Id = Guid.NewGuid() }), Options.Create(worker));
        var runner = new FakeModelRunner();
        var discovery = new RuntimeModelDiscovery(
            Options.Create(worker), Options.Create(new AntigravityOptions { Executable = "agy-test" }), store, runner);

        var catalogs = await discovery.DiscoverAsync();

        Assert.Contains(catalogs.Single(catalog => catalog.RuntimeProfile == "local-pi").Models,
            model => model.Id == "anthropic/claude-test");
        Assert.Contains(catalogs.Single(catalog => catalog.RuntimeProfile == "antigravity-readonly").Models,
            model => model.Id == "gemini-test");
        Assert.All(catalogs.Where(catalog => catalog.RuntimeProfile.StartsWith("claude-", StringComparison.Ordinal)),
            catalog => Assert.Contains("does not expose", catalog.Error, StringComparison.Ordinal));
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
                ? "[{\"id\":\"anthropic/claude-test\",\"displayName\":\"Claude Test\",\"provider\":\"anthropic\"}]"
                : "gemini-test\tGemini Test\n";
            return Task.FromResult(new ModelCommandResult(0, output, string.Empty, false, false));
        }
    }
}
