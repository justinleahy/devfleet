namespace PiCommandCenter.Node;

/// <summary>
/// Configuration for the official Claude Code runtime, bound from the "Claude" section.
/// Settings isolation is via <see cref="SettingsPath"/> only; credentials stay in the
/// provider-managed default config directory.
/// </summary>
public sealed class ClaudeCodeOptions
{
    public const string SectionName = "Claude";

    /// <summary>Official unmodified <c>claude</c> executable.</summary>
    public string Executable { get; set; } = "claude";

    /// <summary>
    /// Trusted, application-owned settings file passed as <c>--settings</c>.
    /// Never a repository path.
    /// </summary>
    public string SettingsPath { get; set; } = string.Empty;

    /// <summary>Timeout waiting for the <c>system/init</c> session_id, in seconds.</summary>
    public int StartTimeoutSeconds { get; set; } = 30;

    /// <summary>How long to wait after SIGINT before escalating to SIGTERM, in milliseconds.</summary>
    public int CancelGraceMilliseconds { get; set; } = 2000;

    /// <summary>Maximum accepted stdout JSON line length; longer lines become malformed events.</summary>
    public int MaxLineBytes { get; set; } = 1_048_576;

    /// <summary>Cap on synthesized malformed-line events per session.</summary>
    public int MaxMalformedEvents { get; set; } = 64;

    /// <summary>Bounded stderr tail retained for diagnostics.</summary>
    public int MaxStderrLines { get; set; } = 200;
}
