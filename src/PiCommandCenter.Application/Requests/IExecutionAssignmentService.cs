using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Application.Requests;

/// <summary>
/// Atomically creates durable execution assignments for eligible queued requests and renews the
/// assigned node's lease without transferring assignment ownership.
/// </summary>
public interface IExecutionAssignmentService
{
    /// <summary>
    /// Creates the next eligible assignment for the node. Returns null when no eligible request
    /// exists or a concurrency limit is reached.
    /// </summary>
    /// <exception cref="ArgumentException">Lease is not positive or node id is empty.</exception>
    Task<ExecutionAssignmentDto?> ClaimNextAsync(
        NodeId nodeId,
        TimeSpan lease,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles the node's complete durable inventory against control-plane ownership.
    /// Missing or unproven active assignments are retained and marked recovery-required.
    /// </summary>
    Task<IReadOnlyList<AssignmentReconciliationResultDto>> ReconcileAsync(
        NodeId nodeId,
        IReadOnlyCollection<ExecutionAssignmentInventoryDto> inventory,
        TimeSpan lease,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews the assignment lease. Throws <see cref="InvalidOperationException"/> when the node
    /// or token does not match the durable assignment, or the lease has expired.
    /// </summary>
    /// <returns>The new lease expiry.</returns>
    Task<DateTimeOffset> RenewAsync(
        WorkRequestId requestId,
        NodeId nodeId,
        string claimToken,
        TimeSpan lease,
        CancellationToken cancellationToken = default);
}
