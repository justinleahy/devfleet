namespace PiCommandCenter.Node.Runtime.Claude.Hooks;

/// <summary>Claude Code runtime profile ids consumed by settings installation (SPEC §26.3).</summary>
public static class ClaudeRuntimeProfiles
{
    public const string ReadOnly = "claude-readonly";
    public const string ReservedWrite = "claude-reserved-write";
}
