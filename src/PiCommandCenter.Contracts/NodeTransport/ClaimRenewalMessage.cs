namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>
/// Transport message: node renews an existing claim's lease.
/// </summary>
public sealed record ClaimRenewalMessage(
    Guid RequestId,
    Guid NodeId,
    string ClaimToken,
    int LeaseSeconds);
