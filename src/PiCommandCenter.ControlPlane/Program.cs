using Microsoft.EntityFrameworkCore;
using Microsoft.FluentUI.AspNetCore.Components;
using PiCommandCenter.Application;
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
builder.Services.AddDbContext<ControlPlaneDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("ControlPlane")
        ?? "Data Source=controlplane.db"));

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHealthChecks("/health");

app.Run();

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
