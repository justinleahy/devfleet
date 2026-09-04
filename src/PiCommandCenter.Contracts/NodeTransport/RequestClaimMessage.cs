namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>
/// Transport message: the Control Plane returns a request claim to the claiming node.
/// </summary>
public sealed record RequestClaimMessage(
    Guid RequestId,
    Guid ProjectId,
    Guid NodeId,
    string ClaimToken,
    DateTimeOffset ClaimedAt,
    DateTimeOffset LeaseExpiresAt);
