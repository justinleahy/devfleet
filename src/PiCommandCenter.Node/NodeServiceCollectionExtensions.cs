using Microsoft.Extensions.DependencyInjection;

namespace PiCommandCenter.Node;

/// <summary>
/// Registers the node worker with a host service collection.
/// </summary>
public static class NodeServiceCollectionExtensions
{
    public static IServiceCollection AddPiNode(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<NodeWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<NodeWorker>());
        return services;
    }
}
