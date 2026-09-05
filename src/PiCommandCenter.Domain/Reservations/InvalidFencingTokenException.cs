namespace PiCommandCenter.Domain.Reservations;

/// <summary>
/// A mutation authorization attempt supplied a fencing token that is no longer current:
/// the lease was renewed through a transfer or forced release. The caller must stop.
/// </summary>
public sealed class InvalidFencingTokenException(Guid leaseId, long expected, long actual)
    : Exception(
        $"Fencing token {actual} is stale for lease '{leaseId}'; the current token is {expected}.")
{
    public Guid LeaseId { get; } = leaseId;

    public long Expected { get; } = expected;

    public long Actual { get; } = actual;
}
