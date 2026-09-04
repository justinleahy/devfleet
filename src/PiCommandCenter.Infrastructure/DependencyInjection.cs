using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Projects;
using PiCommandCenter.Infrastructure.Requests;

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

        return services;
    }
}
