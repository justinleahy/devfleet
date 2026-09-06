using Microsoft.Extensions.Configuration;
using PiCommandCenter.Application.Mail;
using PiCommandCenter.Application.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Node.Projects;
using PiCommandCenter.Node.RuntimeRouting;
using PiCommandCenter.Node.SubscriptionUsage;
using PiCommandCenter.Node.SystemResources;


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
            .AddOptions<WorkspaceValidationOptions>()
            .BindConfiguration(WorkspaceValidationOptions.SectionName)
            .Services
            .AddSingleton<IWorkspaceBindingValidator, WorkspaceBindingValidator>()
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
            .AddSingleton<Quiescence.RequestAdmissionGate>()
            .AddSingleton<Quiescence.IRequestAdmissionGate>(
                static sp => sp.GetRequiredService<Quiescence.RequestAdmissionGate>())
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
            .AddSingleton<IRuntimeReadinessProbe, RuntimeReadinessProbe>()
            .AddSingleton<RuntimeReadinessProvider>()
            .AddSingleton<IRuntimeReadinessProvider>(
                static sp => sp.GetRequiredService<RuntimeReadinessProvider>())
            .AddHostedService(static sp => sp.GetRequiredService<RuntimeReadinessProvider>())
            .AddSingleton<IRuntimeModelCommandRunner, RuntimeModelCommandRunner>()
            .AddSingleton<IRuntimeModelDiscovery, RuntimeModelDiscovery>()
            .AddSingleton<IRuntimeSubscriptionUsageCommandRunner, RuntimeSubscriptionUsageCommandRunner>()
            .AddSingleton<IAntigravitySubscriptionUsageCommandRunner, AntigravitySubscriptionUsageCommandRunner>()
            .AddOptions<SubscriptionUsageOptions>()
            .BindConfiguration(SubscriptionUsageOptions.SectionName)
            .Services
            .AddSingleton<IPostConfigureOptions<SubscriptionUsageOptions>, SubscriptionUsageOptionsPostConfigure>()
            .AddSingleton<ISupplementalSubscriptionUsageSource, ClaudeSubscriptionUsageSource>()
            .AddSingleton<ISupplementalSubscriptionUsageSource, AntigravitySubscriptionUsageSource>()
            .AddSingleton<IRuntimeSubscriptionUsageProbe, RuntimeSubscriptionUsageProbe>()
            .AddSingleton<SubscriptionUsageCache>()
            .AddSingleton<ISubscriptionUsageCache>(
                static sp => sp.GetRequiredService<SubscriptionUsageCache>())
            .AddHostedService(static sp => sp.GetRequiredService<SubscriptionUsageCache>())
            .AddSingleton<NodeSystemResourceMonitor>()
            .AddSingleton<INodeSystemResourceMonitor>(
                static sp => sp.GetRequiredService<NodeSystemResourceMonitor>())
            .AddSingleton<SqliteNodeEventSpool>()
            .AddSingleton<INodeEventSpool>(static sp => sp.GetRequiredService<SqliteNodeEventSpool>())
            .AddSingleton<SqliteNodeAssignmentJournal>()
            .AddSingleton<INodeAssignmentJournal>(
                static sp => sp.GetRequiredService<SqliteNodeAssignmentJournal>())
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
                new Lazy<IAgentRuntimeRegistry>(sp.GetRequiredService<IAgentRuntimeRegistry>),
                new Lazy<IRootSessionSupervisor>(sp.GetRequiredService<IRootSessionSupervisor>),
                new Lazy<Child.INodeAssignmentTerminalizationOrchestrator>(
                    sp.GetRequiredService<Child.INodeAssignmentTerminalizationOrchestrator>)))
            .AddSingleton<Runtime.IPiOrchestrationRequestHandler>(
                static sp => sp.GetRequiredService<Child.PiChildSessionSupervisor>())
            .AddSingleton<Child.IRootSessionTerminalizer>(
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
            .AddOptions<MuseCodeOptions>()
            .BindConfiguration(MuseCodeOptions.SectionName)
            .ValidateOnStart()
            .Services
            .AddSingleton<IValidateOptions<MuseCodeOptions>, MuseCodeOptionsValidator>()
            .AddSingleton<Runtime.Muse.IMuseProcessFactory, Runtime.Muse.MuseProcessFactory>()
            .AddSingleton<Runtime.Muse.MuseCodeRuntimeAdapter>()
            .AddSingleton<IAgentRuntimeAdapter>(
                static sp => sp.GetRequiredService<Runtime.Muse.MuseCodeRuntimeAdapter>())
            .AddSingleton<Runtime.Muse.IMuseModelCatalogReader, Runtime.Muse.MuseModelCatalogReader>()
            .AddSingleton<IAgentRuntimeRegistry>(static sp => new Runtime.AgentRuntimeRegistry(
                sp.GetRequiredService<Runtime.PiRuntimeAdapter>(),
                sp.GetRequiredService<Runtime.Claude.ClaudeCodeRuntimeAdapter>(),
                sp.GetRequiredService<Runtime.Antigravity.AntigravityRuntimeAdapter>(),
                sp.GetRequiredService<Runtime.Muse.MuseCodeRuntimeAdapter>()))
            .AddSingleton<NodeAssignmentCredentialSource>()
            .AddSingleton<INodeAssignmentCredentialSource>(
                static sp => sp.GetRequiredService<NodeAssignmentCredentialSource>())
            .AddSingleton<PiRootSessionSupervisor>()
            .AddSingleton<NodeWorker>()
            .AddSingleton<Child.INodeAssignmentTerminalizationOrchestrator>(
                static sp => sp.GetRequiredService<NodeWorker>())
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
