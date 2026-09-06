using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Verification;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.Child;

/// <summary>
/// Production <see cref="INodeCompletionGateway"/>: hub methods
/// <c>RecordVerificationRun</c>, <c>BeginTerminalization</c>, and <c>ConfirmTerminalization</c>.
/// </summary>
public sealed class NodeTransportCompletionGateway : INodeCompletionGateway
{
    private readonly NodeTransportClient _transport;
    private readonly INodeAssignmentCredentialSource _credentials;

    public NodeTransportCompletionGateway(
        NodeTransportClient transport,
        INodeAssignmentCredentialSource credentials)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    public Task RecordVerificationRunAsync(
        string sessionId,
        VerificationRunDto run,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(run);
        var credential = RequireCredential(run.RequestId);
        return _transport.RecordVerificationRunAsync(
            run,
            credential.ProjectId,
            credential.ClaimToken,
            sessionId,
            cancellationToken);
    }

    public Task<CompletionGateDecision> BeginTerminalizationAsync(
        Guid projectId,
        Guid requestId,
        string rootSessionId,
        TerminalizationIntent intent,
        CompletionEvidence? evidence,
        string? reason,
        CancellationToken cancellationToken)
    {
        var credential = RequireCredential(requestId, projectId);
        return _transport.BeginTerminalizationAsync(
            projectId,
            requestId,
            credential.ClaimToken,
            rootSessionId,
            intent,
            evidence,
            reason,
            cancellationToken);
    }

    public Task<CompletionGateDecision> ConfirmTerminalizationAsync(
        Guid projectId,
        Guid requestId,
        string rootSessionId,
        TerminalizationIntent intent,
        CompletionEvidence? evidence,
        string? reason,
        AssignmentQuiescenceProof proof,
        CancellationToken cancellationToken)
    {
        var credential = RequireCredential(requestId, projectId);
        return _transport.ConfirmTerminalizationAsync(
            projectId,
            requestId,
            credential.ClaimToken,
            rootSessionId,
            intent,
            evidence,
            reason,
            proof,
            cancellationToken);
    }

    private NodeAssignmentCredential RequireCredential(Guid requestId, Guid? projectId = null)
    {
        if (!_credentials.TryGetByRequest(requestId, out var credential))
        {
            throw new InvalidOperationException(
                $"No active assignment credential is available for request '{requestId}'.");
        }

        if (projectId is Guid expectedProjectId && credential.ProjectId != expectedProjectId)
        {
            throw new InvalidOperationException(
                $"The active assignment credential for request '{requestId}'"
                + $" does not belong to project '{expectedProjectId}'.");
        }

        return credential;
    }
}
