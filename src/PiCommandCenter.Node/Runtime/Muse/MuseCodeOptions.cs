namespace PiCommandCenter.Node;

/// <summary>
/// Configuration for the official Muse Code (<c>muse</c>) runtime, bound from the "Muse"
/// configuration section. The host is always launched read-only
/// (<c>serve --disable-write --disable-shell --no-session-log</c>); nothing here can widen
/// that posture, and nothing here holds provider credentials.
/// </summary>
public sealed class MuseCodeOptions
{
    public const string SectionName = "Muse";

    /// <summary>Official <c>muse</c> executable. Default matches the installer path on PATH.</summary>
    public string Executable { get; set; } = "muse";

    /// <summary>
    /// Timeout for the whole start sequence (handshake, <c>session/start</c>, first
    /// <c>turn/start</c> acknowledgement), in seconds.
    /// </summary>
    public int StartTimeoutSeconds { get; set; } = 30;

    /// <summary>Timeout for any single later JSON-RPC request acknowledgement, in seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>How long a cancelled turn may take to settle before the host is terminated, in seconds.</summary>
    public int CancelGraceSeconds { get; set; } = 5;

    /// <summary>Maximum retained stderr lines (oldest dropped).</summary>
    public int MaxStderrLines { get; set; } = 200;

    /// <summary>
    /// Hard cap on a single stdout JSON-RPC frame, in bytes (excluding newline). A longer
    /// frame is a protocol fault and closes the session.
    /// </summary>
    public int MaxLineBytes { get; set; } = 1024 * 1024;
}
