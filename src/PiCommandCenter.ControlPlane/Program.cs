using Microsoft.EntityFrameworkCore;
using Microsoft.FluentUI.AspNetCore.Components;
using PiCommandCenter.Application;
using PiCommandCenter.ControlPlane.Api;
using PiCommandCenter.Infrastructure;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Web.Components;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("PiCommandCenter.ControlPlane", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["ControlPlane:BaseUrl"] ?? "http://127.0.0.1",
        UriKind.Absolute);
});
builder.Services.AddFluentUIComponents();

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddHealthChecks();

// Registers ControlPlaneDbContext (SQLite), IProjectCatalog, IRequestQueue, and the
// SQLite pragmas (WAL, foreign keys). Approved project roots are read by the
// infrastructure layer from "Projects:ApprovedRoots" — nothing is approved unless it
// is explicitly listed there; "~" entries resolve to the current user's home directory.
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

// Controlled startup: apply migrations once and log each phase so operators can see
// exactly what the control plane did to the database before serving traffic.
var logger = app.Services.GetRequiredService<ILogger<ControlPlaneDbContext>>();
try
{
    logger.LogInformation("Applying control-plane database migrations");
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
    await dbContext.Database.MigrateAsync(app.Lifetime.ApplicationStopping);
    logger.LogInformation("Control-plane database migrations applied");
}
catch (Exception ex)
{
    logger.LogError(ex, "Control-plane database startup failed");
    throw;
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHealthChecks("/health");
app.MapProjectsEndpoints();
app.MapRequestsEndpoints();

app.Run();

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
