using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Node.Runtime;
using PiCommandCenter.Node.Runtime.Antigravity;
using PiCommandCenter.Node.Runtime.Claude;

namespace PiCommandCenter.Node.Tests;

public sealed class AgentRuntimeRegistryTests
{
    [Fact]
    public void Resolves_the_fixed_allowlist_and_rejects_arbitrary_profiles()
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
        var registry = new AgentRuntimeRegistry(pi, claude, antigravity);

        Assert.Same(pi, registry.Resolve(AgentRuntimeProfiles.LocalPi));
        Assert.Same(claude, registry.Resolve(AgentRuntimeProfiles.ClaudeReadOnly));
        Assert.Same(claude, registry.Resolve(AgentRuntimeProfiles.ClaudeReservedWrite));
        Assert.Same(antigravity, registry.Resolve(AgentRuntimeProfiles.AntigravityReadOnly));
        Assert.Throws<NotSupportedException>(() => registry.Resolve("custom-binary"));
        Assert.Throws<NotSupportedException>(() => registry.Resolve("antigravity-write"));
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
