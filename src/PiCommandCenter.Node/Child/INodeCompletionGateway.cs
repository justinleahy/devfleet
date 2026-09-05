using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Verification;

namespace PiCommandCenter.Node.Child;

/// <summary>
/// Control-plane completion and verification persistence seam. Production delegates to the
/// node hub; tests substitute fakes. The model never evaluates the gate locally.
/// </summary>
public interface INodeCompletionGateway
{
    Task RecordVerificationRunAsync(VerificationRunDto run, CancellationToken cancellationToken);

    Task<CompletionGateDecision> EvaluateCompletionAsync(
        Guid projectId,
        Guid requestId,
        string rootSessionId,
        CompletionEvidence evidence,
        CancellationToken cancellationToken);
}
