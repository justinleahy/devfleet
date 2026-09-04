using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

public class ProjectRequestEndpointsTests : IClassFixture<ControlPlaneFixture>
{
    private readonly ControlPlaneFixture _fixture;

    public ProjectRequestEndpointsTests(ControlPlaneFixture fixture) => _fixture = fixture;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<Guid> CreateProjectAsync(HttpClient client)
    {
        var repositoryPath = _fixture.CreateGitRepository();
        var response = await client.PostAsJsonAsync(
            "/api/projects",
            new
            {
                displayName = "Queue project",
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

    private static HttpContent RequestBody(string title, int priority) => JsonContent.Create(new
    {
        kind = 0, // Development
        priority,
        riskLevel = 1, // Standard
        title,
        prompt = "Fix the ordering of the queue",
    });

    [Fact]
    public async Task Enqueued_requests_start_queued_and_list_in_priority_order()
    {
        var client = _fixture.CreateClient();
        var projectId = await CreateProjectAsync(client);

        await client.PostAsync($"/api/projects/{projectId}/requests", RequestBody("normal-first", priority: 1));
        await Task.Delay(60);
        await client.PostAsync($"/api/projects/{projectId}/requests", RequestBody("urgent", priority: 3));
        await Task.Delay(60);
        await client.PostAsync($"/api/projects/{projectId}/requests", RequestBody("high", priority: 2));
        await Task.Delay(60);
        await client.PostAsync($"/api/projects/{projectId}/requests", RequestBody("normal-second", priority: 1));

        var response = await client.GetAsync($"/api/projects/{projectId}/requests");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.True(response.IsSuccessStatusCode);
        var requests = body.GetProperty("requests");
        Assert.Equal(4, requests.GetArrayLength());
        Assert.Equal(
            new[] { "urgent", "high", "normal-first", "normal-second" },
            requests.EnumerateArray().Select(r => r.GetProperty("title").GetString()).ToArray());

        var first = requests.EnumerateArray().First();
        Assert.Equal("Queued", first.GetProperty("statusName").GetString());
        Assert.Equal("Urgent", first.GetProperty("priorityName").GetString());
        Assert.Equal("Development", first.GetProperty("kindName").GetString());
        Assert.Equal(1, first.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Request_endpoints_fail_deterministically_for_a_missing_project()
    {
        var client = _fixture.CreateClient();
        var missing = Guid.NewGuid();

        var list = await client.GetAsync($"/api/projects/{missing}/requests");
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);

        var enqueue = await client.PostAsync($"/api/projects/{missing}/requests", RequestBody("orphan", priority: 1));
        Assert.Equal(HttpStatusCode.NotFound, enqueue.StatusCode);
    }

    [Fact]
    public async Task Enqueue_rejects_an_invalid_body_with_400()
    {
        var client = _fixture.CreateClient();
        var projectId = await CreateProjectAsync(client);

        var response = await client.PostAsync(
            $"/api/projects/{projectId}/requests",
            RequestBody(title: "   ", priority: 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
