using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Application.Requests;

/// <summary>
/// Evaluates scheduling eligibility without claiming or otherwise mutating requests.
/// </summary>
public interface IRequestEligibilityEvaluator
{
    /// <summary>
    /// Evaluates requests as one batch so projection callers can share the required data loads.
    /// </summary>
    Task<IReadOnlyDictionary<WorkRequestId, EligibilityDecision>> EvaluateBatchAsync(
        IReadOnlyCollection<WorkRequestId> requestIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates one request, optionally requiring eligibility for a specific candidate node.
    /// Passing no candidate evaluates the request for its designated binding node.
    /// </summary>
    Task<EligibilityDecision> EvaluateAsync(
        WorkRequestId requestId,
        NodeId? candidateNodeId = null,
        CancellationToken cancellationToken = default);
}
