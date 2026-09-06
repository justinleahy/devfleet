using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Identity;
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
    public const string NativeApiLoginPath = "/api/v1/auth/login";
    public const string NativeApiRefreshPath = "/api/v1/auth/refresh";

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

        PasswordFile = Path.Combine(_tempRoot, "auth", "admin.password.hash");
        PrivateFileAccess.WritePrivateFile(PasswordFile, AuthMaterialLoader.HashPassword(AuthTestMaterial.Password));

        CredentialDirectory = Path.Combine(_tempRoot, "node-credentials");
        PrivateFileAccess.CreatePrivateDirectory(CredentialDirectory);
        NodeCredentialFile = WriteNodeCredential(AuthenticatedNodeId, NodeTokenHex);
        SecondaryNodeCredentialFile = WriteNodeCredential(SecondaryNodeId, SecondaryNodeTokenHex);

        DataProtectionKeysDirectory = Path.Combine(_tempRoot, "data-protection-keys");
        PrivateFileAccess.CreatePrivateDirectory(DataProtectionKeysDirectory);

        Factory = CreateIndependentHost();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        db.Database.Migrate();
    }

    public WebApplicationFactory<Program> Factory { get; }

    public string ApprovedRoot { get; }

    public string SqlitePath { get; }

    public string PasswordFile { get; }

    public string CredentialDirectory { get; }

    public string NodeCredentialFile { get; }

    public string SecondaryNodeCredentialFile { get; }

    public string DataProtectionKeysDirectory { get; }

    public Guid AuthenticatedNodeId { get; } = Guid.Parse("16a9c414-dc4b-482a-91be-4f3b947894ff");

    public Guid SecondaryNodeId { get; } = Guid.Parse("e4582f39-10ed-4de3-b036-0415d70a269a");

    public string NodeTokenHex => AuthTestMaterial.NodeTokenHex;

    public string SecondaryNodeTokenHex { get; } = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData("pi-command-center-test-secondary-node"u8));

    public HttpClient CreateClient() => CreateAuthenticatedClient();

    public WebApplicationFactory<Program> CreateIndependentHost()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ControlPlane", $"Data Source={SqlitePath}");
            builder.UseSetting("Projects:ApprovedRoots:0", ApprovedRoot);
            builder.UseTestAuthFiles(
                PasswordFile,
                CredentialDirectory,
                dataProtectionKeysDirectory: DataProtectionKeysDirectory);
        });
    }

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
        AttachAntiforgery(client, factory, asAdmin: true);
        Login(client);
        return client;
    }

    public void AttachAntiforgery(HttpClient client, bool asAdmin = false) => AttachAntiforgery(client, Factory, asAdmin);

    public void AttachAntiforgery(HttpClient client, WebApplicationFactory<Program> factory, bool asAdmin = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        var (cookie, token) = IssueAntiforgery(factory, asAdmin);
        if (!string.IsNullOrEmpty(cookie))
        {
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", cookie);
        }

        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", token);
    }

    /// <summary>
    /// Creates a client that never stores cookies or follows redirects, so every request exercises
    /// the native <c>/api/v1</c> bearer contract exactly as an external client would.
    /// </summary>
    public HttpClient CreateNativeClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = false,
    });

    /// <summary>Creates a cookie-free client whose default headers carry a freshly issued bearer token.</summary>
    public async Task<HttpClient> CreateNativeAuthenticatedClientAsync()
    {
        var client = CreateNativeClient();
        var tokens = await NativeLoginAsync(client);
        UseBearer(client, tokens);
        return client;
    }

    /// <summary>Posts JSON credentials to <c>/api/v1/auth/login</c> and returns the opaque token pair.</summary>
    public static async Task<NativeTokenResponse> NativeLoginAsync(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var response = await client.PostAsJsonAsync(
            NativeApiLoginPath,
            new { username = AuthTestMaterial.Username, password = AuthTestMaterial.Password });
        return await ReadNativeTokensAsync(response);
    }

    /// <summary>Posts a refresh token to <c>/api/v1/auth/refresh</c> and returns the reissued token pair.</summary>
    public static async Task<NativeTokenResponse> NativeRefreshAsync(HttpClient client, string refreshToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var response = await client.PostAsJsonAsync(NativeApiRefreshPath, new { refreshToken });
        return await ReadNativeTokensAsync(response);
    }

    public static void UseBearer(HttpClient client, NativeTokenResponse tokens)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(tokens);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(tokens.TokenType, tokens.AccessToken);
    }

    private static async Task<NativeTokenResponse> ReadNativeTokensAsync(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"Native token request failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        return await response.Content.ReadFromJsonAsync<NativeTokenResponse>()
            ?? throw new InvalidOperationException("Native token response body was empty.");
    }

    public void ConfigureNodeHub(HttpConnectionOptions options) =>
        ConfigureNodeHub(options, AuthenticatedNodeId);

    public void ConfigureNodeHub(HttpConnectionOptions options, Guid nodeId)
    {
        options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
        options.Transports = HttpTransportType.LongPolling;
        var token = GetNodeToken(nodeId);
        options.AccessTokenProvider = () => Task.FromResult<string?>(token);
    }

    public HubConnection CreateNodeHubConnection() => CreateNodeHubConnection(AuthenticatedNodeId);

    public HubConnection CreateNodeHubConnection(Guid nodeId)
    {
        _ = Factory.CreateClient();
        return new HubConnectionBuilder()
            .WithUrl(
                new Uri(Factory.Server.BaseAddress, "nodeHub"),
                options => ConfigureNodeHub(options, nodeId))
            .Build();
    }

    private string WriteNodeCredential(Guid nodeId, string token)
    {
        var path = Path.Combine(CredentialDirectory, $"{nodeId:D}.token");
        PrivateFileAccess.WritePrivateFile(path, token);
        return path;
    }

    private string GetNodeToken(Guid nodeId)
    {
        if (nodeId == AuthenticatedNodeId)
        {
            return NodeTokenHex;
        }

        if (nodeId == SecondaryNodeId)
        {
            return SecondaryNodeTokenHex;
        }

        throw new ArgumentOutOfRangeException(nameof(nodeId), nodeId, "No test credential is provisioned for this node.");
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

    public (string CookieHeader, string RequestToken) IssueAntiforgery(bool asAdmin = false) => IssueAntiforgery(Factory, asAdmin);

    /// <summary>
    /// Issues an antiforgery cookie/token pair from <paramref name="factory"/>. Antiforgery binds the
    /// request token to the caller's claim UID, so tokens for a cookie-authenticated client must be
    /// generated as the synchronized admin (<paramref name="asAdmin"/>) and anonymous ones as nobody.
    /// </summary>
    public (string CookieHeader, string RequestToken) IssueAntiforgery(WebApplicationFactory<Program> factory, bool asAdmin = false)
    {
        ArgumentNullException.ThrowIfNull(factory);
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
        var setCookie = httpContext.Response.Headers.SetCookie.ToString();
        return (setCookie, tokens.RequestToken ?? string.Empty);
    }

    /// <summary>
    /// Builds the same Identity principal the cookie login stores, from the admin account the host
    /// synchronized at startup, so its claim UID matches the one antiforgery reads off the auth cookie.
    /// </summary>
    private static async Task<ClaimsPrincipal> CreateAdminPrincipalAsync(IServiceProvider services)
    {
        var userManager = services.GetRequiredService<UserManager<IdentityUser>>();
        var admin = await userManager.FindByNameAsync(AuthTestMaterial.Username)
            ?? throw new InvalidOperationException(
                $"Admin account '{AuthTestMaterial.Username}' was not synchronized into the test host.");
        return await services.GetRequiredService<SignInManager<IdentityUser>>().CreateUserPrincipalAsync(admin);
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

/// <summary>Opaque bearer token pair returned by the native <c>/api/v1/auth</c> endpoints.</summary>
public sealed record NativeTokenResponse(string TokenType, string AccessToken, long ExpiresIn, string RefreshToken);
