using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

public class ProjectRequestEndpointsTests : IClassFixture<ControlPlaneFixture>
{
    private readonly ControlPlaneFixture _fixture;

    public ProjectRequestEndpointsTests(ControlPlaneFixture fixture) => _fixture = fixture;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<Guid> CreateProjectAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/projects",
            new
            {
                displayName = "Queue project",
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
        var project = JsonSerializer.Deserialize<ProjectDto>(body, Json)!;
        Assert.Null(project.Binding);
        return project.Id;
    }

    private static async Task<JsonElement> EnqueueAsync(
        HttpClient client,
        Guid projectId,
        string title,
        int priority)
    {
        var response = await client.PostAsync(
            $"/api/projects/{projectId}/requests",
            RequestBody(title, priority));
        var request = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(
            $"/api/requests/{request.GetProperty("id").GetGuid()}",
            response.Headers.Location?.ToString());
        return request;
    }

    private static HttpContent RequestBody(string title, int priority) => JsonContent.Create(new
    {
        kind = 0, // Development
        priority,
        riskLevel = 1, // Standard
        title,
        prompt = "Fix the ordering of the queue",
    });

    private static void AssertWaitingForWorkspaceBinding(JsonElement request)
    {
        var schedulingStatus = request.GetProperty("schedulingStatus");
        Assert.Equal("workspace_binding_missing", schedulingStatus.GetProperty("code").GetString());
        Assert.False(schedulingStatus.GetProperty("isEligible").GetBoolean());
        Assert.Equal(JsonValueKind.Null, request.GetProperty("assignment").ValueKind);
    }

    private async Task<SeededAssignment> SeedAssignmentAsync(Guid projectId, Guid requestId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var project = await db.Projects.SingleAsync(candidate => candidate.Id == new ProjectId(projectId));
        var request = await db.WorkRequests.SingleAsync(candidate => candidate.Id == new WorkRequestId(requestId));
        var now = DateTimeOffset.UtcNow;
        var nodeId = NodeId.New();
        var repositoryPath = Path.Combine(_fixture.ApprovedRoot, $"assignment-{requestId:N}");
        var node = FleetNode.Register(nodeId, $"request-node-{requestId:N}", "1.0.0", "{}", now);
        var binding = WorkspaceBinding.Designate(project.Id, nodeId, repositoryPath, now);
        Assert.True(binding.ApplyValidationResult(
            nodeId,
            binding.ValidationRevision,
            WorkspaceBindingStatus.Valid,
            WorkspaceBinding.ValidValidationCode,
            "Seeded for request projection verification.",
            repositoryPath,
            now));

        request.Start(now);
        var claimToken = $"secret-claim-{Guid.NewGuid():N}";
        var assignment = ExecutionAssignment.Create(
            request.Id,
            project.Id,
            binding.Id,
            nodeId,
            repositoryPath,
            project.DefaultBranch,
            binding.ValidationRevision,
            claimToken,
            now,
            TimeSpan.FromMinutes(1));
        db.FleetNodes.Add(node);
        db.WorkspaceBindings.Add(binding);
        db.ExecutionAssignments.Add(assignment);
        await db.SaveChangesAsync();

        return new SeededAssignment(
            requestId,
            projectId,
            binding.Id.Value,
            nodeId.Value,
            repositoryPath,
            project.DefaultBranch,
            binding.ValidationRevision,
            claimToken);
    }

    private static void AssertAssignmentProjection(
        JsonElement request,
        SeededAssignment expected,
        string responseBody)
    {
        Assert.Equal(JsonValueKind.Null, request.GetProperty("schedulingStatus").ValueKind);
        var assignment = request.GetProperty("assignment");
        Assert.Equal(expected.RequestId, assignment.GetProperty("requestId").GetGuid());
        Assert.Equal(expected.ProjectId, assignment.GetProperty("projectId").GetGuid());
        Assert.Equal(expected.WorkspaceBindingId, assignment.GetProperty("workspaceBindingId").GetGuid());
        Assert.Equal(expected.NodeId, assignment.GetProperty("nodeIdSnapshot").GetGuid());
        Assert.Equal(expected.RepositoryPath, assignment.GetProperty("canonicalRepositoryPathSnapshot").GetString());
        Assert.Equal(expected.DefaultBranch, assignment.GetProperty("defaultBranchSnapshot").GetString());
        Assert.Equal(
            expected.BindingValidationRevision,
            assignment.GetProperty("bindingValidationRevisionSnapshot").GetInt64());
        Assert.Equal((int)ExecutionAssignmentState.Starting, assignment.GetProperty("state").GetInt32());
        Assert.Equal(JsonValueKind.Null, assignment.GetProperty("lastRenewedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, assignment.GetProperty("lastReconciledAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, assignment.GetProperty("terminalAt").ValueKind);
        Assert.False(assignment.TryGetProperty("claimToken", out _));
        Assert.DoesNotContain(expected.ClaimToken, responseBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"claimToken\"", responseBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unbound_project_requests_start_queued_and_list_in_priority_order()
    {
        using var client = _fixture.CreateClient();
        var projectId = await CreateProjectAsync(client);

        await EnqueueAsync(client, projectId, "normal-first", priority: 1);
        await Task.Delay(60);
        var enqueued = await EnqueueAsync(client, projectId, "urgent", priority: 3);
        await Task.Delay(60);
        await EnqueueAsync(client, projectId, "high", priority: 2);
        await Task.Delay(60);
        await EnqueueAsync(client, projectId, "normal-second", priority: 1);
        AssertWaitingForWorkspaceBinding(enqueued);

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
        AssertWaitingForWorkspaceBinding(first);

        var getResponse = await client.GetAsync($"/api/requests/{first.GetProperty("id").GetGuid()}");
        var single = await getResponse.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        AssertWaitingForWorkspaceBinding(single);
    }

    [Fact]
    public async Task List_and_get_expose_assignment_without_the_claim_token()
    {
        using var client = _fixture.CreateClient();
        var projectId = await CreateProjectAsync(client);
        var enqueued = await EnqueueAsync(client, projectId, "assigned", priority: 1);
        var requestId = enqueued.GetProperty("id").GetGuid();
        var expected = await SeedAssignmentAsync(projectId, requestId);

        var getResponse = await client.GetAsync($"/api/requests/{requestId}");
        var getBody = await getResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        using var getDocument = JsonDocument.Parse(getBody);
        AssertAssignmentProjection(getDocument.RootElement, expected, getBody);

        var listResponse = await client.GetAsync($"/api/projects/{projectId}/requests");
        var listBody = await listResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        using var listDocument = JsonDocument.Parse(listBody);
        var listed = listDocument.RootElement.GetProperty("requests").EnumerateArray().Single();
        AssertAssignmentProjection(listed, expected, listBody);
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
    public async Task Get_returns_404_for_a_missing_request()
    {
        using var client = _fixture.CreateClient();

        var response = await client.GetAsync($"/api/requests/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

    [Fact]
    public async Task Cancel_queued_request_is_immediate_and_idempotent()
    {
        using var client = _fixture.CreateClient();
        var projectId = await CreateProjectAsync(client);
        var queued = await EnqueueAsync(client, projectId, "cancel queued", priority: 1);
        var requestId = queued.GetProperty("id").GetGuid();

        var first = await client.PostAsJsonAsync(
            $"/api/requests/{requestId}/cancel",
            new { reason = "operator stop" });
        var retry = await client.PostAsJsonAsync(
            $"/api/requests/{requestId}/cancel",
            new { reason = "operator stop" });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var state = await retry.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("Cancelled", state.GetProperty("requestStatus").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("assignmentState").ValueKind);
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var persisted = await db.WorkRequests.SingleAsync(
            candidate => candidate.Id == new WorkRequestId(requestId));
        Assert.Equal(WorkRequestStatus.Cancelled, persisted.Status);
        Assert.False(await db.ExecutionAssignments.AnyAsync(
            candidate => candidate.RequestId == persisted.Id));
    }

    [Fact]
    public async Task Cancel_assigned_request_persists_cancelling_without_terminalizing()
    {
        using var client = _fixture.CreateClient();
        var projectId = await CreateProjectAsync(client);
        var queued = await EnqueueAsync(client, projectId, "cancel assigned", priority: 1);
        var requestId = queued.GetProperty("id").GetGuid();
        await SeedAssignmentAsync(projectId, requestId);

        var response = await client.PostAsJsonAsync(
            $"/api/requests/{requestId}/cancel",
            new { reason = "operator stop" });
        var state = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Cancelling", state.GetProperty("requestStatus").GetString());
        Assert.Equal("Cancelling", state.GetProperty("assignmentState").GetString());
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        Assert.Equal(
            WorkRequestStatus.Cancelling,
            (await db.WorkRequests.SingleAsync(
                candidate => candidate.Id == new WorkRequestId(requestId))).Status);
        var assignment = await db.ExecutionAssignments.SingleAsync(
            candidate => candidate.RequestId == new WorkRequestId(requestId));
        Assert.Equal(ExecutionAssignmentState.Cancelling, assignment.State);
        Assert.Null(assignment.TerminalAt);
    }

    [Fact]
    public async Task Native_cancel_requires_bearer_authentication()
    {
        using var anonymous = _fixture.CreateNativeClient();
        var requestId = Guid.NewGuid();

        var rejected = await anonymous.PostAsJsonAsync(
            $"/api/v1/requests/{requestId}/cancel",
            new { reason = "operator stop" });

        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Null(rejected.Headers.Location);
    }

    private sealed record SeededAssignment(
        Guid RequestId,
        Guid ProjectId,
        Guid WorkspaceBindingId,
        Guid NodeId,
        string RepositoryPath,
        string DefaultBranch,
        long BindingValidationRevision,
        string ClaimToken);

}
