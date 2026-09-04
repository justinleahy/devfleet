using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;

namespace PiCommandCenter.Application.Nodes;

/// <summary>
/// Registers a node with the Control Plane.
/// </summary>
public sealed record RegisterNodeCommand(
    NodeId Id,
    string DisplayName,
    string AgentVersion,
    string CapabilitiesJson);
