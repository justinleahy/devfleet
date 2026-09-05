using Microsoft.Extensions.Configuration;
using PiCommandCenter.Application.Runtime;
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
            .AddOptions<PiWorkerOptions>()
            .BindConfiguration(PiWorkerOptions.SectionName)
            .ValidateOnStart()
            .Services
            .AddSingleton<IValidateOptions<PiWorkerOptions>, PiWorkerOptionsValidator>()
            .AddSingleton<IPostConfigureOptions<PiWorkerOptions>, PiWorkerOptionsPostConfigure>()
            .AddSingleton<SqliteNodeEventSpool>()
            .AddSingleton<INodeEventSpool>(static sp => sp.GetRequiredService<SqliteNodeEventSpool>())
            .AddSingleton<NodeTransportClient>()
            .AddSingleton<Runtime.IPiWorkerProcessFactory, Runtime.NodeWorkerProcessFactory>()
            .AddSingleton<Runtime.PiOrchestrationRequestHandler>()
            .AddSingleton<Child.INodeReservationGateway, Child.NodeTransportReservationGateway>()
            .AddSingleton<Child.INodeMailGateway, Child.NodeTransportMailGateway>()
            .AddSingleton(static sp => ActivatorUtilities.CreateInstance<Child.PiChildSessionSupervisor>(
                sp,
                sp.GetRequiredService<Runtime.PiOrchestrationRequestHandler>()))
            .AddSingleton<Runtime.IPiOrchestrationRequestHandler>(
                static sp => sp.GetRequiredService<Child.PiChildSessionSupervisor>())
            .AddSingleton<Runtime.PiRuntimeAdapter>()
            .AddSingleton<IAgentRuntimeAdapter>(static sp => sp.GetRequiredService<Runtime.PiRuntimeAdapter>())
            .AddSingleton<PiRootSessionSupervisor>()
            .AddSingleton<NodeWorker>()
            .AddHostedService(static sp => sp.GetRequiredService<NodeWorker>());

        return services;
    }
}
