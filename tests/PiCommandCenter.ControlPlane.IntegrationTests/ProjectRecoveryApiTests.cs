using System.Net;
using PiCommandCenter.Domain.Reservations;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Application.Recovery;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Recovery;
using PiCommandCenter.Infrastructure.Reservations;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

public sealed class ProjectRecoveryApiTests : IClassFixture<ControlPlaneFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ControlPlaneFixture _fixture;

    public ProjectRecoveryApiTests(ControlPlaneFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Diagnosis_for_an_empty_project_has_no_hold_or_targets()
    {
        using var client = _fixture.CreateClient();
        var projectId = await CreateProjectAsync(client);

        var response = await client.GetAsync($"/api/projects/{projectId}/recovery");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var diagnosis = JsonDocument.Parse(body).RootElement;
        Assert.Equal(projectId, diagnosis.GetProperty("projectId").GetGuid());
        Assert.False(diagnosis.GetProperty("holdPresent").GetBoolean());
        Assert.Equal(JsonValueKind.Null, diagnosis.GetProperty("holdOperationId").ValueKind);
        Assert.Equal(JsonValueKind.Null, diagnosis.GetProperty("latestOperation").ValueKind);
        Assert.Equal(0, diagnosis.GetProperty("nonterminalAssignments").GetArrayLength());
        Assert.Equal(0, diagnosis.GetProperty("unresolvedReservations").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(diagnosis.GetProperty("inventoryRevision").GetString()));
        Assert.DoesNotContain("claimToken", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Diagnosis_includes_retained_node_and_reservation_facts_without_secrets()
    {
        using var client = _fixture.CreateClient();
        var projectId = await CreateProjectAsync(client);
        var seeded = await SeedRunningAssignmentAsync(projectId);
        await SeedReservationAsync(projectId, seeded.RequestId);

        var response = await client.GetAsync($"/api/projects/{projectId}/recovery");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("claimToken", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recovery-api-claim-token", body, StringComparison.Ordinal);
        var diagnosis = JsonDocument.Parse(body).RootElement;
        var assignment = diagnosis.GetProperty("nonterminalAssignments")[0];
        Assert.Equal(seeded.RequestId, assignment.GetProperty("requestId").GetGuid());
        Assert.Equal(seeded.NodeId, assignment.GetProperty("assignedNodeId").GetGuid());
        Assert.Equal(seeded.DisplayName, assignment.GetProperty("assignedNodeDisplayName").GetString());
        Assert.Equal(seeded.RepositoryPath, assignment.GetProperty("canonicalRepositoryPath").GetString());
        Assert.Equal(nameof(NodeStatus.Offline), assignment.GetProperty("nodeStatus").GetString());
        Assert.True(assignment.TryGetProperty("assignedAt", out _));
        Assert.True(assignment.TryGetProperty("leaseExpiresAt", out _));
        Assert.True(assignment.TryGetProperty("nodeLastContact", out _));
        var reservation = diagnosis.GetProperty("unresolvedReservations")[0];
        Assert.Equal(seeded.RequestId, reservation.GetProperty("requestId").GetGuid());
        Assert.Equal("api-owner", reservation.GetProperty("ownerSessionId").GetString());
        Assert.Equal("held for recovery", reservation.GetProperty("reason").GetString());
        Assert.True(reservation.TryGetProperty("expiresAt", out var expiresAt));
        Assert.NotEqual(JsonValueKind.Null, expiresAt.ValueKind);

        var compactRevision = ProjectRecoveryInventory.ComputeRevision(
            diagnosis.GetProperty("projectVersion").GetInt64(),
            [
                new ProjectRecoveryAssignmentSnapshot(
                    new WorkRequestId(seeded.RequestId),
                    assignment.GetProperty("version").GetInt64(),
                    assignment.GetProperty("state").GetString()!,
                    assignment.GetProperty("bindingRevision").GetInt64()),
            ],
            [
                new ProjectRecoveryReservationSnapshot(
                    reservation.GetProperty("leaseId").GetGuid(),
                    reservation.GetProperty("version").GetInt64(),
                    reservation.GetProperty("state").GetString()!),
            ]);
        Assert.Equal(compactRevision, diagnosis.GetProperty("inventoryRevision").GetString());
    }


    [Fact]
    public async Task Start_on_empty_inventory_is_accepted_with_location_to_diagnosis()
    {
        using var client = _fixture.CreateClient();
        var projectId = await CreateProjectAsync(client);
        var diagnosis = await GetDiagnosisAsync(client, projectId);

        var response = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/recoveries",
            new
            {
                inventoryRevision = diagnosis.GetProperty("inventoryRevision").GetString(),
                idempotencyKey = "empty-start",
            },
            Json);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal($"/api/projects/{projectId}/recovery", response.Headers.Location?.ToString());
        var started = JsonDocument.Parse(body).RootElement;
        Assert.True(started.GetProperty("noOp").GetBoolean());
        Assert.Equal(JsonValueKind.Null, started.GetProperty("operation").ValueKind);
    }

    [Fact]
    public async Task Start_with_inventory_is_accepted_with_location_to_the_operation()
    {
        using var client = _fixture.CreateClient();
        var projectId = await CreateProjectAsync(client);
        await SeedRunningAssignmentAsync(projectId);
        var diagnosis = await GetDiagnosisAsync(client, projectId);

        var response = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/recoveries",
            StartBody(diagnosis, "start-key"),
            Json);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var started = JsonDocument.Parse(body).RootElement;
        Assert.False(started.GetProperty("noOp").GetBoolean());
        var operation = started.GetProperty("operation");
        var recoveryId = operation.GetProperty("id").GetGuid();
        Assert.Equal(
            $"/api/projects/{projectId}/recoveries/{recoveryId}",
            response.Headers.Location?.ToString());
        Assert.Equal("Running", operation.GetProperty("status").GetString());
        Assert.DoesNotContain("claimToken", body, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("spoofed-actor", operation.GetProperty("actor").GetString());

        var get = await client.GetAsync($"/api/projects/{projectId}/recoveries/{recoveryId}");
        var getBody = await get.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(recoveryId, JsonDocument.Parse(getBody).RootElement.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Stale_inventory_and_unresolved_operation_are_conflicts()
    {
        using var client = _fixture.CreateClient();
        var projectId = await CreateProjectAsync(client);
        await SeedRunningAssignmentAsync(projectId);
        var diagnosis = await GetDiagnosisAsync(client, projectId);

        var stale = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/recoveries",
            new
            {
                inventoryRevision = "deadbeef",
                idempotencyKey = "stale-key",
            },
            Json);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(
            "Recovery inventory conflict",
            JsonDocument.Parse(await stale.Content.ReadAsStringAsync()).RootElement.GetProperty("title").GetString());

        var first = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/recoveries",
            StartBody(diagnosis, "key-a"),
            Json);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/recoveries",
            StartBody(diagnosis, "key-b"),
            Json);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(
            "Recovery operation conflict",
            JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement.GetProperty("title").GetString());

        var reused = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/recoveries",
            StartBody(diagnosis, "key-a"),
            Json);
        Assert.Equal(HttpStatusCode.Accepted, reused.StatusCode);
        var firstId = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement
            .GetProperty("operation").GetProperty("id").GetGuid();
        var replayedId = JsonDocument.Parse(await reused.Content.ReadAsStringAsync()).RootElement
            .GetProperty("operation").GetProperty("id").GetGuid();
        Assert.Equal(firstId, replayedId);
    }

    [Fact]
    public async Task Recheck_returns_the_operation_and_rejects_stale_versions()
    {
        using var client = _fixture.CreateClient();
        var projectId = await CreateProjectAsync(client);
        await SeedRunningAssignmentAsync(projectId);
        var diagnosis = await GetDiagnosisAsync(client, projectId);
        var started = await StartAsync(client, projectId, diagnosis, "recheck-start");
        var operation = started.GetProperty("operation");
        var recoveryId = operation.GetProperty("id").GetGuid();
        var version = operation.GetProperty("version").GetInt64();
        await SetOperationStatusAsync(recoveryId, nameof(RecoveryOperationStatus.NeedsIntervention));

        var recheck = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/recoveries/{recoveryId}/recheck",
            new { expectedOperationVersion = version, idempotencyKey = "recheck-key" },
            Json);
        var recheckBody = await recheck.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, recheck.StatusCode);
        var rechecked = JsonDocument.Parse(recheckBody).RootElement;
        Assert.Equal("Running", rechecked.GetProperty("status").GetString());
        Assert.Equal(2, rechecked.GetProperty("attempt").GetInt32());

        var stale = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/recoveries/{recoveryId}/recheck",
            new { expectedOperationVersion = version, idempotencyKey = "other-recheck" },
            Json);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(
            "Recovery revision conflict",
            JsonDocument.Parse(await stale.Content.ReadAsStringAsync()).RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Confirm_manual_recovers_while_retaining_hold_and_rejects_stale_versions()
    {
        using var client = _fixture.CreateClient();
        var projectId = await CreateProjectAsync(client);
        await SeedRunningAssignmentAsync(projectId);
        var diagnosis = await GetDiagnosisAsync(client, projectId);
        var started = await StartAsync(client, projectId, diagnosis, "confirm-manual-start");
        var operation = started.GetProperty("operation");
        var recoveryId = operation.GetProperty("id").GetGuid();
        var version = operation.GetProperty("version").GetInt64();
        var attempt = operation.GetProperty("attempt").GetInt32();
        await SetOperationStatusAsync(recoveryId, nameof(RecoveryOperationStatus.NeedsIntervention));

        var stale = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/recoveries/{recoveryId}/confirm-manual",
            ConfirmManualBody(version + 9, attempt, "stale-manual"),
            Json);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(
            "Recovery revision conflict",
            JsonDocument.Parse(await stale.Content.ReadAsStringAsync()).RootElement.GetProperty("title").GetString());

        var confirm = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/recoveries/{recoveryId}/confirm-manual",
            ConfirmManualBody(version, attempt, "confirm-manual-key"),
            Json);
        var confirmBody = await confirm.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var confirmed = JsonDocument.Parse(confirmBody).RootElement;
        Assert.Equal("Recovered", confirmed.GetProperty("status").GetString());
        Assert.NotEqual("spoofed-actor", confirmed.GetProperty("actor").GetString());
        Assert.DoesNotContain("claimToken", confirmBody, StringComparison.OrdinalIgnoreCase);

        var after = await GetDiagnosisAsync(client, projectId);
        Assert.True(after.GetProperty("holdPresent").GetBoolean());
        Assert.Equal(recoveryId, after.GetProperty("holdOperationId").GetGuid());
    }

    [Fact]
    public async Task Resume_rejects_unready_and_stale_holds()
    {
        using var client = _fixture.CreateClient();
        var projectId = await CreateProjectAsync(client);
        await SeedRunningAssignmentAsync(projectId);
        var diagnosis = await GetDiagnosisAsync(client, projectId);
        var started = await StartAsync(client, projectId, diagnosis, "resume-start");
        var recoveryId = started.GetProperty("operation").GetProperty("id").GetGuid();
        await SetOperationStatusAsync(recoveryId, nameof(RecoveryOperationStatus.Recovered));
        var after = await GetDiagnosisAsync(client, projectId);
        var holdVersion = after.GetProperty("holdVersion").GetInt64();

        var unready = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/recovery/resume",
            new { operationId = recoveryId, expectedHoldVersion = holdVersion },
            Json);
        Assert.Equal(HttpStatusCode.Conflict, unready.StatusCode);
        Assert.Equal(
            "Recovery not ready",
            JsonDocument.Parse(await unready.Content.ReadAsStringAsync()).RootElement.GetProperty("title").GetString());

        var stale = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/recovery/resume",
            new { operationId = recoveryId, expectedHoldVersion = holdVersion + 9 },
            Json);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(
            "Recovery revision conflict",
            JsonDocument.Parse(await stale.Content.ReadAsStringAsync()).RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Missing_project_and_recovery_are_not_found()
    {
        using var client = _fixture.CreateClient();
        var missingProject = Guid.NewGuid();
        var missingRecovery = Guid.NewGuid();

        var diagnosis = await client.GetAsync($"/api/projects/{missingProject}/recovery");
        Assert.Equal(HttpStatusCode.NotFound, diagnosis.StatusCode);

        var projectId = await CreateProjectAsync(client);
        var get = await client.GetAsync($"/api/projects/{projectId}/recoveries/{missingRecovery}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(
            "Recovery not found",
            JsonDocument.Parse(await get.Content.ReadAsStringAsync()).RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Unauthorized_post_and_legacy_csrf_failure_are_rejected()
    {
        using var anonymous = _fixture.CreateAnonymousClient();
        var projectId = Guid.NewGuid();
        var recoveryId = Guid.NewGuid();
        var anonymousPost = await anonymous.PostAsJsonAsync(
            $"/api/projects/{projectId}/recoveries",
            new
            {
                inventoryRevision = "x",
                idempotencyKey = "anon",
            },
            Json);
        Assert.True(
            anonymousPost.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized,
            anonymousPost.StatusCode.ToString());

        var anonymousConfirm = await anonymous.PostAsJsonAsync(
            $"/api/projects/{projectId}/recoveries/{recoveryId}/confirm-manual",
            ConfirmManualBody(1, 1, "anon-manual"),
            Json);
        Assert.True(
            anonymousConfirm.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized,
            anonymousConfirm.StatusCode.ToString());

        using var native = _fixture.CreateNativeClient();
        var nativePost = await native.PostAsJsonAsync(
            $"/api/v1/projects/{projectId}/recoveries",
            new
            {
                inventoryRevision = "x",
                idempotencyKey = "native-anon",
            },
            Json);
        Assert.Equal(HttpStatusCode.Unauthorized, nativePost.StatusCode);

        var nativeConfirm = await native.PostAsJsonAsync(
            $"/api/v1/projects/{projectId}/recoveries/{recoveryId}/confirm-manual",
            ConfirmManualBody(1, 1, "native-anon-manual"),
            Json);
        Assert.Equal(HttpStatusCode.Unauthorized, nativeConfirm.StatusCode);

        using var cookie = _fixture.CreateAuthenticatedClient();
        cookie.DefaultRequestHeaders.Remove("RequestVerificationToken");
        var csrf = await cookie.PostAsJsonAsync(
            $"/api/projects/{projectId}/recoveries",
            new
            {
                inventoryRevision = "x",
                idempotencyKey = "csrf",
            },
            Json);
        Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);

        var csrfConfirm = await cookie.PostAsJsonAsync(
            $"/api/projects/{projectId}/recoveries/{recoveryId}/confirm-manual",
            ConfirmManualBody(1, 1, "csrf-manual"),
            Json);
        Assert.Equal(HttpStatusCode.BadRequest, csrfConfirm.StatusCode);
    }

    private static object StartBody(JsonElement diagnosis, string key) => new
    {
        inventoryRevision = diagnosis.GetProperty("inventoryRevision").GetString(),
        actor = "spoofed-actor",
        idempotencyKey = key,
    };

    private static object ConfirmManualBody(long expectedOperationVersion, int expectedAttempt, string key) => new
    {
        expectedOperationVersion,
        expectedAttempt,
        exactProjectName = "Recovery project",
        actor = "spoofed-actor",
        idempotencyKey = key,
        confirmOriginalExecutionCannotResume = true,
        writerAccessPrevented = true,
        acknowledgeEvidenceGaps = true,
        processStopEvidence = "stopped assignment trees at 2026-09-06T12:00:00Z; descendants excluded",
        repositoryStatusSnapshot = "dirty worktree; HEAD main; owning workspace /repo",
        repositoryStatusSource = "administrator inspection of owning workspace",
        repositoryCollectedAt = DateTimeOffset.UtcNow,
        reservationAndEventGapAccounting = "lease captured; spool gap acknowledged",
    };

    private static async Task<Guid> CreateProjectAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/projects",
            new
            {
                displayName = "Recovery project",
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

    private static async Task<JsonElement> StartAsync(
        HttpClient client,
        Guid projectId,
        JsonElement diagnosis,
        string key)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/recoveries",
            StartBody(diagnosis, key),
            Json);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return JsonDocument.Parse(body).RootElement;
    }

    private async Task<SeededAssignment> SeedRunningAssignmentAsync(Guid projectId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var project = await db.Projects.SingleAsync(candidate => candidate.Id == new ProjectId(projectId));
        var now = DateTimeOffset.UtcNow;
        var nodeId = NodeId.New();
        var repositoryPath = Path.Combine(_fixture.ApprovedRoot, $"recovery-{projectId:N}");
        var displayName = $"recovery-node-{projectId:N}";
        var node = FleetNode.Register(nodeId, displayName, "1.0.0", "{}", now);
        var binding = WorkspaceBinding.Designate(project.Id, nodeId, repositoryPath, now);
        Assert.True(binding.ApplyValidationResult(
            nodeId,
            binding.ValidationRevision,
            WorkspaceBindingStatus.Valid,
            WorkspaceBinding.ValidValidationCode,
            "Seeded for recovery API tests.",
            repositoryPath,
            now));
        var request = WorkRequest.Enqueue(
            project.Id,
            WorkRequestKind.Development,
            RequestPriority.Normal,
            RiskLevel.Standard,
            "Recovery API request",
            "Exercise recovery HTTP.",
            now);
        request.Start(now);
        var assignment = ExecutionAssignment.Create(
            request.Id,
            project.Id,
            binding.Id,
            nodeId,
            repositoryPath,
            project.DefaultBranch,
            binding.ValidationRevision,
            "recovery-api-claim-token",
            now,
            TimeSpan.FromMinutes(5));
        db.FleetNodes.Add(node);
        db.WorkspaceBindings.Add(binding);
        db.WorkRequests.Add(request);
        db.ExecutionAssignments.Add(assignment);
        await db.SaveChangesAsync();
        return new SeededAssignment(request.Id.Value, nodeId.Value, displayName, repositoryPath);
    }

    private async Task SeedReservationAsync(Guid projectId, Guid requestId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.Set<ReservationLeaseRow>().Add(new ReservationLeaseRow
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            RequestId = requestId,
            OwnerSessionId = "api-owner",
            Reason = "held for recovery",
            FencingToken = 1,
            State = nameof(ReservationLeaseState.Active),
            AcquiredAtUtcTicks = now.UtcTicks,
            LastRenewedAtUtcTicks = now.UtcTicks,
            ExpiresAtUtcTicks = now.AddMinutes(3).UtcTicks,
            Version = 1,
        });
        await db.SaveChangesAsync();
    }

    private sealed record SeededAssignment(
        Guid RequestId,
        Guid NodeId,
        string DisplayName,
        string RepositoryPath);

    private async Task SetOperationStatusAsync(Guid recoveryId, string status)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var row = await db.Set<RecoveryOperationRow>().SingleAsync(candidate => candidate.Id == recoveryId);
        row.Status = status;
        await db.SaveChangesAsync();
    }
}
