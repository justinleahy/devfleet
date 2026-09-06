using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Verification;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.Child;

/// <summary>
/// Control-plane completion and verification persistence seam. Production delegates to the
/// node hub; tests substitute fakes. The model never evaluates the gate locally.
/// Terminalization is two-step: <see cref="BeginTerminalizationAsync"/> closes admission and
/// runs the objective preflight; <see cref="ConfirmTerminalizationAsync"/> carries the exact
/// quiescence proof and commits the terminal outcome.
/// </summary>
public interface INodeCompletionGateway
{
    Task RecordVerificationRunAsync(
        string sessionId,
        VerificationRunDto run,
        CancellationToken cancellationToken);

    Task<CompletionGateDecision> BeginTerminalizationAsync(
        Guid projectId,
        Guid requestId,
        string rootSessionId,
        TerminalizationIntent intent,
        CompletionEvidence? evidence,
        string? reason,
        CancellationToken cancellationToken);

    Task<CompletionGateDecision> ConfirmTerminalizationAsync(
        Guid projectId,
        Guid requestId,
        string rootSessionId,
        TerminalizationIntent intent,
        CompletionEvidence? evidence,
        string? reason,
        AssignmentQuiescenceProof proof,
        CancellationToken cancellationToken);
}
