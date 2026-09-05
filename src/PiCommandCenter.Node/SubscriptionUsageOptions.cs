using Microsoft.Extensions.Options;

namespace PiCommandCenter.Node;

/// <summary>
/// Paths and commands used to read provider subscription usage, bound from the
/// <c>SubscriptionUsage</c> section. Pi-backed providers come from the bundled sidecar;
/// provider-native supplemental sources use only the credentials they own.
/// </summary>
public sealed class SubscriptionUsageOptions
{
    public const string SectionName = "SubscriptionUsage";

    /// <summary>Repository-relative location of the sidecar entry point.</summary>
    public const string DefaultScriptPath = "runtime/pi-worker/src/usage.ts";

    /// <summary>Default Claude Code OAuth credential store.</summary>
    public const string DefaultClaudeCredentialPath = "~/.claude/.credentials.json";

    /// <summary>
    /// Node.js executable started (without a shell) with <see cref="ScriptPath"/> as its only
    /// argument. A bare name is resolved on the node's PATH; an absolute path pins one install.
    /// Empty disables the sidecar; supplemental sources still run.
    /// </summary>
    public string NodeExecutable { get; set; } = "node";

    /// <summary>
    /// Path to the usage sidecar script. A relative path resolves against the node's working
    /// directory. Empty disables the sidecar; supplemental sources still run.
    /// </summary>
    public string ScriptPath { get; set; } = DefaultScriptPath;

    /// <summary>
    /// Claude Code credential store holding the <c>claudeAiOauth</c> entry. Empty disables
    /// the Anthropic supplemental source.
    /// </summary>
    public string ClaudeCredentialPath { get; set; } = DefaultClaudeCredentialPath;
}

/// <summary>Expands the configured Claude credential path after configuration binding.</summary>
public sealed class SubscriptionUsageOptionsPostConfigure
    : IPostConfigureOptions<SubscriptionUsageOptions>
{
    public void PostConfigure(string? name, SubscriptionUsageOptions options)
    {
        options.ClaudeCredentialPath =
            NodeOptionsPostConfigure.ExpandPath(options.ClaudeCredentialPath);
    }
}
