using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.ControlPlane.Security;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Security;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

/// <summary>
/// Boots the real control plane against an isolated temporary SQLite database and an isolated
/// temporary approved root, so tests never touch the user's projects and stay parallel-safe.
/// </summary>
public sealed class ControlPlaneFixture : IDisposable
{
    private readonly string _tempRoot;

    public ControlPlaneFixture()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "pi-cc-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        ApprovedRoot = Path.Combine(_tempRoot, "approved");
        Directory.CreateDirectory(ApprovedRoot);
        SqlitePath = Path.Combine(_tempRoot, "controlplane.db");
        using (File.Create(SqlitePath))
        {
        }

        (PasswordFile, CredentialFile) = AuthTestMaterial.WriteTo(Path.Combine(_tempRoot, "auth"));

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ControlPlane", $"Data Source={SqlitePath}");
            builder.UseSetting("Projects:ApprovedRoots:0", ApprovedRoot);
            builder.UseTestAuthFiles(PasswordFile, CredentialFile);
        });

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        db.Database.Migrate();
    }

    public WebApplicationFactory<Program> Factory { get; }

    public string ApprovedRoot { get; }

    public string SqlitePath { get; }

    public string PasswordFile { get; }

    public string CredentialFile { get; }

    public string NodeTokenHex => AuthTestMaterial.NodeTokenHex;

    public HttpClient CreateClient() => CreateAuthenticatedClient();

    public HttpClient CreateAnonymousClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    public HttpClient CreateAuthenticatedClient() => CreateAuthenticatedClient(Factory);

    public HttpClient CreateAuthenticatedClient(WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        AttachAntiforgery(client, factory);
        Login(client);
        return client;
    }

    public void AttachAntiforgery(HttpClient client) => AttachAntiforgery(client, Factory);

    public void AttachAntiforgery(HttpClient client, WebApplicationFactory<Program> factory)
    {
        var (cookie, token) = IssueAntiforgery(factory);
        if (!string.IsNullOrEmpty(cookie))
        {
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", cookie);
        }

        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", token);
    }

    public void ConfigureNodeHub(HttpConnectionOptions options)
    {
        options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
        options.Transports = HttpTransportType.LongPolling;
        options.AccessTokenProvider = () => Task.FromResult<string?>(NodeTokenHex);
    }

    public HubConnection CreateNodeHubConnection()
    {
        _ = Factory.CreateClient();
        return new HubConnectionBuilder()
            .WithUrl(new Uri(Factory.Server.BaseAddress, "nodeHub"), ConfigureNodeHub)
            .Build();
    }

    /// <summary>Initializes a fresh real Git repository inside the fixture's approved root.</summary>
    public string CreateGitRepository()
    {
        var path = Path.Combine(ApprovedRoot, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        RunGit(["init", "-q", "-b", "main", path]);
        RunGit(["-C", path, "config", "user.email", "tests@example.invalid"]);
        RunGit(["-C", path, "config", "user.name", "Command Center Tests"]);
        return path;
    }

    public (string CookieHeader, string RequestToken) IssueAntiforgery() => IssueAntiforgery(Factory);

    public (string CookieHeader, string RequestToken) IssueAntiforgery(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var antiforgery = scope.ServiceProvider.GetRequiredService<IAntiforgery>();
        var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new HostString("localhost");
        var tokens = antiforgery.GetAndStoreTokens(httpContext);
        var setCookie = httpContext.Response.Headers.SetCookie.ToString();
        return (setCookie, tokens.RequestToken ?? string.Empty);
    }

    private void Login(HttpClient client)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = AuthTestMaterial.Username,
            ["password"] = AuthTestMaterial.Password,
            ["returnUrl"] = "/",
        });
        using var response = client.PostAsync("/account/login", content).GetAwaiter().GetResult();
        if (response.StatusCode is not HttpStatusCode.Redirect and not HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"Test login failed with {(int)response.StatusCode} {response.Headers.Location}");
        }
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
