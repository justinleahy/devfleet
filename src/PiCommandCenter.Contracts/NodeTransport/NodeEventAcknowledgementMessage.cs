namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>
/// Transport message: acknowledgement of the event ids durably accepted by the Control Plane.
/// </summary>
public sealed record NodeEventAcknowledgementMessage(IReadOnlyList<string> EventIds);
