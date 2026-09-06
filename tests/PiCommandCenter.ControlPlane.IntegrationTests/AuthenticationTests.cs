using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using PiCommandCenter.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc.Testing;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

public sealed class AuthenticationTests : IClassFixture<ControlPlaneFixture>
{
    private readonly ControlPlaneFixture _fixture;

    public AuthenticationTests(ControlPlaneFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Anonymous_api_and_dashboard_are_rejected()
    {
        using var client = _fixture.CreateAnonymousClient();

        var dashboard = await client.GetAsync("/");
        var api = await client.GetAsync("/api/projects");

        Assert.False(dashboard.IsSuccessStatusCode);
        Assert.False(api.IsSuccessStatusCode);
        Assert.True(
            dashboard.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized,
            dashboard.StatusCode.ToString());
        Assert.True(
            api.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized,
            api.StatusCode.ToString());
        if (dashboard.StatusCode == HttpStatusCode.Redirect)
        {
            Assert.Contains("/login", dashboard.Headers.Location?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Health_is_anonymous_on_loopback()
    {
        using var client = _fixture.CreateAnonymousClient();
        var response = await client.GetAsync("/health");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", (await response.Content.ReadAsStringAsync()).Trim());
    }

    [Fact]
    public async Task Login_cookie_allows_api_and_wrong_password_does_not_echo_secret()
    {
        using var client = _fixture.CreateAnonymousClient();
        _fixture.AttachAntiforgery(client);

        using var bad = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = AuthTestMaterial.Username,
            ["password"] = "not-the-password",
            ["returnUrl"] = "/",
        });
        var failed = await client.PostAsync("/account/login", bad);
        Assert.Equal(HttpStatusCode.Redirect, failed.StatusCode);
        var location = failed.Headers.Location?.ToString() ?? string.Empty;
        Assert.Contains("error=invalid", location, StringComparison.Ordinal);
        Assert.DoesNotContain("not-the-password", location, StringComparison.Ordinal);
        Assert.DoesNotContain(AuthTestMaterial.Password, location, StringComparison.Ordinal);

        using var good = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = AuthTestMaterial.Username,
            ["password"] = AuthTestMaterial.Password,
            ["returnUrl"] = "/",
        });
        var succeeded = await client.PostAsync("/account/login", good);
        Assert.Equal(HttpStatusCode.Redirect, succeeded.StatusCode);

        var api = await client.GetAsync("/api/projects");
        Assert.True(api.IsSuccessStatusCode, await api.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Auth_cookie_from_disposed_host_authorizes_independent_host_sharing_keys()
    {
        string? authCookie;
        using (var hostA = _fixture.CreateIndependentHost())
        {
            using var clientA = hostA.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
            _fixture.AttachAntiforgery(clientA, hostA);
            using var login = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = AuthTestMaterial.Username,
                ["password"] = AuthTestMaterial.Password,
                ["returnUrl"] = "/",
            });
            var succeeded = await clientA.PostAsync("/account/login", login);
            Assert.Equal(HttpStatusCode.Redirect, succeeded.StatusCode);
            Assert.True(succeeded.Headers.TryGetValues("Set-Cookie", out var setCookies));
            authCookie = setCookies.FirstOrDefault(static cookie =>
                cookie.StartsWith("pcc.admin=", StringComparison.Ordinal));
            Assert.False(string.IsNullOrEmpty(authCookie));
            authCookie = authCookie.Split(';', 2)[0];
        }

        using var hostB = _fixture.CreateIndependentHost();
        using var clientB = hostB.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        clientB.DefaultRequestHeaders.Add("Cookie", authCookie);
        var api = await clientB.GetAsync("/api/projects");
        Assert.True(api.IsSuccessStatusCode, await api.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task State_changing_post_without_antiforgery_is_rejected()
    {
        using var client = _fixture.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");

        var response = await client.PostAsJsonAsync(
            "/api/projects",
            new { displayName = "no-csrf", defaultBranch = "main" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_node_token_is_unauthorized_and_each_per_node_token_connects()
    {
        _ = _fixture.Factory.CreateClient();
        await using var wrong = new HubConnectionBuilder()
            .WithUrl(new Uri(_fixture.Factory.Server.BaseAddress, "nodeHub"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _fixture.Factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(new string('0', 64));
            })
            .Build();

        var wrongEx = await Assert.ThrowsAnyAsync<Exception>(() => wrong.StartAsync());
        Assert.Contains("401", wrongEx.Message + wrongEx.InnerException?.Message, StringComparison.Ordinal);

        await using var primary = _fixture.CreateNodeHubConnection();
        await primary.StartAsync();
        Assert.Equal(HubConnectionState.Connected, primary.State);

        await using var secondary = _fixture.CreateNodeHubConnection(_fixture.SecondaryNodeId);
        await secondary.StartAsync();
        Assert.Equal(HubConnectionState.Connected, secondary.State);
    }

    [Fact]
    public void Auth_files_and_credential_directory_are_owner_only()
    {
        Assert.Equal($"{_fixture.AuthenticatedNodeId:D}.token", Path.GetFileName(_fixture.NodeCredentialFile));
        Assert.Equal($"{_fixture.SecondaryNodeId:D}.token", Path.GetFileName(_fixture.SecondaryNodeCredentialFile));
        Assert.Equal(64, new FileInfo(_fixture.NodeCredentialFile).Length);
        Assert.Equal(64, new FileInfo(_fixture.SecondaryNodeCredentialFile).Length);

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var passwordMode = File.GetUnixFileMode(_fixture.PasswordFile);
        var credentialDirectoryMode = File.GetUnixFileMode(_fixture.CredentialDirectory);
        var nodeTokenMode = File.GetUnixFileMode(_fixture.NodeCredentialFile);
        var secondaryNodeTokenMode = File.GetUnixFileMode(_fixture.SecondaryNodeCredentialFile);
        var keysMode = File.GetUnixFileMode(_fixture.DataProtectionKeysDirectory);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, passwordMode);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            credentialDirectoryMode);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, nodeTokenMode);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, secondaryNodeTokenMode);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, keysMode);
    }

    [Fact]
    public async Task Login_response_does_not_contain_password_or_node_token()
    {
        using var client = _fixture.CreateAnonymousClient();
        _fixture.AttachAntiforgery(client);
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = AuthTestMaterial.Username,
            ["password"] = AuthTestMaterial.Password,
            ["returnUrl"] = "/",
        });
        var response = await client.PostAsync("/account/login", body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(AuthTestMaterial.Password, text, StringComparison.Ordinal);
        Assert.False(text.Contains(_fixture.NodeTokenHex, StringComparison.OrdinalIgnoreCase));
        Assert.False(text.Contains(_fixture.SecondaryNodeTokenHex, StringComparison.OrdinalIgnoreCase));
        var headers = response.Headers.ToString();
        Assert.False(headers.Contains(_fixture.NodeTokenHex, StringComparison.OrdinalIgnoreCase));
        Assert.False(headers.Contains(_fixture.SecondaryNodeTokenHex, StringComparison.OrdinalIgnoreCase));
    }
}
