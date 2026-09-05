using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Node.Security;

namespace PiCommandCenter.Node.Tests;

public sealed class ProviderAuthClassifierTests
{
    [Theory]
    [InlineData("Error: not logged in. Run `claude login`")]
    [InlineData("not authenticated. Complete agy login locally.")]
    [InlineData("Authentication required")]
    public void Detects_provider_auth_missing(string diagnostic)
        => Assert.True(ProviderAuthClassifier.IsMissing(diagnostic));

    [Fact]
    public void Snapshot_payload_is_input_required_blocked_with_native_login_reason()
    {
        var payload = ProviderAuthClassifier.SnapshotPayload(
            AgentRuntimeKinds.ClaudeCode,
            """not logged in {"api_key":"sk-ant-secretvalue1234567890"}""");

        Assert.Equal(nameof(AgentAttention.InputRequired), payload["attention"]);
        Assert.Equal(nameof(AgentWorkState.Blocked), payload["workState"]);
        var reason = Assert.IsType<string>(payload["reason"]);
        Assert.Contains("claude login", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not collect", reason, StringComparison.OrdinalIgnoreCase);
        var diagnostic = Assert.IsType<string>(payload["diagnostic"]);
        Assert.DoesNotContain("sk-ant-secret", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Antigravity_reason_names_agy_login()
    {
        var reason = ProviderAuthClassifier.NativeLoginReason(AgentRuntimeKinds.Antigravity);
        Assert.Contains("agy login", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("crash", reason, StringComparison.OrdinalIgnoreCase);
    }
}
