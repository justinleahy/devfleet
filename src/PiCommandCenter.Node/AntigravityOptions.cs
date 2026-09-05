namespace PiCommandCenter.Node;

/// <summary>
/// Configuration for the official Antigravity (<c>agy</c>) runtime, bound from the
/// "Antigravity" configuration section. Never holds provider credentials.
/// </summary>
public sealed class AntigravityOptions
{
    public const string SectionName = "Antigravity";

    /// <summary>Official <c>agy</c> executable. Default matches the installer path on PATH.</summary>
    public string Executable { get; set; } = "agy";

    /// <summary>Timeout waiting for the first <c>init</c> event after launch, in seconds.</summary>
    public int StartTimeoutSeconds { get; set; } = 30;

    /// <summary>Grace period after SIGINT before SIGTERM on cancel, in seconds.</summary>
    public int CancelGraceSeconds { get; set; } = 5;

    /// <summary>Maximum retained stderr lines (oldest dropped).</summary>
    public int MaxStderrLines { get; set; } = 200;

    /// <summary>Hard cap on a single stdout NDJSON line, in bytes (excluding newline).</summary>
    public int MaxLineBytes { get; set; } = 1024 * 1024;
}
