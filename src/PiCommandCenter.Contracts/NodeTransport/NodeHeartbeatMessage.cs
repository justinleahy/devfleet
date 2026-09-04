namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>
/// Transport message: periodic node heartbeat with locally active session ids.
/// </summary>
public sealed record NodeHeartbeatMessage(
    Guid NodeId,
    IReadOnlyList<string> ActiveSessionIds);
