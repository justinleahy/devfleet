using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

public class ProjectEndpointsTests : IClassFixture<ControlPlaneFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ControlPlaneFixture _fixture;

    public ProjectEndpointsTests(ControlPlaneFixture fixture) => _fixture = fixture;

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

    private static HttpContent DesignationBody(Guid nodeId, string repositoryPath) =>
        JsonContent.Create(new { nodeId, repositoryPath });

    private static async Task<ProjectDto> RegisterProjectAsync(HttpClient client, string displayName)
    {
        var response = await client.PostAsync("/api/projects", RegisterBody(displayName));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"status {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<ProjectDto>(body, Json)!;
    }

    private async Task RegisterNodeAsync()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<INodeRegistry>();
        await registry.RegisterAsync(
            new RegisterNodeCommand(
                new NodeId(_fixture.AuthenticatedNodeId),
                "project-endpoint-node",
                "1.0.0",
                "{}"),
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Registration_accepts_metadata_only_and_returns_an_unbound_project()
    {
        using var client = _fixture.CreateClient();

        var response = await client.PostAsync("/api/projects", RegisterBody("Fleet"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var project = JsonSerializer.Deserialize<ProjectDto>(body, Json)!;
        Assert.Equal($"/api/projects/{project.Id}", response.Headers.Location?.ToString());
        Assert.Equal("Fleet", project.DisplayName);
        Assert.Equal("main", project.DefaultBranch);
        Assert.Null(project.Binding);
        Assert.Equal(1, project.Version);
    }

    [Fact]
    public async Task Invalid_project_metadata_is_a_deterministic_400()
    {
        using var client = _fixture.CreateClient();

        var response = await client.PostAsync("/api/projects", RegisterBody("   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal("Validation failed", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Get_and_list_include_an_unbound_registration()
    {
        using var client = _fixture.CreateClient();
        var registered = await RegisterProjectAsync(client, "Lookup");

        var fetchedResponse = await client.GetAsync($"/api/projects/{registered.Id}");
        var fetched = await fetchedResponse.Content.ReadFromJsonAsync<ProjectDto>(Json);
        var listResponse = await client.GetAsync("/api/projects");
        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.Equal(HttpStatusCode.OK, fetchedResponse.StatusCode);
        Assert.NotNull(fetched);
        Assert.Equal(registered.Id, fetched!.Id);
        Assert.Null(fetched.Binding);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listed = list.GetProperty("projects")
            .EnumerateArray()
            .Single(project => project.GetProperty("id").GetGuid() == registered.Id);
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("binding").ValueKind);

        var missing = await client.GetAsync($"/api/projects/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Workspace_binding_can_be_designated_remain_pending_offline_and_deleted()
    {
        using var client = _fixture.CreateClient();
        var project = await RegisterProjectAsync(client, "Binding lifecycle");
        await RegisterNodeAsync();
        var repositoryPath = _fixture.CreateGitRepository();

        var designatedResponse = await client.PutAsync(
            $"/api/projects/{project.Id}/workspace-binding",
            DesignationBody(_fixture.AuthenticatedNodeId, repositoryPath));
        var designated = await designatedResponse.Content.ReadFromJsonAsync<WorkspaceBindingDto>(Json);

        Assert.Equal(HttpStatusCode.OK, designatedResponse.StatusCode);
        Assert.NotNull(designated);
        Assert.Equal(_fixture.AuthenticatedNodeId, designated!.NodeId);
        Assert.Equal(repositoryPath, designated.RepositoryPath);
        Assert.Equal(WorkspaceBindingStatus.PendingValidation, designated.Status);
        Assert.Equal(1, designated.ValidationRevision);
        Assert.Null(designated.CanonicalRepositoryPath);

        var validationResponse = await client.PostAsync(
            $"/api/projects/{project.Id}/workspace-binding/validate",
            content: null);
        var pending = await validationResponse.Content.ReadFromJsonAsync<WorkspaceBindingDto>(Json);

        Assert.Equal(HttpStatusCode.OK, validationResponse.StatusCode);
        Assert.NotNull(pending);
        Assert.Equal(designated, pending);
        Assert.Equal(WorkspaceBindingStatus.PendingValidation, pending!.Status);

        var fetched = await client.GetFromJsonAsync<ProjectDto>($"/api/projects/{project.Id}", Json);
        Assert.Equal(designated, fetched!.Binding);

        var deleted = await client.DeleteAsync($"/api/projects/{project.Id}/workspace-binding");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        fetched = await client.GetFromJsonAsync<ProjectDto>($"/api/projects/{project.Id}", Json);
        Assert.Null(fetched!.Binding);
    }

    [Fact]
    public async Task Workspace_binding_routes_map_missing_invalid_and_conflicting_designations()
    {
        using var client = _fixture.CreateClient();
        await RegisterNodeAsync();
        var firstProject = await RegisterProjectAsync(client, "First binding");
        var secondProject = await RegisterProjectAsync(client, "Second binding");
        var repositoryPath = _fixture.CreateGitRepository();

        var missingProject = await client.PutAsync(
            $"/api/projects/{Guid.NewGuid()}/workspace-binding",
            DesignationBody(_fixture.AuthenticatedNodeId, repositoryPath));
        var missingNode = await client.PutAsync(
            $"/api/projects/{firstProject.Id}/workspace-binding",
            DesignationBody(Guid.NewGuid(), repositoryPath));
        var invalidPath = await client.PutAsync(
            $"/api/projects/{firstProject.Id}/workspace-binding",
            DesignationBody(_fixture.AuthenticatedNodeId, "relative/path"));
        var firstDesignation = await client.PutAsync(
            $"/api/projects/{firstProject.Id}/workspace-binding",
            DesignationBody(_fixture.AuthenticatedNodeId, repositoryPath));
        var conflict = await client.PutAsync(
            $"/api/projects/{secondProject.Id}/workspace-binding",
            DesignationBody(_fixture.AuthenticatedNodeId, repositoryPath));

        Assert.Equal(HttpStatusCode.NotFound, missingProject.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingNode.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidPath.StatusCode);
        Assert.Equal(HttpStatusCode.OK, firstDesignation.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Unbound_validation_is_409_and_missing_binding_resources_are_404()
    {
        using var client = _fixture.CreateClient();
        var project = await RegisterProjectAsync(client, "Unbound validation");
        var missingProjectId = Guid.NewGuid();

        var unboundValidation = await client.PostAsync(
            $"/api/projects/{project.Id}/workspace-binding/validate",
            content: null);
        var missingValidation = await client.PostAsync(
            $"/api/projects/{missingProjectId}/workspace-binding/validate",
            content: null);
        var missingDelete = await client.DeleteAsync(
            $"/api/projects/{missingProjectId}/workspace-binding");

        Assert.Equal(HttpStatusCode.Conflict, unboundValidation.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingValidation.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingDelete.StatusCode);
    }

    [Fact]
    public async Task Legacy_project_validate_post_returns_method_not_allowed()
    {
        using var client = _fixture.CreateClient();
        var project = await RegisterProjectAsync(client, "No ambiguous validation");

        var response = await client.PostAsync($"/api/projects/{project.Id}/validate", content: null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
