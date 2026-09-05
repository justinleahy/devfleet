using PiCommandCenter.Application.Runtime;

namespace PiCommandCenter.Node;

/// <summary>
/// Configuration for the Pi worker runtime, bound from the "Pi" configuration section.
/// </summary>
public sealed class PiWorkerOptions
{
    public const string SectionName = "Pi";

    /// <summary>
    /// Absolute path to the Pi worker entry point (typically
    /// <c>runtime/pi-worker/src/index.ts</c>). Resolved from the repository/content root
    /// when left empty.
    /// </summary>
    public string WorkerPath { get; set; } = string.Empty;

    /// <summary>Node.js executable used to launch the worker process.</summary>
    public string NodeExecutable { get; set; } = "node";

    /// <summary>
    /// Application-controlled directory for Pi session files; '~' expands to the user home.
    /// </summary>
    public string AgentDataDirectory { get; set; } = "~/.local/share/pi-command-center/pi-agent";

    /// <summary>Timeout for the initial <c>session.start</c> handshake, in seconds.</summary>
    public int StartTimeoutSeconds { get; set; } = 30;

    /// <summary>Timeout for correlated protocol requests (<c>session.input</c>, snapshot, tools), in seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>Maximum number of simultaneously running child agents per request (SPEC §10).</summary>
    public int MaxChildAgentsPerRequest { get; set; } = 4;

    /// <summary>Roles a child agent may take (SPEC §13.3 pipeline roles).</summary>
    public string[] AllowedChildRoles { get; set; } =
        ["root", "architect", "implementer", "reviewer", "verifier"];

    /// <summary>Runtime profiles a child agent may run under (SPEC §15).</summary>
    public string[] AllowedRuntimeProfiles { get; set; } =
        [AgentRuntimeProfiles.LocalPi, AgentRuntimeProfiles.ClaudeReadOnly,
            AgentRuntimeProfiles.ClaudeReservedWrite, AgentRuntimeProfiles.AntigravityReadOnly];
}
