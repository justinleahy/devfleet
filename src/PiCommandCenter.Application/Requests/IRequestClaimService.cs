using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Application.Requests;

/// <summary>
/// Atomically claims queued work requests for a node and maintains their leases. Claim selection
/// is: enabled projects assigned to the node, requests in Queued state, ordered by priority
/// descending then CreatedAt ascending, enforcing one active Development request per node and the
/// project's configured read-only concurrency limit.
/// </summary>
public interface IRequestClaimService
{
    /// <summary>
    /// Claims the next eligible request for the node. Returns null when no eligible request exists
    /// or a concurrency limit is reached.
    /// </summary>
    /// <exception cref="ArgumentException">Lease is not positive or node id is empty.</exception>
    Task<RequestClaimDto?> ClaimNextAsync(NodeId nodeId, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews a claim's lease. Throws <see cref="InvalidOperationException"/> when the node or
    /// token does not match the active claim, or the lease has expired.
    /// </summary>
    /// <returns>The new lease expiry.</returns>
    Task<DateTimeOffset> RenewAsync(WorkRequestId requestId, NodeId nodeId, string claimToken, TimeSpan lease, CancellationToken cancellationToken = default);
}
