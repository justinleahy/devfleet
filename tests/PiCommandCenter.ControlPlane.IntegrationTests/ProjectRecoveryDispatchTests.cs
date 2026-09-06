using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Application.Recovery;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Recovery;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

public sealed class ProjectRecoveryDispatchTests : IClassFixture<ControlPlaneFixture>, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ControlPlaneFixture _fixture;
    private readonly HubConnection _connection;

    public ProjectRecoveryDispatchTests(ControlPlaneFixture fixture)
    {
        _fixture = fixture;
        _connection = fixture.CreateNodeHubConnection(fixture.AuthenticatedNodeId);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Start_delivers_recover_assignment_to_registered_owner()
    {
        var received = new TaskCompletionSource<RecoverAssignmentCommandMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await _connection.StartAsync();
        var nodeId = _fixture.AuthenticatedNodeId;
        await _connection.InvokeAsync<NodeDto>(
            "Register",
            new NodeRegistrationMessage(nodeId, "recovery-dispatch", "1.0.0", "{}"));

        using var client = _fixture.CreateClient();
        var projectId = await CreateProjectAsync(client);
        var seed = await SeedRunningAssignmentAsync(projectId, nodeId);
        var diagnosis = await GetDiagnosisAsync(client, projectId);

        _connection.On<RecoverAssignmentCommandMessage>(
            "RecoverAssignment",
            command =>
            {
                if (command.RequestId != seed.RequestId)
                {
                    return Task.CompletedTask;
                }

                received.TrySetResult(command);
                return Task.CompletedTask;
            });
        var started = await StartRecoveryAsync(client, projectId, diagnosis, "dispatch-start");
        var recoveryId = started.GetProperty("operation").GetProperty("id").GetGuid();

        var command = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(recoveryId, command.RecoveryId);
        Assert.Equal(1, command.Attempt);
        Assert.Equal(projectId, command.ProjectId);
        Assert.Equal(seed.RequestId, command.RequestId);
        Assert.Equal(seed.ClaimToken, command.ClaimToken);
        Assert.Equal(seed.BindingRevision, command.BindingRevision);
        Assert.NotEqual(default, command.Deadline);
    }

    [Fact]
    public async Task Register_redelivers_current_attempt()
    {
        using var client = _fixture.CreateClient();
        var projectId = await CreateProjectAsync(client);
        var seed = await SeedRunningAssignmentAsync(projectId, _fixture.AuthenticatedNodeId);
        var diagnosis = await GetDiagnosisAsync(client, projectId);
        var started = await StartRecoveryAsync(client, projectId, diagnosis, "reconnect-start");
        var recoveryId = started.GetProperty("operation").GetProperty("id").GetGuid();

        var received = new TaskCompletionSource<RecoverAssignmentCommandMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.On<RecoverAssignmentCommandMessage>(
            "RecoverAssignment",
            command =>
            {
                if (command.RecoveryId != recoveryId)
                {
                    return Task.CompletedTask;
                }

                received.TrySetResult(command);
                return Task.CompletedTask;
            });
        await _connection.StartAsync();
        await _connection.InvokeAsync<NodeDto>(
            "Register",
            new NodeRegistrationMessage(_fixture.AuthenticatedNodeId, "recovery-reconnect", "1.0.0", "{}"));

        // Register awaits dispatch enqueue; the client callback may run after InvokeAsync returns.
        var command = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(recoveryId, command.RecoveryId);
        Assert.Equal(seed.RequestId, command.RequestId);
        Assert.Equal(seed.ClaimToken, command.ClaimToken);
    }

    [Fact]
    public async Task Recovered_operation_is_not_commanded_on_register()
    {
        using var client = _fixture.CreateClient();
        var projectId = await CreateProjectAsync(client);
        await SeedRunningAssignmentAsync(projectId, _fixture.AuthenticatedNodeId);
        var diagnosis = await GetDiagnosisAsync(client, projectId);
        var started = await StartRecoveryAsync(client, projectId, diagnosis, "terminal-start");
        var recoveryId = started.GetProperty("operation").GetProperty("id").GetGuid();
        await SetOperationStatusAsync(recoveryId, nameof(RecoveryOperationStatus.Recovered));

        var received = false;
        _connection.On<RecoverAssignmentCommandMessage>("RecoverAssignment", command =>
        {
            if (command.RecoveryId == recoveryId)
            {
                received = true;
            }

            return Task.CompletedTask;
        });
        await _connection.StartAsync();
        await _connection.InvokeAsync<NodeDto>(
            "Register",
            new NodeRegistrationMessage(_fixture.AuthenticatedNodeId, "recovery-terminal", "1.0.0", "{}"));

        Assert.False(received);
    }

    private async Task<Seed> SeedRunningAssignmentAsync(Guid projectId, Guid nodeId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var project = await db.Projects.SingleAsync(candidate => candidate.Id == new ProjectId(projectId));
        var now = DateTimeOffset.UtcNow;
        var typedNodeId = new NodeId(nodeId);
        var repositoryPath = Path.Combine(_fixture.ApprovedRoot, $"recovery-dispatch-{projectId:N}");
        if (!await db.FleetNodes.AnyAsync(candidate => candidate.Id == typedNodeId))
        {
            db.FleetNodes.Add(FleetNode.Register(
                typedNodeId,
                $"recovery-dispatch-{nodeId:N}",
                "1.0.0",
                "{}",
                now));
        }

        var binding = WorkspaceBinding.Designate(project.Id, typedNodeId, repositoryPath, now);
        Assert.True(binding.ApplyValidationResult(
            typedNodeId,
            binding.ValidationRevision,
            WorkspaceBindingStatus.Valid,
            WorkspaceBinding.ValidValidationCode,
            "Seeded for recovery dispatch tests.",
            repositoryPath,
            now));
        var request = WorkRequest.Enqueue(
            project.Id,
            WorkRequestKind.Development,
            RequestPriority.Normal,
            RiskLevel.Standard,
            "Recovery dispatch request",
            "Exercise recovery command delivery.",
            now);
        request.Start(now);
        var claimToken = "recovery-dispatch-" + Guid.NewGuid().ToString("N");
        var assignment = ExecutionAssignment.Create(
            request.Id,
            project.Id,
            binding.Id,
            typedNodeId,
            repositoryPath,
            project.DefaultBranch,
            binding.ValidationRevision,
            claimToken,
            now,
            TimeSpan.FromMinutes(5));
        db.WorkspaceBindings.Add(binding);
        db.WorkRequests.Add(request);
        db.ExecutionAssignments.Add(assignment);
        await db.SaveChangesAsync();
        return new Seed(request.Id.Value, claimToken, assignment.BindingValidationRevisionSnapshot);
    }

    private async Task SetOperationStatusAsync(Guid recoveryId, string status)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var row = await db.Set<RecoveryOperationRow>().SingleAsync(candidate => candidate.Id == recoveryId);
        row.Status = status;
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> CreateProjectAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/projects",
            new
            {
                displayName = "Recovery dispatch project",
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
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"status {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<ProjectDto>(body, Json)!.Id;
    }

    private static async Task<JsonElement> GetDiagnosisAsync(HttpClient client, Guid projectId)
    {
        var response = await client.GetAsync($"/api/projects/{projectId}/recovery");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<JsonElement> StartRecoveryAsync(
        HttpClient client,
        Guid projectId,
        JsonElement diagnosis,
        string key)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/recoveries",
            new
            {
                inventoryRevision = diagnosis.GetProperty("inventoryRevision").GetString(),
                idempotencyKey = key,
            },
            Json);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return JsonDocument.Parse(body).RootElement;
    }

    private sealed record Seed(Guid RequestId, string ClaimToken, long BindingRevision);
}
