using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Application.VerificationPolicy;

/// <summary>Admin-facing gateway to a live node's bounded verification-policy catalog and selection validation.</summary>
public interface INodeVerificationPolicyGateway
{
    Task<VerificationPolicyCatalogMessage> GetCatalogAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default);

    Task<VerificationProfileSelectionResultMessage> ValidateSelectionAsync(
        Guid nodeId,
        VerificationProfileSelectionRequestMessage request,
        CancellationToken cancellationToken = default);
}
