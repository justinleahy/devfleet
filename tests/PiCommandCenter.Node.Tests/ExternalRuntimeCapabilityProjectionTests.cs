using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Node.Runtime.Antigravity;
using PiCommandCenter.Node.Runtime.Claude;

namespace PiCommandCenter.Node.Tests;

/// <summary>
/// Capability flags come from the live adapter profile/features, never version strings (SPEC §28).
/// </summary>
public sealed class ExternalRuntimeCapabilityProjectionTests
{
    [Fact]
    public void Claude_headless_print_does_not_advertise_send_or_child_spawn()
    {
        var settings = Path.GetTempFileName();
        File.WriteAllText(settings, "{}");
        var adapter = new ClaudeCodeRuntimeAdapter(
            Options.Create(new NodeOptions { Id = Guid.NewGuid() }),
            Options.Create(new ClaudeCodeOptions { SettingsPath = settings }),
            new OfficialAgentProcessFactory(),
            TimeProvider.System,
            NullLogger<ClaudeCodeRuntimeAdapter>.Instance);

        Assert.Equal(AgentRuntimeKinds.ClaudeCode, adapter.RuntimeKind);
        Assert.True(adapter.Capabilities.SupportsStreamingEvents);
        Assert.False(adapter.Capabilities.SupportsSendInput);
        Assert.True(adapter.Capabilities.SupportsCancel);
        Assert.True(adapter.Capabilities.SupportsSnapshot);
        Assert.False(adapter.Capabilities.SupportsChildSpawn);
        Assert.False(adapter.Capabilities.SupportsPlanTools);
        File.Delete(settings);
    }

    [Fact]
    public void Antigravity_read_only_reviewer_advertises_send_but_not_child_spawn()
    {
        var adapter = new AntigravityRuntimeAdapter(
            Options.Create(new NodeOptions { Id = Guid.NewGuid() }),
            Options.Create(new AntigravityOptions()),
            new AntigravityProcessFactory(),
            TimeProvider.System,
            NullLogger<AntigravityRuntimeAdapter>.Instance);

        Assert.Equal(AgentRuntimeKinds.Antigravity, adapter.RuntimeKind);
        Assert.True(adapter.Capabilities.SupportsStreamingEvents);
        Assert.True(adapter.Capabilities.SupportsSendInput);
        Assert.True(adapter.Capabilities.SupportsCancel);
        Assert.True(adapter.Capabilities.SupportsSnapshot);
        Assert.False(adapter.Capabilities.SupportsChildSpawn);
        Assert.False(adapter.Capabilities.SupportsPlanTools);
    }

    [Fact]
    public void Unsupported_controls_stay_off_even_when_compared_to_Pi_full_surface()
    {
        var none = AgentRuntimeCapabilities.None;
        Assert.False(none.SupportsStreamingEvents);

        var claude = new ClaudeCodeRuntimeAdapter(
            Options.Create(new NodeOptions { Id = Guid.NewGuid() }),
            Options.Create(new ClaudeCodeOptions { SettingsPath = Path.GetTempFileName() }),
            new OfficialAgentProcessFactory(),
            TimeProvider.System,
            NullLogger<ClaudeCodeRuntimeAdapter>.Instance).Capabilities;

        Assert.NotEqual(none, claude);
        Assert.False(claude.SupportsPlanTools);
        Assert.False(claude.SupportsChildSpawn);
    }
}
