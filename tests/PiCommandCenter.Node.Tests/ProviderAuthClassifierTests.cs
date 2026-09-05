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

    [Theory]
    [InlineData("Error: not signed in. Run `muse login` to authenticate.")]
    [InlineData("You are signed out of Muse Code.")]
    [InlineData("Muse: not signed in")]
    [InlineData("Please sign in to Muse before starting a session.")]
    [InlineData("Missing Meta API key")]
    [InlineData("Invalid Meta API key provided")]
    [InlineData("META_API_KEY is not set")]
    [InlineData("error: Meta API key expired")]
    public void Detects_muse_provider_auth_missing(string diagnostic)
        => Assert.True(ProviderAuthClassifier.IsMissing(diagnostic));

    [Theory]
    [InlineData("Meta released a new Llama model today.")]
    [InlineData("Muse session started in read-only mode.")]
    [InlineData("Muse serve listening on stdio; write tools disabled.")]
    [InlineData("Rotate your Meta API key periodically for hygiene.")]
    [InlineData("Model catalog contains 12 Meta models.")]
    [InlineData("Login page rendered by the muse UI theme.")]
    public void Unrelated_muse_or_meta_text_is_not_auth_failure(string diagnostic)
        => Assert.False(ProviderAuthClassifier.IsMissing(diagnostic));

    [Fact]
    public void Muse_reason_is_exact_credential_free_native_guidance()
    {
        var reason = ProviderAuthClassifier.NativeLoginReason(AgentRuntimeKinds.Muse);
        Assert.Equal(
            "Complete Muse Code login locally (muse login). The Command Center does not collect provider credentials.",
            reason);
    }

    [Fact]
    public void Muse_snapshot_payload_blocks_with_native_guidance_and_redacts_secrets()
    {
        var payload = ProviderAuthClassifier.SnapshotPayload(
            AgentRuntimeKinds.Muse,
            """signed out of Muse {"api_key":"meta-secretvalue1234567890"} Bearer abcDEF123token /home/alice/.muse/credentials.json""");

        Assert.Equal(nameof(AgentAttention.InputRequired), payload["attention"]);
        Assert.Equal(nameof(AgentWorkState.Blocked), payload["workState"]);
        Assert.Equal("provider_native_login_required", payload["auth"]);
        var reason = Assert.IsType<string>(payload["reason"]);
        Assert.Contains("muse login", reason, StringComparison.Ordinal);
        Assert.Equal(reason, payload["statusReason"]);
        var diagnostic = Assert.IsType<string>(payload["diagnostic"]);
        Assert.DoesNotContain("meta-secretvalue", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("abcDEF123token", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/alice", diagnostic, StringComparison.Ordinal);
    }
}
