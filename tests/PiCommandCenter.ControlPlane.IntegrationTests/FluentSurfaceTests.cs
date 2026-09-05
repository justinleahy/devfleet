using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.ControlPlane.Security;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Infrastructure.Persistence;
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
    public async Task Dashboard_renders_fleet_resource_labels_and_values_from_the_latest_heartbeat()
    {
        var nodeId = Guid.NewGuid();
        await using var connection = _fixture.CreateNodeHubConnection();
        await connection.StartAsync();
        await connection.InvokeAsync<NodeDto>(
            "Register", new NodeRegistrationMessage(nodeId, "pi-resource-ui", "1.0.0", "{}"));
        var observedAt = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        await connection.InvokeAsync<NodeDto>(
            "Heartbeat",
            new NodeHeartbeatMessage(
                nodeId,
                [],
                new NodeResourceSnapshotMessage(
                    observedAt,
                    CpuUsagePercent: 12.5,
                    MemoryUsedBytes: 1024L * 1024L,
                    MemoryTotalBytes: 2L * 1024L * 1024L,
                    DiskUsedBytes: 3L * 1024L * 1024L,
                    DiskTotalBytes: 4L * 1024L * 1024L,
                    LoadAverageOneMinute: 0.5,
                    UptimeSeconds: 3661d)));

        var html = await GetHtmlAsync(_fixture.CreateClient(), "/");

        Assert.Contains("pi-resource-ui", html);
        Assert.Contains(">CPU<", html);
        Assert.Contains("12.5%", html);
        Assert.Contains(">Memory<", html);
        Assert.Contains("1.0 MiB of 2.0 MiB", html);
        Assert.Contains(">Disk<", html);
        Assert.Contains("3.0 MiB of 4.0 MiB", html);
        Assert.Contains("Load, 1 minute", html);
        Assert.Contains("0.50", html);
        Assert.Contains(">Uptime<", html);
        Assert.Contains("1h 1m", html);
        Assert.DoesNotContain(observedAt.ToString("u"), html);
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

    [Fact]
    public async Task Navigation_offers_statistics_after_usage()
    {
        var client = _fixture.CreateClient();

        var html = await GetHtmlAsync(client, "/");

        // "Usage" and "Statistics" are section names; neither word is dashboard copy.
        var usage = html.IndexOf("Usage", StringComparison.Ordinal);
        var statistics = html.IndexOf("Statistics", StringComparison.Ordinal);
        Assert.True(usage >= 0, "Usage nav entry missing");
        Assert.True(statistics >= 0, "Statistics nav entry missing");
        Assert.True(usage < statistics, "Statistics must follow Usage in the section list");
        Assert.Contains("/statistics", html);
    }

    [Fact]
    public async Task Statistics_page_renders_every_counter_label_and_the_cost_caveat()
    {
        var client = _fixture.CreateClient();

        var html = await GetHtmlAsync(client, "/statistics");

        Assert.Contains("Fleet statistics", html);
        Assert.Contains("Tracked agents", html);
        Assert.Contains("Active agents", html);
        Assert.Contains("Input tokens", html);
        Assert.Contains("Output tokens", html);
        Assert.Contains("Cache read", html);
        Assert.Contains("Cache write", html);
        Assert.Contains("Thinking tokens", html);
        Assert.Contains("Estimated cost (USD)", html);
        Assert.Contains("By runtime", html);
        // The USD figure is whatever the runtime itself estimated, and the page says so.
        Assert.Contains("client-side catalogue estimate, not a billing figure.", html);
    }

    [Fact]
    public async Task Statistics_page_reads_an_unreported_counter_as_unavailable_and_a_real_zero_as_zero()
    {
        var client = _fixture.CreateClient();

        var html = await GetHtmlAsync(client, "/statistics");

        // No session has run, so the agent counts are a measured zero while every token series
        // and the cost estimate are absent rather than zero.
        Assert.Contains("0 of 0 agent(s) reported tokens", html);
        Assert.Contains("0 malformed telemetry event(s) ignored", html);
        Assert.Contains("no agent report recorded", html);
        Assert.Contains("Unavailable", html);
        Assert.Contains("No agent session recorded yet", html);
        Assert.Contains("Back to fleet", html);
    }

    /// <summary>
    /// Boots a second control plane over its own database, so a seeded session never disturbs
    /// the shared fixture's empty-fleet expectations, records one pi session carrying
    /// <paramref name="usageJson"/> as its finished-turn usage, and returns the statistics page.
    /// </summary>
    private async Task<string> StatisticsHtmlForPiUsageAsync(string usageJson)
    {
        var root = Path.Combine(Path.GetTempPath(), "pi-cc-statistics", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sqlitePath = Path.Combine(root, "controlplane.db");
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:ControlPlane", $"Data Source={sqlitePath}");
                builder.UseSetting("Projects:ApprovedRoots:0", _fixture.ApprovedRoot);
                builder.UseTestAuthFiles(_fixture.PasswordFile, _fixture.CredentialFile);
            });

            var projectId = Guid.NewGuid();
            var requestId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
                await db.Database.MigrateAsync();
                db.AgentSessions.Add(new AgentSessionRow
                {
                    Id = "s-figures",
                    ProjectId = projectId,
                    RequestId = requestId,
                    AgentName = "s-figures",
                    Role = "task",
                    Runtime = AgentRuntimeKinds.Pi,
                    Model = "model",
                    Liveness = "Online",
                    Activity = "Responding",
                    Attention = "None",
                    WorkState = "Executing",
                    StatusReason = string.Empty,
                    StartedAtUtcTicks = now.UtcTicks,
                });
                db.SessionEvents.Add(new SessionEvent
                {
                    EventId = "evt-figures-1",
                    NodeId = Guid.NewGuid(),
                    ProjectId = projectId,
                    RequestId = requestId,
                    SessionId = "s-figures",
                    Sequence = 1,
                    Type = "message.completed",
                    OccurredAtUtcTicks = now.UtcTicks,
                    ReceivedAtUtcTicks = now.UtcTicks,
                    PayloadJson = $$"""
                        { "data": { "type": "message_end", "message": { "role": "assistant",
                          "usage": {{usageJson}} } } }
                        """,
                });
                await db.SaveChangesAsync();
            }

            using var client = _fixture.CreateAuthenticatedClient(factory);
            return await GetHtmlAsync(client, "/statistics");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Statistics_page_states_a_billion_scale_counter_in_full()
    {
        var html = await StatisticsHtmlForPiUsageAsync(
            """{ "input": 1234567890, "output": 40, "cacheRead": 0, "cacheWrite": 0 }""");

        // Counters are exact, so every digit of the grouped total is on the page, not a
        // rounded or abbreviated stand-in.
        Assert.Contains("1,234,567,890", html);
    }

    [Fact]
    public async Task Statistics_page_reads_a_cost_below_four_places_as_a_positive_bound()
    {
        var html = await StatisticsHtmlForPiUsageAsync(
            """
            { "input": 10, "output": 5, "cacheRead": 0, "cacheWrite": 0,
              "cost": { "total": 0.00004 } }
            """);

        // A genuine estimate under the four-place form still reads as money that was spent:
        // "< 0.0001" (escaped in the markup), never 0.0000 and never a plain 0.
        Assert.Contains("&lt; 0.0001", html);
        Assert.DoesNotContain("0.0000", html);
    }
}
