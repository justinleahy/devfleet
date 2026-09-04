namespace PiCommandCenter.Domain.Requests;

/// <summary>
/// A node's claim on a queued work request, valid until <see cref="LeaseExpiresAt"/>. Constructed
/// only through <see cref="RequestClaim.Create"/> or rehydration via <see cref="RequestClaim.Rehydrate"/>.
/// </summary>
public sealed class RequestClaim
{
    private RequestClaim(
        WorkRequestId requestId,
        ProjectId projectId,
        NodeId nodeId,
        string claimToken,
        DateTimeOffset claimedAt,
        DateTimeOffset leaseExpiresAt,
        long version)
    {
        RequestId = requestId;
        ProjectId = projectId;
        NodeId = nodeId;
        ClaimToken = claimToken;
        ClaimedAt = claimedAt;
        LeaseExpiresAt = leaseExpiresAt;
        Version = version;
    }

    public WorkRequestId RequestId { get; }

    public ProjectId ProjectId { get; }

    public NodeId NodeId { get; }

    /// <summary>Opaque non-empty token the node must present to renew the lease.</summary>
    public string ClaimToken { get; }

    public DateTimeOffset ClaimedAt { get; }

    public DateTimeOffset LeaseExpiresAt { get; private set; }

    /// <summary>Optimistic concurrency token.</summary>
    public long Version { get; private set; }

    /// <summary>
    /// Creates a claim. Throws <see cref="ArgumentException"/> when the token is empty, the node id
    /// is empty, the lease is not positive, or the timestamps are inconsistent.
    /// </summary>
    public static RequestClaim Create(
        WorkRequestId requestId,
        ProjectId projectId,
        NodeId nodeId,
        string claimToken,
        DateTimeOffset claimedAt,
        TimeSpan lease)
    {
        EnsureToken(claimToken);
        if (lease <= TimeSpan.Zero)
        {
            throw new ArgumentException("Lease duration must be positive.", nameof(lease));
        }

        return new RequestClaim(
            requestId,
            projectId,
            nodeId,
            claimToken.Trim(),
            claimedAt,
            claimedAt + lease,
            version: 1);
    }

    /// <summary>Rehydrates a persisted claim without mutating timestamps or version.</summary>
    public static RequestClaim Rehydrate(
        WorkRequestId requestId,
        ProjectId projectId,
        NodeId nodeId,
        string claimToken,
        DateTimeOffset claimedAt,
        DateTimeOffset leaseExpiresAt,
        long version)
    {
        EnsureToken(claimToken);
        if (leaseExpiresAt < claimedAt)
        {
            throw new ArgumentException("Lease expiry must not precede the claim time.", nameof(leaseExpiresAt));
        }

        return new RequestClaim(
            requestId,
            projectId,
            nodeId,
            claimToken.Trim(),
            claimedAt,
            leaseExpiresAt,
            version);
    }

    /// <summary>
    /// Renews the lease when the presenting node and token match exactly and the current lease has
    /// not expired. Throws <see cref="InvalidOperationException"/> otherwise.
    /// </summary>
    /// <returns>The new lease expiry.</returns>
    public DateTimeOffset Renew(NodeId nodeId, string claimToken, TimeSpan lease, DateTimeOffset at)
    {
        EnsureToken(claimToken);
        if (lease <= TimeSpan.Zero)
        {
            throw new ArgumentException("Lease duration must be positive.", nameof(lease));
        }

        if (NodeId != nodeId)
        {
            throw new InvalidOperationException($"Claim is owned by node '{NodeId}', not '{nodeId}'.");
        }

        if (!string.Equals(ClaimToken, claimToken.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claim token does not match the active claim.");
        }

        if (LeaseExpiresAt < at)
        {
            throw new InvalidOperationException("Claim lease has already expired and cannot be renewed.");
        }

        LeaseExpiresAt = at + lease;
        Version++;
        return LeaseExpiresAt;
    }

    private static void EnsureToken(string claimToken)
    {
        if (string.IsNullOrWhiteSpace(claimToken))
        {
            throw new ArgumentException("Claim token must not be empty.", nameof(claimToken));
        }

        if (claimToken.Trim().Length == 0)
        {
            throw new ArgumentException("Claim token must not be empty.", nameof(claimToken));
        }
    }
}
