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
    public void Resolves_reserved_harness_providers_to_adapters_and_everything_else_to_pi()
    {
        var node = Options.Create(new NodeOptions { Id = Guid.NewGuid() });
        var pi = new PiRuntimeAdapter(
            node,
            Options.Create(new PiWorkerOptions { WorkerPath = "/tmp/pi.js" }),
            new NodeWorkerProcessFactory(),
            new StubHandler(),
            TimeProvider.System,
            NullLogger<PiRuntimeAdapter>.Instance,
            new Quiescence.RequestAdmissionGate(TimeProvider.System));
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

        Assert.Same(pi, registry.Resolve(AgentModelSelector.Parse("codex/gpt-5.6-sol")));
        Assert.Same(pi, registry.Resolve(AgentModelSelector.Parse("zai/glm-4.7")));
        Assert.Same(pi, registry.Resolve(AgentModelSelector.Parse("kimi-coding/k3")));
        Assert.Same(pi, registry.Resolve(AgentModelSelector.Parse("opencode-go/big-pickle")));
        Assert.Same(pi, registry.Resolve(AgentModelSelector.Parse("custom-binary/custom-model")));
        Assert.Same(claude, registry.Resolve(AgentModelSelector.Parse("claude-code/fable-5-1")));
        Assert.Same(antigravity, registry.Resolve(AgentModelSelector.Parse("antigravity/gemini-3-pro")));
        Assert.Same(muse, registry.Resolve(AgentModelSelector.Parse("muse/muse-spark-1.3")));

        // Selectors fail closed before reaching the registry: no provider, a malformed slug, a
        // case/whitespace variant, the reserved runtime name 'pi', or the deprecated model id
        // 'default' never resolve to an executable. Unknown but syntactically valid providers
        // route only to Pi.
        Assert.Throws<ArgumentException>(() => AgentModelSelector.Parse("custom-binary"));
        Assert.Throws<ArgumentException>(() => AgentModelSelector.Parse("pi/default"));
        Assert.Throws<ArgumentException>(() => AgentModelSelector.Parse("Codex/default"));
        Assert.Throws<ArgumentException>(() => AgentModelSelector.Parse("codex /default"));
        Assert.Throws<ArgumentException>(() => AgentModelSelector.Parse("codex/default"));
        Assert.Throws<ArgumentException>(() => AgentModelSelector.Parse("claude-code/default"));
        Assert.Throws<ArgumentException>(() => AgentModelSelector.Parse("antigravity/default"));
        Assert.Throws<ArgumentException>(() => AgentModelSelector.Parse("muse/default"));
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
