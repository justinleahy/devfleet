using PiCommandCenter.Application.Projects;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;

namespace PiCommandCenter.Application.VerificationPolicy;

/// <summary>
/// Loads the Project's designated WorkspaceBinding, asks that exact connected node to validate
/// the requested profile/revision, and persists only a valid selection.
/// </summary>
public sealed class ProjectVerificationPolicyService(
    IProjectCatalog projects,
    INodeVerificationPolicyGateway gateway) : IProjectVerificationPolicyService
{
    public async Task<VerificationPolicyCatalogMessage> GetCatalogAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await projects.GetAsync(projectId, cancellationToken).ConfigureAwait(false);
        var binding = RequireBinding(project);
        return await gateway.GetCatalogAsync(binding.NodeId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectDto> SelectAsync(
        ProjectId projectId,
        string? profileId,
        string? profileRevision,
        CancellationToken cancellationToken = default)
    {
        var project = await projects.GetAsync(projectId, cancellationToken).ConfigureAwait(false);
        var binding = RequireBinding(project);
        var expectedBindingId = new WorkspaceBindingId(binding.Id);
        var expectedNodeId = new NodeId(binding.NodeId);
        var expectedRevision = binding.ValidationRevision;
        var expectedProjectVersion = project.Version;
        var request = new VerificationProfileSelectionRequestMessage(
            project.Id,
            expectedBindingId.Value,
            expectedRevision,
            NormalizeOptional(profileId),
            NormalizeOptional(profileRevision));
        var validation = await gateway.ValidateSelectionAsync(
            expectedNodeId.Value,
            request,
            cancellationToken).ConfigureAwait(false);
        if (!validation.Accepted)
        {
            throw new VerificationPolicySelectionException(
                string.IsNullOrWhiteSpace(validation.Detail)
                    ? $"The designated node rejected the verification policy selection ({validation.Code})."
                    : validation.Detail.Trim());
        }

        var persistedId = NormalizeOptional(validation.ProfileId) ?? request.ProfileId;
        var persistedRevision = NormalizeOptional(validation.ProfileRevision) ?? request.ProfileRevision;
        return await projects.SelectTrustedVerificationProfileAsync(
            projectId,
            expectedBindingId,
            expectedNodeId,
            expectedRevision,
            expectedProjectVersion,
            persistedId,
            persistedRevision,
            cancellationToken).ConfigureAwait(false);
    }

    private static WorkspaceBindingDto RequireBinding(ProjectDto project)
    {
        return project.Binding
            ?? throw new VerificationPolicySelectionException(
                "A designated workspace is required before a verification policy can be selected.");
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
