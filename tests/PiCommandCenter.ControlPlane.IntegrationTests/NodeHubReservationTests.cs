using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain.Reservations;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

/// <summary>
/// Exercises the reservation operations over a real /nodeHub SignalR connection: every
/// operation round-trips through the one-argument transport messages and returns the full
/// lease facts or a typed error envelope.
/// </summary>
public sealed class NodeHubReservationTests : IClassFixture<ControlPlaneFixture>, IDisposable
{
    private readonly ControlPlaneFixture _fixture;
    private readonly HubConnection _connection;
    private readonly Guid _projectId;
    private readonly Guid _requestId;
    private readonly string _sessionA = "session-a-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _sessionB = "session-b-" + Guid.NewGuid().ToString("N")[..8];

    public NodeHubReservationTests(ControlPlaneFixture fixture)
    {
        _fixture = fixture;
        _connection = new HubConnectionBuilder()
            .WithUrl(
                "http://server/nodeHub",
                options => { options.HttpMessageHandlerFactory = _ => fixture.Factory.Server.CreateHandler(); })
            .WithAutomaticReconnect()
            .Build();
        _connection.StartAsync().GetAwaiter().GetResult();

        _projectId = SeedProjectAsync().GetAwaiter().GetResult();
        _requestId = Guid.NewGuid();
    }

    public void Dispose() => _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Fact]
    public async Task Acquire_renew_expand_transfer_and_release_round_trip_with_full_lease_facts()
    {
        var acquire = await AcquireAsync(_sessionA, ["src/PiCommandCenter.Domain"]);
        Assert.Null(acquire.Error);
        var lease = acquire.Lease!;
        Assert.Equal(_projectId, lease.ProjectId);
        Assert.Equal(_requestId, lease.RequestId);
        Assert.Equal(_sessionA, lease.OwnerSessionId);
        Assert.Equal(1, lease.FencingToken);
        Assert.Equal((int)ReservationLeaseState.Active, lease.State);
        Assert.Equal(nameof(ReservationLeaseState.Active), lease.StateName);
        Assert.Null(lease.ReleasedAt);
        Assert.Equal("running the pi worker", lease.Reason);
        var scope = Assert.Single(lease.Scopes);
        Assert.Equal((int)ReservationScopeKind.Directory, scope.Kind);
        Assert.Equal(nameof(ReservationScopeKind.Directory), scope.KindName);
        Assert.Equal("src/PiCommandCenter.Domain/", scope.Path);
        Assert.True(lease.ExpiresAt > lease.AcquiredAt);

        var renew = await _connection.InvokeAsync<ReservationOperationResultMessage>(
            "RenewReservation",
            new ReservationMutationMessage(lease.LeaseId, lease.FencingToken, _sessionA));
        Assert.Null(renew.Error);
        Assert.Equal(lease.LeaseId, renew.Lease!.LeaseId);
        Assert.True(renew.Lease.ExpiresAt >= lease.ExpiresAt);

        var expand = await _connection.InvokeAsync<ReservationOperationResultMessage>(
            "ExpandReservation",
            new ExpandReservationMessage(
                lease.LeaseId,
                renew.Lease.FencingToken,
                _sessionA,
                [new ReservationScopeMessage((int)ReservationScopeKind.File, nameof(ReservationScopeKind.File), "src/PiCommandCenter.Domain/ReservationScope.cs")]));
        Assert.Null(expand.Error);
        Assert.Equal(2, expand.Lease!.Scopes.Length);

        var transfer = await _connection.InvokeAsync<ReservationOperationResultMessage>(
            "TransferReservation",
            new TransferReservationMessage(lease.LeaseId, _sessionA, _sessionB));
        Assert.Null(transfer.Error);
        Assert.Equal(_sessionB, transfer.Lease!.OwnerSessionId);
        Assert.True(transfer.Lease.FencingToken > renew.Lease.FencingToken);

        var release = await _connection.InvokeAsync<ReservationOperationResultMessage>(
            "ReleaseReservation",
            new ReleaseReservationMessage(lease.LeaseId, _sessionB));
        Assert.Null(release.Error);
        Assert.Equal((int)ReservationLeaseState.Released, release.Lease!.State);
        Assert.Equal(nameof(ReservationLeaseState.Released), release.Lease.StateName);
        Assert.NotNull(release.Lease.ReleasedAt);
    }

    [Fact]
    public async Task Acquiring_a_held_scope_reports_the_conflict_lease_and_owner()
    {
        var first = await AcquireAsync(_sessionA, ["src/PiCommandCenter.Application"]);
        Assert.Null(first.Error);

        var second = await AcquireAsync(_sessionB, ["src/PiCommandCenter.Application"]);
        Assert.Null(second.Lease);
        var error = second.Error!;
        Assert.Equal(ReservationErrorCodes.Conflict, error.Code);
        var conflict = Assert.Single(error.Conflicts);
        Assert.Equal(first.Lease!.LeaseId, conflict.LeaseId);
        Assert.Equal(_sessionA, conflict.OwnerSessionId);
        Assert.Equal("src/PiCommandCenter.Application/", conflict.ScopePath);
    }

    [Fact]
    public async Task AuthorizeMutation_accepts_covered_mutations_and_rejects_stale_tokens()
    {
        var acquire = await AcquireAsync(_sessionA, ["src/PiCommandCenter.Contracts"]);
        var lease = acquire.Lease!;

        var ok = await _connection.InvokeAsync<MutationAuthorizationResultMessage>(
            "AuthorizeMutation",
            new MutationAuthorizationMessage(
                lease.LeaseId,
                lease.FencingToken,
                _sessionA,
                "src/PiCommandCenter.Contracts/ProtocolVersion.cs",
                (int)MutationOperation.Edit,
                nameof(MutationOperation.Edit)));
        Assert.True(ok.Authorized);
        Assert.Null(ok.Error);

        var stale = await _connection.InvokeAsync<MutationAuthorizationResultMessage>(
            "AuthorizeMutation",
            new MutationAuthorizationMessage(
                lease.LeaseId,
                lease.FencingToken + 100,
                _sessionA,
                "src/PiCommandCenter.Contracts/ProtocolVersion.cs",
                (int)MutationOperation.Edit,
                nameof(MutationOperation.Edit)));
        Assert.False(stale.Authorized);
        Assert.Equal(ReservationErrorCodes.InvalidFencingToken, stale.Error!.Code);

        var outside = await _connection.InvokeAsync<MutationAuthorizationResultMessage>(
            "AuthorizeMutation",
            new MutationAuthorizationMessage(
                lease.LeaseId,
                lease.FencingToken,
                _sessionA,
                "src/PiCommandCenter.Application/Uncovered.cs",
                (int)MutationOperation.Edit,
                nameof(MutationOperation.Edit)));
        Assert.False(outside.Authorized);
        Assert.NotNull(outside.Error);
    }

    [Fact]
    public async Task ListReservations_returns_every_lease_fact()
    {
        var acquire = await AcquireAsync(_sessionA, ["docs"]);
        Assert.Null(acquire.Error);

        var listed = await _connection.InvokeAsync<ReservationLeaseMessage[]>(
            "ListReservations",
            new ListReservationsMessage(_projectId, IncludeReleased: true));
        var lease = Assert.Single(listed, candidate => candidate.LeaseId == acquire.Lease!.LeaseId);
        Assert.Equal(_sessionA, lease.OwnerSessionId);
        Assert.Equal(lease.FencingToken, acquire.Lease!.FencingToken);
        Assert.Equal(nameof(ReservationLeaseState.Active), lease.StateName);
        Assert.Single(lease.Scopes, scope => scope.Path == "docs/");
    }

    [Fact]
    public async Task Unknown_leases_map_to_typed_not_found_and_premature_recovery_maps_to_invalid_state()
    {
        var unknown = await _connection.InvokeAsync<ReservationOperationResultMessage>(
            "RenewReservation",
            new ReservationMutationMessage(Guid.NewGuid(), 1, _sessionA));
        Assert.Null(unknown.Lease);
        Assert.Equal(ReservationErrorCodes.NotFound, unknown.Error!.Code);

        var acquire = await AcquireAsync(_sessionA, ["tests/PiCommandCenter.Domain.Tests"]);
        var lease = acquire.Lease!;
        var premature = await _connection.InvokeAsync<ReservationOperationResultMessage>(
            "MarkReservationRecovery",
            new MarkRecoveryMessage(lease.LeaseId, "node crashed mid-mutation"));
        Assert.Null(premature.Lease);
        Assert.Equal(ReservationErrorCodes.InvalidState, premature.Error!.Code);
    }

    private Task<ReservationOperationResultMessage> AcquireAsync(string sessionId, string[] paths) =>
        _connection.InvokeAsync<ReservationOperationResultMessage>(
            "AcquireReservation",
            new AcquireReservationMessage(
                _projectId,
                _requestId,
                sessionId,
                paths.Select(path => new ReservationScopeMessage(
                    (int)ReservationScopeKind.Directory,
                    nameof(ReservationScopeKind.Directory),
                    path)).ToArray(),
                "running the pi worker"));

    private async Task<Guid> SeedProjectAsync()
    {
        var repositoryPath = _fixture.CreateGitRepository();
        using var client = _fixture.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/projects",
            new
            {
                displayName = "Reservation transport",
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
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"status {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }
}
