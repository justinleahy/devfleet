using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Node.Runtime;
using PiCommandCenter.Node.Runtime.Antigravity;
using PiCommandCenter.Node.Runtime.Claude;
using PiCommandCenter.Node.Runtime.Muse;

namespace PiCommandCenter.Node.Tests;

public sealed class AgentRuntimeRegistryTests
{
    [Fact]
    public void Resolves_by_exact_runtime_prefix_and_rejects_unknown_runtimes()
    {
        var node = Options.Create(new NodeOptions { Id = Guid.NewGuid() });
        var pi = new PiRuntimeAdapter(
            node,
            Options.Create(new PiWorkerOptions { WorkerPath = "/tmp/pi.js" }),
            new NodeWorkerProcessFactory(),
            new StubHandler(),
            TimeProvider.System,
            NullLogger<PiRuntimeAdapter>.Instance);
        var settings = Path.GetTempFileName();
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

        Assert.Same(pi, registry.Resolve(AgentModelSelector.Parse("codex/default")));
        Assert.Same(pi, registry.Resolve(AgentModelSelector.Parse("codex/gpt-6-astra")));
        Assert.Same(claude, registry.Resolve(AgentModelSelector.Parse("claude-code/default")));
        Assert.Same(claude, registry.Resolve(AgentModelSelector.Parse("claude-code/fable-5-1")));
        Assert.Same(antigravity, registry.Resolve(AgentModelSelector.Parse("antigravity/default")));
        Assert.Same(muse, registry.Resolve(AgentModelSelector.Parse("muse/default")));
        Assert.Same(muse, registry.Resolve(AgentModelSelector.Parse("muse/muse-1")));

        // Selectors fail closed before reaching the registry: no runtime, unknown runtime, or a
        // case/whitespace variant of a trusted one never resolve to an executable.
        Assert.Throws<ArgumentException>(() => AgentModelSelector.Parse("custom-binary"));
        Assert.Throws<ArgumentException>(() => AgentModelSelector.Parse("custom-binary/default"));
        Assert.Throws<ArgumentException>(() => AgentModelSelector.Parse("Codex/default"));
        Assert.Throws<ArgumentException>(() => AgentModelSelector.Parse("codex /default"));
        Assert.Throws<ArgumentNullException>(() => registry.Resolve(null!));
    }

    private sealed class StubHandler : IPiOrchestrationRequestHandler
    {
        public Task<PiToolResponse> HandleAsync(
            PiOrchestrationContext context,
            string requestType,
            System.Text.Json.JsonElement? payload,
            CancellationToken cancellationToken)
            => Task.FromResult(PiToolResponse.Failure("noop", requestType));
    }
}
