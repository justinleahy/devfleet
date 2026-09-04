using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PiCommandCenter.Application.Projects;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

public class ProjectEndpointsTests : IClassFixture<ControlPlaneFixture>
{
    private readonly ControlPlaneFixture _fixture;

    public ProjectEndpointsTests(ControlPlaneFixture fixture) => _fixture = fixture;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static HttpContent RegisterBody(string displayName, string repositoryPath) =>
        JsonContent.Create(new RegisterProjectCommand(
            DisplayName: displayName,
            RepositoryPath: repositoryPath,
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
    public async Task Registering_a_real_git_repository_returns_201_with_the_project()
    {
        var client = _fixture.CreateClient();
        var repositoryPath = _fixture.CreateGitRepository();

        var response = await client.PostAsync("/api/projects", RegisterBody("Fleet", repositoryPath));
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(HttpStatusCode.Created == response.StatusCode, $"status {(int)response.StatusCode}: {body}");
        Assert.Equal($"/api/projects/{JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()}", response.Headers.Location?.ToString());
        var project = JsonSerializer.Deserialize<ProjectDto>(body, Json)!;
        Assert.Equal("Fleet", project.DisplayName);
        Assert.Equal("main", project.DefaultBranch);
        Assert.Equal(1, project.Version);
    }

    [Fact]
    public async Task Registering_a_duplicate_repository_path_is_a_deterministic_409()
    {
        var client = _fixture.CreateClient();
        var repositoryPath = _fixture.CreateGitRepository();
        var first = await client.PostAsync("/api/projects", RegisterBody("First", repositoryPath));
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());

        var second = await client.PostAsync("/api/projects", RegisterBody("Second", repositoryPath + "/"));
        var body = await second.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal("Duplicate project", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Registering_a_path_outside_the_approved_root_is_a_deterministic_400()
    {
        var client = _fixture.CreateClient();
        var outsideRoot = Path.Combine(Path.GetTempPath(), "pi-cc-integration", Guid.NewGuid().ToString("N"), "outside");
        Directory.CreateDirectory(outsideRoot);

        var response = await client.PostAsync("/api/projects", RegisterBody("Rogue", outsideRoot));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_project_returns_the_registration_and_a_missing_id_is_404()
    {
        var client = _fixture.CreateClient();
        var repositoryPath = _fixture.CreateGitRepository();
        var created = await client.PostAsync("/api/projects", RegisterBody("Lookup", repositoryPath));
        var registered = await created.Content.ReadFromJsonAsync<ProjectDto>(Json);

        var fetched = await client.GetAsync($"/api/projects/{registered!.Id}");
        var dto = await fetched.Content.ReadFromJsonAsync<ProjectDto>(Json);

        Assert.True(fetched.IsSuccessStatusCode, $"status {(int)fetched.StatusCode}");
        Assert.NotNull(dto);
        Assert.Equal(registered.Id, dto!.Id);
        Assert.Equal("Lookup", dto.DisplayName);

        var missing = await client.GetAsync($"/api/projects/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task List_projects_reflects_registrations()
    {
        var client = _fixture.CreateClient();
        var repositoryPath = _fixture.CreateGitRepository();
        var created = await client.PostAsync("/api/projects", RegisterBody("Listed", repositoryPath));
        var registered = await created.Content.ReadFromJsonAsync<ProjectDto>(Json);

        var response = await client.GetAsync("/api/projects");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.True(response.IsSuccessStatusCode);
        var projects = body.GetProperty("projects");
        Assert.True(projects.GetArrayLength() >= 1);
        Assert.Contains(
            projects.EnumerateArray(),
            p => p.GetProperty("id").GetGuid() == registered!.Id);
    }

    [Fact]
    public async Task Validate_reports_the_repository_report_for_a_registered_project()
    {
        var client = _fixture.CreateClient();
        var repositoryPath = _fixture.CreateGitRepository();
        var created = await client.PostAsync("/api/projects", RegisterBody("Validated", repositoryPath));
        var registered = await created.Content.ReadFromJsonAsync<ProjectDto>(Json);

        var response = await client.PostAsync($"/api/projects/{registered!.Id}/validate", content: null);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        Assert.True(report.GetProperty("isValid").GetBoolean(), report.GetRawText());

        var missing = await client.PostAsync($"/api/projects/{Guid.NewGuid()}/validate", content: null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
