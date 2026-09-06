using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Application.Requests;

/// <summary>
/// Durably cancels queued work or begins cancellation of its retained execution assignment.
/// </summary>
public interface IRequestCancellationService
{
    /// <summary>
    /// Cancels unassigned queued work immediately. Assigned work and its assignment enter
    /// Cancelling together and remain nonterminal until the assigned node proves quiescence.
    /// Exact retries return the current cancellation state.
    /// </summary>
    Task<RequestCancellationResult> CancelAsync(
        WorkRequestId requestId,
        CancelWorkRequestCommand command,
        CancellationToken cancellationToken = default);
}

/// <summary>Operator-provided context for a work-request cancellation.</summary>
public sealed record CancelWorkRequestCommand(string? Reason);

/// <summary>
/// Durable state returned by request cancellation, including the retained owner for best-effort
/// notification when assigned work is still quiescing.
/// </summary>
public sealed record RequestCancellationResult(
    WorkRequestId RequestId,
    ProjectId ProjectId,
    WorkRequestStatus RequestStatus,
    ExecutionAssignmentState? AssignmentState,
    NodeId? AssignedNodeId,
    string Reason);

/// <summary>Raised when a terminal request cannot accept cancellation.</summary>
public sealed class RequestCancellationRejectedException(
    WorkRequestId requestId,
    WorkRequestStatus status)
    : Exception($"Work request '{requestId.Value}' in status '{status}' cannot be cancelled.")
{
    public WorkRequestId RequestId { get; } = requestId;

    public WorkRequestStatus Status { get; } = status;
}
