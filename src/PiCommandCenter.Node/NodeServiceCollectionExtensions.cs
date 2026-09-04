using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace PiCommandCenter.Node;

/// <summary>
/// Registers the node worker, transport, and event spool with a host service collection.
/// </summary>
public static class NodeServiceCollectionExtensions
{
    public static IServiceCollection AddPiNode(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLogging();

        services.TryAddSingleton<IConfiguration>(static _ => new ConfigurationManager());
        services.TryAddSingleton(TimeProvider.System);

        services
            .AddOptions<NodeOptions>()
            .BindConfiguration(NodeOptions.SectionName)
            .ValidateOnStart()
            .Services
            .AddSingleton<IValidateOptions<NodeOptions>, NodeOptionsValidator>()
            .AddSingleton<IPostConfigureOptions<NodeOptions>, NodeOptionsPostConfigure>()
            .AddSingleton<SqliteNodeEventSpool>()
            .AddSingleton<INodeEventSpool>(static sp => sp.GetRequiredService<SqliteNodeEventSpool>())
            .AddSingleton<NodeTransportClient>()
            .AddSingleton<NodeWorker>()
            .AddHostedService(static sp => sp.GetRequiredService<NodeWorker>());

        return services;
    }
}
