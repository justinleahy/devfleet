using PiCommandCenter.Domain;

namespace PiCommandCenter.Application.Nodes;

/// <summary>
/// Applies a node heartbeat; also refreshes reported agent metadata.
/// </summary>
public sealed record NodeHeartbeatCommand(
    NodeId Id,
    IReadOnlyList<string> ActiveSessionIds);
