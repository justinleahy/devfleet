using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Reservations;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain.Reservations;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

/// <summary>
/// Exercises the browser-facing forced-release endpoint: it must reject unconfirmed
/// requests, and a confirmed force release must issue a fresh fencing token, record the
/// audit fact with the repository status snapshot, and release the lease.
/// </summary>
public sealed class ReservationsEndpointsTests : IClassFixture<ControlPlaneFixture>
{
    private readonly ControlPlaneFixture _fixture;
    private readonly HubFixture _hub;

    public ReservationsEndpointsTests(ControlPlaneFixture fixture)
    {
        _fixture = fixture;
        _hub = new HubFixture(fixture);
    }

    [Fact]
    public async Task ForceRelease_without_explicit_confirmation_is_rejected()
    {
        var projectId = await SeedProjectAsync();
        var lease = await AcquireAsync(projectId);

        using var client = _fixture.CreateClient();
        foreach (var confirm in new[] { false, (bool?)null })
        {
            var response = await client.PostAsJsonAsync(
                $"/api/reservations/{lease.LeaseId}/force-release",
                new
                {
                    projectId,
                    reason = "human intervention",
                    repositoryStatusSnapshot = "M src/A.cs",
                    confirm,
                });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var bodyText = await response.Content.ReadAsStringAsync();
            Assert.True(bodyText.Contains("Confirmation required"), $"body: {bodyText}");
        }

        // The lease must be untouched after every rejected attempt.
        var current = await GetLeaseAsync(projectId, lease.LeaseId);
        Assert.Equal((int)ReservationLeaseState.Active, current.State);
        Assert.Equal(lease.FencingToken, current.FencingToken);
    }

    [Fact]
    public async Task ForceRelease_missing_reason_or_snapshot_is_rejected()
    {
        var projectId = await SeedProjectAsync();
        var lease = await AcquireAsync(projectId);
        using var client = _fixture.CreateClient();

        var noReason = await client.PostAsJsonAsync(
            $"/api/reservations/{lease.LeaseId}/force-release",
            new { projectId, reason = "", repositoryStatusSnapshot = "M src/A.cs", confirm = true });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        var noSnapshot = await client.PostAsJsonAsync(
            $"/api/reservations/{lease.LeaseId}/force-release",
            new { projectId, reason = "human intervention", repositoryStatusSnapshot = "", confirm = true });
        Assert.Equal(HttpStatusCode.BadRequest, noSnapshot.StatusCode);
    }

    [Fact]
    public async Task Unknown_lease_returns_not_found()
    {
        using var client = _fixture.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/reservations/{Guid.NewGuid()}/force-release",
            new { projectId = Guid.NewGuid(), reason = "human intervention", repositoryStatusSnapshot = "clean", confirm = true });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Confirmed_force_release_releases_with_a_fresh_fencing_token_and_records_the_audit_fact()
    {
        var projectId = await SeedProjectAsync();
        var lease = await AcquireAsync(projectId);
        var originalToken = lease.FencingToken;

        using var client = _fixture.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/reservations/{lease.LeaseId}/force-release",
            new
            {
                projectId,
                reason = "human intervention",
                repositoryStatusSnapshot = "M src/A.cs\n?? bin/",
                requestedBy = "on-call operator",
                confirm = true,
            });
        Assert.True(response.IsSuccessStatusCode, $"status {(int)response.StatusCode}");
        var released = await response.Content.ReadFromJsonAsync<ReservationLeaseDto>();

        Assert.NotNull(released);
        Assert.Equal((int)ReservationLeaseState.Released, released!.State);
        Assert.NotNull(released.ReleasedAt);
        Assert.True(released.FencingToken > originalToken,
            $"fencing token must advance on force release: {originalToken} -> {released.FencingToken}");

        // Stale-token mutation authorization must now fail for the released lease.
        var stale = await _hub.Connection.InvokeAsync<MutationAuthorizationResultMessage>(
            "AuthorizeMutation",
            new MutationAuthorizationMessage(
                lease.LeaseId,
                originalToken,
                "session-a",
                "src/A.cs",
                (int)MutationOperation.Edit,
                nameof(MutationOperation.Edit)));
        Assert.False(stale.Authorized);
        Assert.NotNull(stale.Error);

        // The audit fact records the operator, reason, and repository status snapshot.
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var fact = await db.ReservationAuditFacts.AsNoTracking()
            .Where(f => f.LeaseId == lease.LeaseId && f.Kind == "ForceReleased")
            .OrderByDescending(f => f.AtUtcTicks)
            .FirstOrDefaultAsync();
        Assert.NotNull(fact);
        Assert.Equal("human intervention", fact!.Reason);
        Assert.Equal("on-call operator", fact.Actor);
        Assert.Equal("M src/A.cs\n?? bin/", fact.RepositoryStatusSnapshot);
    }

    private async Task<Guid> SeedProjectAsync()
    {
        var repositoryPath = _fixture.CreateGitRepository();
        using var client = _fixture.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/projects",
            new
            {
                displayName = "Reservation endpoints",
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
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private async Task<ReservationLeaseDto> AcquireAsync(Guid projectId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var reservations = scope.ServiceProvider.GetRequiredService<IReservationService>();
        return await reservations.AcquireAsync(
            new AcquireReservationCommand(
                projectId,
                Guid.NewGuid(),
                "session-a",
                [new ReservationScopeDto((int)ReservationScopeKind.File, nameof(ReservationScopeKind.File), "src/A.cs")],
                "acquired before force release"));
    }

    private async Task<ReservationLeaseDto> GetLeaseAsync(Guid projectId, Guid leaseId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var reservations = scope.ServiceProvider.GetRequiredService<IReservationService>();
        var leases = await reservations.ListAsync(projectId, includeReleased: true);
        return Assert.Single(leases, candidate => candidate.LeaseId == leaseId);
    }

    private sealed class HubFixture : IDisposable
    {
        public HubConnection Connection { get; }

        public HubFixture(ControlPlaneFixture fixture)
        {
            Connection = new HubConnectionBuilder()
                .WithUrl(
                    "http://server/nodeHub",
                    fixture.ConfigureNodeHub)
                .Build();
            Connection.StartAsync().GetAwaiter().GetResult();
        }

        public void Dispose() => Connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
