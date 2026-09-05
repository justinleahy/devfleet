using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Application.Runtime;

/// <summary>Admin-facing gateway to live node-owned runtime routing configuration.</summary>
public interface INodeRuntimeConfigurationGateway
{
    Task<NodeRuntimeConfigurationMessage> GetAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RuntimeModelCatalogMessage>> DiscoverModelsAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default);

    Task<NodeRuntimeConfigurationMessage> UpdateAsync(
        Guid nodeId,
        UpdateNodeRuntimeConfigurationMessage update,
        CancellationToken cancellationToken = default);
}
