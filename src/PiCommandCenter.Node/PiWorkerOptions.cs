using PiCommandCenter.Application.Runtime;

namespace PiCommandCenter.Node;

/// <summary>One trusted model candidate in an ordered node-owned role route.</summary>
public sealed class AgentRoleRouteCandidate
{
    /// <summary>Canonical <c>runtime/model</c> selector (see <see cref="AgentModelSelector"/>).</summary>
    public string Model { get; set; } = string.Empty;
}


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

    /// <summary>
    /// Canonical <c>runtime/model</c> selector for the root agent; <c>codex/default</c> lets the
    /// provider choose its default model.
    /// </summary>
    public string Model { get; set; } = DefaultCodex;

    /// <summary>
    /// Optional system prompt sent to the worker in <c>session.start</c>; empty sends none.
    /// </summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>Timeout for the initial <c>session.start</c> handshake, in seconds.</summary>
    public int StartTimeoutSeconds { get; set; } = 30;

    /// <summary>Timeout for correlated protocol requests (<c>session.input</c>, snapshot, tools), in seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>Maximum number of simultaneously running child agents per request (SPEC §10).</summary>
    public int MaxChildAgentsPerRequest { get; set; } = 4;

    /// <summary>Roles a child agent may take (SPEC §13.3 pipeline roles).</summary>
    public string[] AllowedChildRoles { get; set; } =
        ["root", "architect", "implementer", "reviewer", "verifier"];

    /// <summary>
    /// How often an active child lease is renewed while its owner adapter is alive, in seconds.
    /// </summary>
    public int LeaseRenewalSeconds { get; set; } = 30;

    /// <summary>
    /// Ordered model candidates for each role. The node tries candidates in order;
    /// agent-generated spawn requests cannot override this routing policy.
    /// </summary>
    public Dictionary<string, AgentRoleRouteCandidate[]> RoleRoutes { get; set; } =
        new(StringComparer.Ordinal)
        {
            ["root"] = [Candidate(DefaultCodex)],
            ["architect"] =
            [
                Candidate(DefaultClaude),
                Candidate(DefaultAntigravity),
                Candidate(DefaultMuse),
                Candidate(DefaultCodex),
            ],
            ["implementer"] =
            [
                Candidate(DefaultCodex),
                Candidate(DefaultClaude),
            ],
            ["reviewer"] =
            [
                Candidate(DefaultAntigravity),
                Candidate(DefaultClaude),
                Candidate(DefaultMuse),
                Candidate(DefaultCodex),
            ],
            ["verifier"] =
            [
                Candidate(DefaultCodex),
                Candidate(DefaultClaude),
            ],
        };

    private const string DefaultCodex = AgentModelSelector.Codex + "/" + AgentModelSelector.DefaultModelId;
    private const string DefaultClaude = AgentModelSelector.ClaudeCode + "/" + AgentModelSelector.DefaultModelId;
    private const string DefaultAntigravity = AgentModelSelector.Antigravity + "/" + AgentModelSelector.DefaultModelId;
    private const string DefaultMuse = AgentModelSelector.Muse + "/" + AgentModelSelector.DefaultModelId;

    private static AgentRoleRouteCandidate Candidate(string model) => new() { Model = model };
}
