using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Runtime.Muse;
using PiCommandCenter.Node.RuntimeRouting;

namespace PiCommandCenter.Node.Tests;

public sealed class RuntimeReadinessProbeAuthTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "devfleet-readiness-auth-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task Pi_requires_both_an_exact_catalog_match_and_usable_current_auth()
    {
        var worker = CreateWorker();
        var runner = new FakeModelRunner((executable, arguments) =>
            executable == "node-test" && arguments.Count == 1
                ? Ok("""
                    [
                      {"id":"codex/gpt-current","authStatus":"ready"},
                      {"id":"zai/glm-expired","authStatus":"unavailable"},
                      {"id":"kimi-coding/k2","authStatus":"unknown"}
                    ]
                    """)
                : Missing());
        var probe = CreateProbe(worker, runner);

        var observed = await probe.ObserveAsync(
            [
                AgentModelSelector.Parse("codex/gpt-current"),
                AgentModelSelector.Parse("codex/not-in-catalog"),
                AgentModelSelector.Parse("zai/glm-expired"),
                AgentModelSelector.Parse("kimi-coding/k2"),
            ],
            CancellationToken.None);

        Assert.Equal(RuntimeReadinessStatuses.Ready, observed["codex/gpt-current"]);
        Assert.Equal(RuntimeReadinessStatuses.Unavailable, observed["codex/not-in-catalog"]);
        Assert.Equal(RuntimeReadinessStatuses.Unavailable, observed["zai/glm-expired"]);
        Assert.Equal(RuntimeReadinessStatuses.Unknown, observed["kimi-coding/k2"]);
    }

    [Fact]
    public async Task Antigravity_catalog_without_a_supported_auth_status_remains_fail_closed()
    {
        var worker = CreateWorker();
        var runner = new FakeModelRunner((executable, arguments) =>
            executable == "agy-test" && arguments.SequenceEqual(["models"])
                ? Ok("gemini-3-pro\tGemini 3 Pro\n")
                : Missing());
        var probe = CreateProbe(
            worker,
            runner,
            new AntigravityOptions { Executable = "agy-test" });

        var observed = await probe.ObserveAsync(
            [
                AgentModelSelector.Parse("antigravity/gemini-3-pro"),
                AgentModelSelector.Parse("antigravity/not-in-catalog"),
            ],
            CancellationToken.None);

        Assert.Equal(RuntimeReadinessStatuses.Unknown, observed["antigravity/gemini-3-pro"]);
        Assert.Equal(RuntimeReadinessStatuses.Unavailable, observed["antigravity/not-in-catalog"]);
        var command = Assert.Single(runner.Commands);
        Assert.Equal("agy-test", command.Executable);
        Assert.Equal(["models"], command.Arguments);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private PiWorkerOptions CreateWorker()
    {
        var runtime = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"))).FullName;
        var agentData = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"))).FullName;
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

    private static RuntimeReadinessProbe CreateProbe(
        PiWorkerOptions worker,
        IRuntimeModelCommandRunner runner,
        AntigravityOptions? antigravity = null)
        => new(
            Options.Create(worker),
            Options.Create(new ClaudeCodeOptions()),
            Options.Create(antigravity ?? new AntigravityOptions()),
            runner,
            new StubMuseCatalogReader());

    private static ModelCommandResult Ok(string output)
        => new(0, output, string.Empty, TimedOut: false, Truncated: false);

    private static ModelCommandResult Missing()
        => new(null, string.Empty, string.Empty, TimedOut: false, Truncated: false);

    private sealed class FakeModelRunner(
        Func<string, IReadOnlyList<string>, ModelCommandResult> handler)
        : IRuntimeModelCommandRunner
    {
        public ConcurrentQueue<(string Executable, IReadOnlyList<string> Arguments)> Commands { get; } = new();

        public Task<ModelCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            Commands.Enqueue((executable, arguments));
            return Task.FromResult(handler(executable, arguments));
        }
    }

    private sealed class StubMuseCatalogReader : IMuseModelCatalogReader
    {
        public Task<MuseModelCatalogResult> ReadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new MuseModelCatalogResult([], [], null));
    }
}
