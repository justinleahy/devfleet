namespace PiCommandCenter.Node.Runtime.Claude;

/// <summary>PoC Claude Code runtime profiles (SPEC §26.3).</summary>
public static class ClaudeCodeProfiles
{
    public const string ReadOnly = "claude-readonly";
    public const string ReservedWrite = "claude-reserved-write";

    public static bool IsSupported(string profile) =>
        string.Equals(profile, ReadOnly, StringComparison.Ordinal)
        || string.Equals(profile, ReservedWrite, StringComparison.Ordinal);
}
