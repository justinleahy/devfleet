using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Application.Transport;
using PiCommandCenter.Infrastructure.Nodes;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Application.Sessions;
using PiCommandCenter.Infrastructure.Projects;
using PiCommandCenter.Infrastructure.Requests;
using PiCommandCenter.Infrastructure.Sessions;
using PiCommandCenter.Infrastructure.Transport;

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

        services.Configure<ProjectCatalogOptions>(
            configuration.GetSection(ProjectCatalogOptions.SectionName));

        services.AddScoped<IProjectCatalog, ProjectCatalog>();
        services.AddScoped<IRequestQueue, RequestQueue>();
        services.AddScoped<INodeRegistry, NodeRegistry>();
        services.AddScoped<IRequestClaimService, RequestClaimService>();
        services.AddScoped<INodeEventSink, NodeEventSink>();
        services.AddScoped<IAgentSessionStore, AgentSessionStore>();
        services.AddReservations();
        services.AddVerificationAndCompletion();
        return services;
    }
}
