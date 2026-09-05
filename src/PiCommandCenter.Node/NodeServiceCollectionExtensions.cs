using Microsoft.Extensions.Configuration;
using PiCommandCenter.Application.Mail;
using PiCommandCenter.Application.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Node.RuntimeRouting;
using PiCommandCenter.Node.SubscriptionUsage;


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
            .AddOptions<NodeAuthenticationOptions>()
            .BindConfiguration(NodeAuthenticationOptions.SectionName)
            .Services
            .AddSingleton<NodeCredentialLoader>()
            .AddOptions<Verification.VerificationOptions>()
            .BindConfiguration(Verification.VerificationOptions.SectionName)
            .ValidateOnStart()
            .Services
            .AddSingleton<IValidateOptions<Verification.VerificationOptions>, Verification.VerificationOptionsValidator>()
            .AddSingleton<Verification.IVerificationCommandRunner, Verification.VerificationCommandRunner>()
            .AddSingleton<Repository.IRepositoryInspector, Repository.RepositoryInspector>()
            .AddSingleton<Repository.RequestWorkspaceTracker>()
            .AddSingleton<Application.Git.ITrustedGitService, Git.RestrictedGitService>()
            .AddSingleton<Child.INodeCompletionGateway, Child.NodeTransportCompletionGateway>()
            .AddSingleton<Repository.IRuntimeCrashRecovery, Repository.RuntimeCrashRecovery>()
            .AddOptions<PiWorkerOptions>()
            .BindConfiguration(PiWorkerOptions.SectionName)
            .ValidateOnStart()
            .Services
            .AddSingleton<IValidateOptions<PiWorkerOptions>, PiWorkerOptionsValidator>()
            .AddSingleton<IPostConfigureOptions<PiWorkerOptions>, PiWorkerOptionsPostConfigure>()
            .AddSingleton<NodeRuntimeRoutingStore>()
            .AddSingleton<INodeRuntimeRoutingStore>(
                static sp => sp.GetRequiredService<NodeRuntimeRoutingStore>())
            .AddSingleton<IRuntimeModelCommandRunner, RuntimeModelCommandRunner>()
            .AddSingleton<IRuntimeModelDiscovery, RuntimeModelDiscovery>()
            .AddSingleton<IRuntimeSubscriptionUsageCommandRunner, RuntimeSubscriptionUsageCommandRunner>()
            .AddOptions<SubscriptionUsageOptions>()
            .BindConfiguration(SubscriptionUsageOptions.SectionName)
            .Services
            .AddSingleton<IProviderSubscriptionQuotaReader, ProviderSubscriptionQuotaReader>()
            .AddSingleton<IRuntimeSubscriptionUsageProbe, RuntimeSubscriptionUsageProbe>()
            .AddSingleton<SqliteNodeEventSpool>()
            .AddSingleton<INodeEventSpool>(static sp => sp.GetRequiredService<SqliteNodeEventSpool>())
            .AddSingleton<NodeTransportClient>()
            .AddSingleton<INodeHubOps>(static sp => sp.GetRequiredService<NodeTransportClient>())
            .AddSingleton<IRootSessionSupervisor>(
                static sp => sp.GetRequiredService<PiRootSessionSupervisor>())
            .AddSingleton<ISessionCanceller>(static sp => new ChildSessionCanceller(
                sp.GetRequiredService<Child.PiChildSessionSupervisor>(),
                sp.GetRequiredService<IRootSessionSupervisor>()))
            .AddSingleton<Runtime.IPiWorkerProcessFactory, Runtime.NodeWorkerProcessFactory>()
            .AddSingleton<Runtime.PiOrchestrationRequestHandler>()
            .AddSingleton<Child.INodeReservationGateway, Child.NodeTransportReservationGateway>()
            .AddSingleton<Child.NodeTransportMailGateway>()
            .AddSingleton<Child.INodeMailGateway>(
                static sp => sp.GetRequiredService<Child.NodeTransportMailGateway>())
            .AddSingleton<IAgentIdentityRegistry>(
                static sp => sp.GetRequiredService<Child.NodeTransportMailGateway>())
            .AddSingleton(static sp => ActivatorUtilities.CreateInstance<Child.PiChildSessionSupervisor>(
                sp,
                sp.GetRequiredService<Runtime.PiOrchestrationRequestHandler>(),
                new Lazy<IAgentRuntimeRegistry>(sp.GetRequiredService<IAgentRuntimeRegistry>)))
            .AddSingleton<Runtime.IPiOrchestrationRequestHandler>(
                static sp => sp.GetRequiredService<Child.PiChildSessionSupervisor>())
            .AddSingleton<Runtime.PiRuntimeAdapter>()
            .AddSingleton<IAgentRuntimeAdapter>(static sp => sp.GetRequiredService<Runtime.PiRuntimeAdapter>())
            .AddOptions<ClaudeCodeOptions>()
            .BindConfiguration(ClaudeCodeOptions.SectionName)
            .ValidateOnStart()
            .Services
            .AddSingleton<IValidateOptions<ClaudeCodeOptions>, ClaudeCodeOptionsValidator>()
            .AddSingleton<Runtime.Claude.IOfficialAgentProcessFactory, Runtime.Claude.OfficialAgentProcessFactory>()
            .AddSingleton<Runtime.Claude.ClaudeCodeRuntimeAdapter>()
            .AddSingleton<IAgentRuntimeAdapter>(
                static sp => sp.GetRequiredService<Runtime.Claude.ClaudeCodeRuntimeAdapter>())
            .AddOptions<AntigravityOptions>()
            .BindConfiguration(AntigravityOptions.SectionName)
            .ValidateOnStart()
            .Services
            .AddSingleton<IValidateOptions<AntigravityOptions>, AntigravityOptionsValidator>()
            .AddSingleton<IPostConfigureOptions<AntigravityOptions>, AntigravityOptionsPostConfigure>()
            .AddSingleton<Runtime.Antigravity.IAntigravityProcessFactory, Runtime.Antigravity.AntigravityProcessFactory>()
            .AddSingleton<Runtime.Antigravity.AntigravityRuntimeAdapter>()
            .AddSingleton<IAgentRuntimeAdapter>(
                static sp => sp.GetRequiredService<Runtime.Antigravity.AntigravityRuntimeAdapter>())
            .AddSingleton<IAgentRuntimeRegistry>(static sp => new Runtime.AgentRuntimeRegistry(
                sp.GetRequiredService<Runtime.PiRuntimeAdapter>(),
                sp.GetRequiredService<Runtime.Claude.ClaudeCodeRuntimeAdapter>(),
                sp.GetRequiredService<Runtime.Antigravity.AntigravityRuntimeAdapter>()))
            .AddSingleton<PiRootSessionSupervisor>()
            .AddSingleton<NodeWorker>()
            .AddSingleton<Runtime.Claude.Hooks.ClaudeHookAuditLog>()
            .AddSingleton<Runtime.Claude.Hooks.ClaudeReservationHookEvaluator>()
            .AddSingleton<Runtime.Claude.Hooks.ClaudeReservationHookServer>()
            .AddSingleton<Runtime.Claude.Hooks.ClaudeHookSettingsInstaller>()
            .AddHostedService(static sp =>
                sp.GetRequiredService<Runtime.Claude.Hooks.ClaudeReservationHookServer>())
            .AddHostedService(static sp => sp.GetRequiredService<NodeWorker>());

        return services;
    }
}
