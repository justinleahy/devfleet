using PiCommandCenter.Domain.Nodes;

namespace PiCommandCenter.Application.Nodes;

/// <summary>
/// Read model of a fleet node.
/// </summary>
public sealed record NodeDto(
    Guid Id,
    string DisplayName,
    string AgentVersion,
    DateTimeOffset LastHeartbeatAt,
    NodeStatus Status,
    string CapabilitiesJson,
    long Version);
