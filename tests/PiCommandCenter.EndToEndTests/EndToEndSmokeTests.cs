using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PiCommandCenter.Contracts;
using PiCommandCenter.Node;

namespace PiCommandCenter.EndToEndTests;

public class EndToEndSmokeTests : IClassFixture<EndToEndFixture>
{
    private readonly EndToEndFixture _fixture;

    public EndToEndSmokeTests(EndToEndFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ControlPlane_serves_the_fleet_dashboard_and_health_endpoint()
    {
        var client = _fixture.CreateClient();

        var healthResponse = await client.GetAsync("/health");
        var healthPayload = await healthResponse.Content.ReadAsStringAsync();
        var homeResponse = await client.GetAsync("/");
        var html = await homeResponse.Content.ReadAsStringAsync();

        Assert.True(healthResponse.IsSuccessStatusCode, $"status {(int)healthResponse.StatusCode}: {healthPayload}");
        Assert.Equal("Healthy", healthPayload.Trim());
        Assert.True(homeResponse.IsSuccessStatusCode, html);
        Assert.Contains("Pi Command Center", html);
        Assert.Contains("Fleet dashboard", html);
        Assert.Contains("Active projects", html);
        Assert.Contains("Queued requests", html);
    }

    [Fact]
    public void Node_hosting_registers_a_hosted_worker_and_matches_the_protocol_contract()
    {
        using var app = new HostBuilder()
            .ConfigureServices(services => services.AddPiNode())
            .Build();

        Assert.Contains(app.Services.GetServices<IHostedService>(), s => s is NodeWorker);
        Assert.Equal(1, ProtocolVersion.Current);
    }

    [Fact]
    public async Task Registering_a_project_and_queuing_requests_is_visible_in_the_fluent_surface()
    {
        var client = _fixture.CreateClient();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        // 1. Register a real temporary Git repository inside the approved root.
        var repositoryPath = _fixture.CreateGitRepository();
        var registerResponse = await client.PostAsJsonAsync(
            "/api/projects",
            new
            {
                displayName = "Journey project",
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
            json);
        var registerBody = await registerResponse.Content.ReadAsStringAsync();
        Assert.True(registerResponse.IsSuccessStatusCode, $"status {(int)registerResponse.StatusCode}: {registerBody}");
        var projectId = JsonDocument.Parse(registerBody).RootElement.GetProperty("id").GetGuid();

        // 2. Queue two requests with different priorities.
        await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/requests",
            new { kind = 0, priority = 1, riskLevel = 1, title = "steady work", prompt = "Do the work" },
            json);
        await Task.Delay(60);
        await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/requests",
            new { kind = 0, priority = 3, riskLevel = 1, title = "urgent work", prompt = "Do it first" },
            json);

        // 3. The dashboard shows the project card.
        var dashboard = await (await client.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("Journey project", dashboard);
        Assert.Contains($"/projects/{projectId}", dashboard);

        // 4. The project page shows the composer and the ordered queue.
        var projectPage = await (await client.GetAsync($"/projects/{projectId}")).Content.ReadAsStringAsync();
        Assert.Contains("Journey project", projectPage);
        Assert.Contains("New request", projectPage);
        Assert.Contains("Queue request", projectPage);
        var urgent = projectPage.IndexOf("urgent work", StringComparison.Ordinal);
        var steady = projectPage.IndexOf("steady work", StringComparison.Ordinal);
        Assert.True(urgent >= 0 && steady >= 0, $"project page missing queue rows:\n{projectPage}");
        Assert.True(urgent < steady, "queue rows must render priority descending");
    }
}
