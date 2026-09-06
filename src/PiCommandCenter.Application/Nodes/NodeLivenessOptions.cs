namespace PiCommandCenter.Application.Nodes;

/// <summary>
/// Shared node heartbeat cadence and freshness policy.
/// </summary>
public sealed class NodeLivenessOptions
{
    public const string SectionName = "Node";

    /// <summary>Configured heartbeat cadence in seconds.</summary>
    public int HeartbeatSeconds { get; set; } = 10;

    /// <summary>A node becomes stale after three configured heartbeat intervals.</summary>
    public TimeSpan StaleAfter => TimeSpan.FromSeconds(3d * Math.Max(1, HeartbeatSeconds));
}
