namespace PiCommandCenter.Node;

/// <summary>
/// Configuration for the standalone node, bound from the "Node" configuration section.
/// </summary>
public sealed class NodeOptions
{
    public const string SectionName = "Node";

    /// <summary>Base URL of the Control Plane the node connects to.</summary>
    public string ControlPlaneUrl { get; set; } = "http://127.0.0.1:5057";

    /// <summary>Stable identity of this node. Persisted to the data directory when not configured.</summary>
    public Guid Id { get; set; }

    /// <summary>Human-readable node name; defaults to the machine name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Node agent version; defaults to the entry assembly informational version.</summary>
    public string AgentVersion { get; set; } = string.Empty;

    /// <summary>JSON blob describing node capabilities.</summary>
    public string CapabilitiesJson { get; set; } = "{}";

    /// <summary>Interval between heartbeats, in seconds.</summary>
    public int HeartbeatSeconds { get; set; } = 10;

    /// <summary>Lease duration requested when claiming work, in seconds.</summary>
    public int ClaimLeaseSeconds { get; set; } = 60;

    /// <summary>Path to the local event spool database; '~' expands to the user home.</summary>
    public string EventSpoolPath { get; set; } = "~/.local/share/pi-command-center/node-spool.db";

    /// <summary>When true, a dirty working tree blocks claim start (SPEC §19.4).</summary>
    public bool RequireCleanStart { get; set; } = true;

    /// <summary>When true, untracked files do not fail a clean-start check.</summary>
    public bool AllowUntrackedFiles { get; set; }

    /// <summary>Maximum number of concurrent active request claims this node holds.</summary>
    public int MaxConcurrentRequests { get; set; } = 4;
}
