using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.FluentUI.AspNetCore.Components;
using PiCommandCenter.Application;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Api;
using PiCommandCenter.ControlPlane.Api;
using PiCommandCenter.ControlPlane.Security;
using PiCommandCenter.ControlPlane.RuntimeRouting;
using PiCommandCenter.ControlPlane.Projects;
using PiCommandCenter.ControlPlane.SubscriptionUsage;
using PiCommandCenter.Infrastructure;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Security;
using PiCommandCenter.Web.Components;
using PiCommandCenter.ControlPlane.Hubs;
using PiCommandCenter.ControlPlane.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();

if (ControlPlaneAuthSetup.IsSetupRequested(args))
{
    var force = args.Any(argument => string.Equals(argument, "--force", StringComparison.OrdinalIgnoreCase));
    var readPassword = args.Any(argument =>
        string.Equals(argument, "--password-stdin", StringComparison.OrdinalIgnoreCase));
    try
    {
        var suppliedPassword = readPassword
            ? Console.In.ReadLine()
                ?? throw new InvalidOperationException("No administrator password was received on stdin.")
            : null;
        var result = ControlPlaneAuthSetup.Run(builder.Configuration, force, suppliedPassword);
        Console.WriteLine($"Username: {result.Username}");
        if (!readPassword)
        {
            Console.WriteLine($"Password: {result.OneTimePassword}");
            Console.WriteLine("Store the administrator password now; it is not kept in plaintext.");
        }
        else
        {
            Console.WriteLine("Administrator password read from stdin.");
        }

        Console.WriteLine($"Password file: {result.PasswordFile}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.ExitCode = 1;
    }

    return;
}

if (!builder.Environment.IsEnvironment("Testing")
    && string.IsNullOrWhiteSpace(builder.Configuration["Kestrel:Endpoints:Http:Url"])
    && string.IsNullOrWhiteSpace(builder.Configuration["urls"])
    && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls(builder.Configuration["ControlPlane:BaseUrl"] ?? "http://127.0.0.1:5000");
}

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

// Node fleet transport: SignalR hub at /nodeHub (server-only; never navigated from
// the browser) plus the background sweeper that flips silent nodes offline after
// three missed heartbeats.
builder.Services.AddSignalR(options => options.EnableDetailedErrors = builder.Environment.IsDevelopment());
builder.Services.Configure<NodeLivenessOptions>(
    builder.Configuration.GetSection(NodeLivenessOptions.SectionName));
builder.Services.AddHostedService<NodeLivenessService>();
builder.Services.AddSingleton<NodeConnectionDirectory>();
builder.Services.AddSingleton<IWorkspaceValidationGateway, NodeWorkspaceValidationGateway>();
builder.Services.AddSingleton<INodeRuntimeConfigurationGateway, NodeRuntimeConfigurationGateway>();
builder.Services.AddSingleton<INodeSubscriptionUsageGateway, NodeSubscriptionUsageGateway>();
builder.Services.AddSingleton<INativeApiRealtimeGateway, NativeApiRealtimeGateway>();

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddHealthChecks();

// Registers ControlPlaneDbContext (SQLite), IProjectCatalog, IRequestQueue, and the
// SQLite pragmas (WAL, foreign keys). Approved project roots are read by the
// infrastructure layer from "Projects:ApprovedRoots" — nothing is approved unless it
// is explicitly listed there; "~" entries resolve to the current user's home directory.
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddControlPlaneAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddAgentMail();
builder.Services.AddPiCommandCenterApi();

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
    await scope.ServiceProvider.GetRequiredService<AdminAccountSynchronizer>()
        .SynchronizeAsync(app.Lifetime.ApplicationStopping);
}
catch (Exception ex)
{
    logger.LogError(ex, "Control-plane database startup failed");
    throw;
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHealthChecks("/health")
    .AllowAnonymous()
    .AddEndpointFilter(async (context, next) =>
    {
        var ip = context.HttpContext.Connection.RemoteIpAddress;
        if (ip is not null && !IPAddress.IsLoopback(ip))
        {
            return Results.Unauthorized();
        }

        return await next(context).ConfigureAwait(false);
    });

app.MapAccountEndpoints();
app.MapPiCommandCenterApi();
app.MapHub<NodeHub>("/nodeHub").RequireAuthorization(AuthPolicies.Node);

app.Run();

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
