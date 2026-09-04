namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>
/// Transport message: node asks to atomically claim the next queued request.
/// </summary>
public sealed record ClaimRequestMessage(
    Guid NodeId,
    int LeaseSeconds);
