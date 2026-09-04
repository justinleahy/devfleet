using System.Net.Http.Json;
using System.Text.Json;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

/// <summary>
/// The Fluent UI Blazor surface as it is actually rendered (prerendered HTML of the Interactive
/// Server pages): the fleet dashboard and the per-project request composer.
/// </summary>
public class FluentSurfaceTests : IClassFixture<ControlPlaneFixture>
{
    private readonly ControlPlaneFixture _fixture;

    public FluentSurfaceTests(ControlPlaneFixture fixture) => _fixture = fixture;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<Guid> RegisterProjectAsync(HttpClient client, string displayName)
    {
        var repositoryPath = _fixture.CreateGitRepository();
        var response = await client.PostAsJsonAsync(
            "/api/projects",
            new
            {
                displayName,
                repositoryPath,
                defaultBranch = "main",
                enabled = true,
                maxActiveWriteRequests = 2,
                maxReadOnlyRequests = 4,
                maxChildAgentsPerRequest = 1,
                requireCleanStart = true,
                createRequestBranch = true,
                createRequestCommit = false,
                autoMerge = false,
            },
            Json);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"status {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<string> GetHtmlAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"status {(int)response.StatusCode}: {html}");
        return html;
    }

    [Fact]
    public async Task Dashboard_renders_the_fleet_metrics_and_project_cards()
    {
        var client = _fixture.CreateClient();
        var projectId = await RegisterProjectAsync(client, "Card project");

        var html = await GetHtmlAsync(client, "/");

        Assert.Contains("Fleet dashboard", html);
        Assert.Contains("Active projects", html);
        Assert.Contains("Active agents", html);
        Assert.Contains("Queued requests", html);
        Assert.Contains("Needs attention", html);
        Assert.Contains("Projects", html);
        Assert.Contains("Card project", html);
        Assert.Contains($"/projects/{projectId}", html);
        // The registration surface is reachable from the dashboard.
        Assert.Contains("Register project", html);
    }

    [Fact]
    public async Task Dashboard_shows_the_empty_state_before_anything_is_registered()
    {
        var client = _fixture.CreateClient();

        var html = await GetHtmlAsync(client, "/");

        Assert.Contains("Fleet dashboard", html);
        Assert.Contains("No projects registered", html);
    }

    [Fact]
    public async Task Project_page_renders_the_metadata_and_request_composer()
    {
        var client = _fixture.CreateClient();
        var projectId = await RegisterProjectAsync(client, "Composer project");

        var html = await GetHtmlAsync(client, $"/projects/{projectId}");

        Assert.Contains("Composer project", html);
        Assert.Contains("Repository path", html);
        Assert.Contains("Default branch", html);
        Assert.Contains("Node status", html);
        Assert.Contains("New request", html);
        Assert.Contains("Queue", html);
        // The composer fields.
        Assert.Contains("Title", html);
        Assert.Contains("Prompt", html);
        Assert.Contains("Queue request", html);
        // Empty queue states ship with the page.
        Assert.Contains("Nothing queued.", html);
        Assert.Contains("No active request.", html);
    }

    [Fact]
    public async Task Project_page_lists_queued_requests_in_priority_order_with_their_badges()
    {
        var client = _fixture.CreateClient();
        var projectId = await RegisterProjectAsync(client, "Queue surface");
        await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/requests",
            new { kind = 0, priority = 1, riskLevel = 1, title = "normal request", prompt = "work" },
            Json);
        await Task.Delay(60);
        await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/requests",
            new { kind = 0, priority = 3, riskLevel = 1, title = "urgent request", prompt = "work" },
            Json);

        var html = await GetHtmlAsync(client, $"/projects/{projectId}");

        var urgent = html.IndexOf("urgent request", StringComparison.Ordinal);
        var normal = html.IndexOf("normal request", StringComparison.Ordinal);
        Assert.True(urgent >= 0, "urgent request missing from project page");
        Assert.True(normal >= 0, "normal request missing from project page");
        Assert.True(urgent < normal, "queue rows must render priority descending");
        Assert.Contains("Queued", html);
    }

    [Fact]
    public async Task Unknown_project_id_renders_the_not_found_surface()
    {
        var client = _fixture.CreateClient();

        var html = await GetHtmlAsync(client, $"/projects/{Guid.NewGuid()}");

        Assert.Contains("Project not found", html);
        Assert.Contains("No project is registered with that id.", html);
        Assert.Contains("Back to fleet", html);
    }
}
