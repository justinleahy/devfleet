using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Application.Completion;

/// <summary>
/// Two-step terminalization authority over a request and its execution assignment.
/// <see cref="BeginAsync"/> closes admission and occupies capacity in Finalizing/Cancelling;
/// <see cref="ConfirmAsync"/> validates an explicit quiescence proof and atomically persists
/// the request result (Complete only), the work request terminal status, and the assignment
/// terminal status. Exact retries return the persisted outcome without reopening.
/// </summary>
public interface IAssignmentTerminalizationService
{
    Task<CompletionGateDecision> BeginAsync(
        NodeId nodeId,
        ProjectId projectId,
        WorkRequestId requestId,
        string claimToken,
        string rootSessionId,
        TerminalizationIntent intent,
        CompletionEvidence? evidence,
        string? reason,
        CancellationToken cancellationToken = default);

    Task<CompletionGateDecision> ConfirmAsync(
        NodeId nodeId,
        ProjectId projectId,
        WorkRequestId requestId,
        string claimToken,
        string rootSessionId,
        TerminalizationIntent intent,
        CompletionEvidence? evidence,
        string? reason,
        AssignmentQuiescenceProof proof,
        CancellationToken cancellationToken = default);

    Task<RequestResultDto?> GetResultAsync(
        WorkRequestId requestId,
        CancellationToken cancellationToken = default);
}
