namespace PiCommandCenter.Node;

/// <summary>
/// Where the node finds provider OAuth credentials for remaining-quota reads, bound from the
/// "SubscriptionUsage" section. Each path is read as a regular file, and rotated tokens are
/// written back to the same file. An empty path disables that provider's quota read.
/// </summary>
public sealed class SubscriptionUsageOptions
{
    public const string SectionName = "SubscriptionUsage";

    /// <summary>Pi coding agent credential store holding the <c>openai-codex</c> OAuth entry.</summary>
    public string PiCredentialPath { get; set; } = "~/.pi/agent/auth.json";

    /// <summary>Claude Code credential store holding the <c>claudeAiOauth</c> entry.</summary>
    public string ClaudeCredentialPath { get; set; } = "~/.claude/.credentials.json";
}
