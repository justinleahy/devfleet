using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Verification;

namespace PiCommandCenter.Node.Child;

/// <summary>
/// Production <see cref="INodeCompletionGateway"/>: hub methods
/// <c>RecordVerificationRun</c> and <c>EvaluateCompletion</c>.
/// </summary>
public sealed class NodeTransportCompletionGateway : INodeCompletionGateway
{
    private readonly NodeTransportClient _transport;

    public NodeTransportCompletionGateway(NodeTransportClient transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public Task RecordVerificationRunAsync(VerificationRunDto run, CancellationToken cancellationToken)
        => _transport.RecordVerificationRunAsync(run, cancellationToken);

    public Task<CompletionGateDecision> EvaluateCompletionAsync(
        Guid projectId,
        Guid requestId,
        string rootSessionId,
        CompletionEvidence evidence,
        CancellationToken cancellationToken)
        => _transport.EvaluateCompletionAsync(
            projectId, requestId, rootSessionId, evidence, cancellationToken);
}
