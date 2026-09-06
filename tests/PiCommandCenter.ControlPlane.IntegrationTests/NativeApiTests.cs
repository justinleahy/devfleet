using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Infrastructure.Security;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

/// <summary>
/// Defends the native <c>/api/v1</c> contract as an external client sees it: opaque bearer tokens,
/// no cookies, no antiforgery, generic authentication failures, and OpenAPI that documents only v1,
/// while the legacy cookie <c>/api</c> surface keeps its existing behavior.
/// </summary>
public sealed class NativeApiTests : IClassFixture<ControlPlaneFixture>
{
    private const string ProjectsPath = "/api/v1/projects";

    private readonly ControlPlaneFixture _fixture;

    public NativeApiTests(ControlPlaneFixture fixture) => _fixture = fixture;

    private static HttpContent RegisterBody(string displayName) =>
        JsonContent.Create(new RegisterProjectCommand(
            DisplayName: displayName,
            DefaultBranch: "main",
            Enabled: true,
            MaxActiveWriteRequests: 2,
            MaxReadOnlyRequests: 4,
            MaxChildAgentsPerRequest: 1,
            RequireCleanStart: true,
            CreateRequestBranch: true,
            CreateRequestCommit: false,
            AutoMerge: false));

    [Fact]
    public async Task Anonymous_v1_resource_is_401_without_redirect_or_cookie()
    {
        using var client = _fixture.CreateNativeClient();

        var response = await client.GetAsync(ProjectsPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.False(response.Headers.Contains("Set-Cookie"), response.Headers.ToString());
    }

    [Fact]
    public async Task Legacy_cookie_session_does_not_authorize_v1()
    {
        using var client = _fixture.CreateAuthenticatedClient();

        var response = await client.GetAsync(ProjectsPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Login_issues_opaque_bearer_tokens_that_authorize_get_projects()
    {
        using var client = _fixture.CreateNativeClient();

        var tokens = await ControlPlaneFixture.NativeLoginAsync(client);

        Assert.Equal("Bearer", tokens.TokenType);
        Assert.Equal(3600, tokens.ExpiresIn);
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));

        ControlPlaneFixture.UseBearer(client, tokens);
        var response = await client.GetAsync(ProjectsPath);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"status {(int)response.StatusCode}: {body}");
        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(body).RootElement.GetProperty("projects").ValueKind);
    }

    [Fact]
    public async Task Bad_login_is_generic_401_and_leaks_no_secret()
    {
        using var client = _fixture.CreateNativeClient();
        const string wrongPassword = "not-the-password";

        var wrongPasswordResponse = await client.PostAsJsonAsync(
            ControlPlaneFixture.NativeApiLoginPath,
            new { username = AuthTestMaterial.Username, password = wrongPassword });
        var unknownUserResponse = await client.PostAsJsonAsync(
            ControlPlaneFixture.NativeApiLoginPath,
            new { username = "nobody-" + Guid.NewGuid().ToString("N"), password = AuthTestMaterial.Password });

        foreach (var response in new[] { wrongPasswordResponse, unknownUserResponse })
        {
            var text = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Null(response.Headers.Location);
            Assert.False(response.Headers.Contains("Set-Cookie"), response.Headers.ToString());
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

            var problem = JsonDocument.Parse(text).RootElement;
            Assert.Equal(401, problem.GetProperty("status").GetInt32());
            Assert.Equal("Unauthorized", problem.GetProperty("title").GetString());
            Assert.False(problem.TryGetProperty("detail", out _), text);

            Assert.DoesNotContain(wrongPassword, text, StringComparison.Ordinal);
            Assert.DoesNotContain(AuthTestMaterial.Password, text, StringComparison.Ordinal);
            Assert.DoesNotContain(AuthTestMaterial.Username, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(AuthTestMaterial.NodeTokenHex, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(AuthTestMaterial.NodeTokenHex, response.Headers.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Bearer_post_creates_project_without_antiforgery()
    {
        using var client = await _fixture.CreateNativeAuthenticatedClientAsync();
        Assert.False(client.DefaultRequestHeaders.Contains("Cookie"));
        Assert.False(client.DefaultRequestHeaders.Contains("RequestVerificationToken"));

        var response = await client.PostAsync(ProjectsPath, RegisterBody("Native"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.Created, $"status {(int)response.StatusCode}: {body}");
        var project = JsonDocument.Parse(body).RootElement;
        Assert.Equal("Native", project.GetProperty("displayName").GetString());
        Assert.Equal($"{ProjectsPath}/{project.GetProperty("id").GetString()}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Refresh_reissues_a_usable_access_token_and_rejects_garbage()
    {
        using var loginClient = _fixture.CreateNativeClient();
        var initial = await ControlPlaneFixture.NativeLoginAsync(loginClient);

        var refreshed = await ControlPlaneFixture.NativeRefreshAsync(loginClient, initial.RefreshToken);

        Assert.Equal("Bearer", refreshed.TokenType);
        Assert.Equal(3600, refreshed.ExpiresIn);
        Assert.NotEqual(initial.AccessToken, refreshed.AccessToken);
        Assert.False(string.IsNullOrWhiteSpace(refreshed.RefreshToken));

        using var resourceClient = _fixture.CreateNativeClient();
        ControlPlaneFixture.UseBearer(resourceClient, refreshed);
        var response = await resourceClient.GetAsync(ProjectsPath);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"status {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var garbage = await loginClient.PostAsJsonAsync(
            ControlPlaneFixture.NativeApiRefreshPath,
            new { refreshToken = "not-a-refresh-token" });
        var garbageText = await garbage.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, garbage.StatusCode);
        Assert.Equal("application/problem+json", garbage.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Unauthorized", JsonDocument.Parse(garbageText).RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain("not-a-refresh-token", garbageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_cookie_api_still_requires_antiforgery_for_the_same_request()
    {
        using var client = _fixture.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");

        var rejected = await client.PostAsync("/api/projects", RegisterBody("Legacy"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        _fixture.AttachAntiforgery(client, asAdmin: true);
        var accepted = await client.PostAsync("/api/projects", RegisterBody("Legacy"));
        var body = await accepted.Content.ReadAsStringAsync();
        Assert.True(accepted.StatusCode == HttpStatusCode.Created, $"status {(int)accepted.StatusCode}: {body}");
        Assert.Equal($"/api/projects/{JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()}", accepted.Headers.Location?.ToString());
    }

    [Fact]
    public async Task OpenApi_v1_document_lists_only_native_routes()
    {
        using var client = _fixture.CreateNativeClient();

        var response = await client.GetAsync("/openapi/v1.json");
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"status {(int)response.StatusCode}: {text}");
        var paths = JsonDocument.Parse(text).RootElement.GetProperty("paths");
        var routes = paths.EnumerateObject().Select(p => p.Name).ToList();

        Assert.Contains(ProjectsPath, routes);
        Assert.Contains(ControlPlaneFixture.NativeApiLoginPath, routes);
        Assert.Contains(ControlPlaneFixture.NativeApiRefreshPath, routes);
        Assert.All(routes, route => Assert.StartsWith("/api/v1/", route, StringComparison.Ordinal));
        Assert.DoesNotContain("/api/projects", routes);
        Assert.DoesNotContain("/account/login", routes);
        Assert.DoesNotContain("/api/v1/auth/logout", routes);
        Assert.DoesNotContain("/api/v1/auth/register", routes);
    }
}
