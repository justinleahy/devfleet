using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Recovery;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Application.Statistics;
using PiCommandCenter.Application.Transport;
using PiCommandCenter.Infrastructure.Nodes;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Application.Sessions;
using PiCommandCenter.Infrastructure.Projects;
using PiCommandCenter.Infrastructure.Recovery;
using PiCommandCenter.Infrastructure.Requests;
using PiCommandCenter.Infrastructure.Sessions;
using PiCommandCenter.Infrastructure.Statistics;
using PiCommandCenter.Infrastructure.Transport;
using PiCommandCenter.Infrastructure.Verification;

namespace PiCommandCenter.Infrastructure;

/// <summary>
/// Registers the control plane persistence stack: SQLite context with WAL initialization,
/// the project catalog and request queue adapters, and the system time provider.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IProjectionNotifier, ProjectionNotifier>();

        services.AddDbContext<ControlPlaneDbContext>((_, optionsBuilder) =>
        {
            var connectionString = configuration.GetConnectionString("ControlPlane")
                ?? "Data Source=controlplane.db";
            optionsBuilder.UseSqlite(connectionString);
            optionsBuilder.AddInterceptors(new SqliteConnectionInitializerInterceptor());
        });

        services.AddScoped<IProjectCatalog, ProjectCatalog>();
        services.AddScoped<IWorkspaceBindingCatalog, WorkspaceBindingCatalog>();
        services.AddScoped<IRequestQueue, RequestQueue>();
        services.AddScoped<IRequestEligibilityEvaluator, RequestEligibilityEvaluator>();
        services.AddScoped<VerificationPolicyUpgradeMigrator>();
        services.AddScoped<INodeRegistry, NodeRegistry>();
        services.AddScoped<IExecutionAssignmentService, ExecutionAssignmentService>();
        services.AddScoped<IRequestCancellationService, RequestCancellationService>();
        services.AddScoped<IProjectRecoveryService, ProjectRecoveryService>();
        services.AddScoped<IManualProjectRecoveryService, ManualRecoveryService>();

        services.AddScoped<IRecoveryAttemptCoordinator, RecoveryAttemptCoordinator>();
        services.AddScoped<IRecoveryAttemptDispatcher, RecoveryAttemptDispatcher>();

        services.AddScoped<IRecoveryTargetTerminalizer, RecoveryTargetTerminalizer>();
        services.AddScoped<IAssignmentOperationAuthorizer, AssignmentOperationAuthorizer>();
        services.AddScoped<INodeEventSink, NodeEventSink>();
        services.AddScoped<IAgentSessionStore, AgentSessionStore>();
        services.AddScoped<IFleetStatisticsService, FleetStatisticsService>();
        services.AddReservations();
        services.AddVerificationAndCompletion();
        return services;
    }
}
