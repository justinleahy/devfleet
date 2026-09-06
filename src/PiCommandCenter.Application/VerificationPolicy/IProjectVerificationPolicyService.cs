using PiCommandCenter.Application.Projects;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;

namespace PiCommandCenter.Application.VerificationPolicy;

/// <summary>
/// Validates a Project's trusted verification-profile selection on its designated live node,
/// then persists only a valid selection. Null clears to baseline-only.
/// </summary>
public interface IProjectVerificationPolicyService
{
    Task<VerificationPolicyCatalogMessage> GetCatalogAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default);

    Task<ProjectDto> SelectAsync(
        ProjectId projectId,
        string? profileId,
        string? profileRevision,
        CancellationToken cancellationToken = default);
}
