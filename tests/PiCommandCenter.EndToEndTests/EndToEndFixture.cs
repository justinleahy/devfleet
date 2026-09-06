using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.ControlPlane.Security;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Security;

namespace PiCommandCenter.EndToEndTests;

/// <summary>
/// Boots the full control plane against an isolated temporary SQLite database and an isolated
/// temporary approved root so end-to-end journeys never touch the user's repositories.
/// </summary>
public sealed class EndToEndFixture : IDisposable
{
    private readonly string _tempRoot;

    public EndToEndFixture()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "pi-cc-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        ApprovedRoot = Path.Combine(_tempRoot, "approved");
        Directory.CreateDirectory(ApprovedRoot);
        SqlitePath = Path.Combine(_tempRoot, "controlplane.db");
        using (File.Create(SqlitePath))
        {
        }

        var auth = AuthTestMaterial.WriteTo(Path.Combine(_tempRoot, "auth"));
        PasswordFile = auth.PasswordFile;
        CredentialDirectory = auth.CredentialDirectory;
        AuthenticatedNodeId = auth.AuthenticatedNodeId;
        NodeTokenHex = auth.NodeTokenHex;

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ControlPlane", $"Data Source={SqlitePath}");
            builder.UseSetting("Projects:ApprovedRoots:0", ApprovedRoot);
            builder.UseTestAuthFiles(PasswordFile, CredentialDirectory);
        });

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        db.Database.Migrate();
    }

    public WebApplicationFactory<Program> Factory { get; }

    public string ApprovedRoot { get; }

    public string SqlitePath { get; }

    public string PasswordFile { get; }

    public string CredentialDirectory { get; }

    public Guid AuthenticatedNodeId { get; }

    public string NodeTokenHex { get; }

    public HttpClient CreateClient() => CreateAuthenticatedClient(Factory);

    public HttpClient CreateAuthenticatedClient(WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = true,
            HandleCookies = true,
        });
        var (cookie, token) = IssueAntiforgery(factory, asAdmin: true);
        if (!string.IsNullOrEmpty(cookie))
        {
            client.DefaultRequestHeaders.Add("Cookie", cookie);
        }

        client.DefaultRequestHeaders.Add("RequestVerificationToken", token);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = AuthTestMaterial.Username,
            ["password"] = AuthTestMaterial.Password,
            ["returnUrl"] = "/",
            ["__RequestVerificationToken"] = token,
        });
        using var response = client.PostAsync("/account/login", content).GetAwaiter().GetResult();
        if (response.StatusCode is not HttpStatusCode.Redirect and not HttpStatusCode.OK && !response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Test login failed with {(int)response.StatusCode}");
        }

        return client;
    }

    public (string CookieHeader, string RequestToken) IssueAntiforgery(bool asAdmin = false) =>
        IssueAntiforgery(Factory, asAdmin);

    public (string CookieHeader, string RequestToken) IssueAntiforgery(
        WebApplicationFactory<Program> factory,
        bool asAdmin = false)
    {
        using var scope = factory.Services.CreateScope();
        var antiforgery = scope.ServiceProvider.GetRequiredService<IAntiforgery>();
        var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new HostString("localhost");
        if (asAdmin)
        {
            httpContext.User = CreateAdminPrincipalAsync(scope.ServiceProvider).GetAwaiter().GetResult();
        }
        var tokens = antiforgery.GetAndStoreTokens(httpContext);
        return (httpContext.Response.Headers.SetCookie.ToString(), tokens.RequestToken ?? string.Empty);
    }

    private static async Task<ClaimsPrincipal> CreateAdminPrincipalAsync(IServiceProvider services)
    {
        var userManager = services.GetRequiredService<UserManager<IdentityUser>>();
        var admin = await userManager.FindByNameAsync(AuthTestMaterial.Username)
            ?? throw new InvalidOperationException(
                $"Admin account '{AuthTestMaterial.Username}' was not synchronized into the test host.");
        return await services.GetRequiredService<SignInManager<IdentityUser>>()
            .CreateUserPrincipalAsync(admin);
    }

    public string CreateGitRepository()
    {
        var path = Path.Combine(ApprovedRoot, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        RunGit(["init", "-q", "-b", "main", path]);
        RunGit(["-C", path, "config", "user.email", "tests@example.invalid"]);
        RunGit(["-C", path, "config", "user.name", "Command Center Tests"]);
        return path;
    }

    private static void RunGit(IReadOnlyList<string> argumentList)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in argumentList)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        process.WaitForExit(30_000);
        if (process.ExitCode != 0)
        {
            Assert.Fail($"git {string.Join(' ', argumentList)} failed with exit code {process.ExitCode}.");
        }
    }

    public void Dispose()
    {
        Factory.Dispose();
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
