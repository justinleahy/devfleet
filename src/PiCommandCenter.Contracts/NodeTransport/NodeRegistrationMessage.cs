namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>
/// Transport message: node registration handshake (protocolVersion 1).
/// </summary>
public sealed record NodeRegistrationMessage(
    Guid NodeId,
    string DisplayName,
    string AgentVersion,
    string CapabilitiesJson);
