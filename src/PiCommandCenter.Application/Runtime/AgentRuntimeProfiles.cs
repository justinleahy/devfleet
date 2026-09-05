namespace PiCommandCenter.Application.Runtime;

/// <summary>Exact configured runtime-profile allowlist for child spawn.</summary>
public static class AgentRuntimeProfiles
{
    public const string LocalPi = "local-pi";
    public const string ClaudeReadOnly = "claude-readonly";
    public const string ClaudeReservedWrite = "claude-reserved-write";
    public const string AntigravityReadOnly = "antigravity-readonly";
}
