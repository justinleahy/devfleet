using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Application.Runtime;

/// <summary>
/// Admin-facing gateway to node-owned provider subscription usage
/// (provider subscription limits, not session or context usage).
/// </summary>
public interface INodeSubscriptionUsageGateway
{
    Task<NodeSubscriptionUsageMessage> GetAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default);
}
