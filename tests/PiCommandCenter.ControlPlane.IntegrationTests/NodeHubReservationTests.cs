using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using PiCommandCenter.Contracts.NodeTransport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Infrastructure.Persistence;
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
    private readonly string _claimToken;
    private readonly string _sessionA = "session-a-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _sessionB = "session-b-" + Guid.NewGuid().ToString("N")[..8];

    public NodeHubReservationTests(ControlPlaneFixture fixture)
    {
        _fixture = fixture;
        _connection = new HubConnectionBuilder()
            .WithUrl(
                "http://server/nodeHub",
                fixture.ConfigureNodeHub)
            .WithAutomaticReconnect()
            .Build();
        _connection.StartAsync().GetAwaiter().GetResult();
        _ = _connection.InvokeAsync<NodeDto>(
            "Register",
            new NodeRegistrationMessage(fixture.AuthenticatedNodeId, "pi-hub-reservations", "1.0.0", "{}"))
            .GetAwaiter().GetResult();

        (_projectId, _requestId, _claimToken) = SeedAssignmentAsync().GetAwaiter().GetResult();
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
            new ReservationMutationMessage(
                _projectId, _requestId, _claimToken, lease.LeaseId, lease.FencingToken, _sessionA));
        Assert.Null(renew.Error);
        Assert.Equal(lease.LeaseId, renew.Lease!.LeaseId);
        Assert.True(renew.Lease.ExpiresAt >= lease.ExpiresAt);

        var expand = await _connection.InvokeAsync<ReservationOperationResultMessage>(
            "ExpandReservation",
            new ExpandReservationMessage(
                _projectId,
                _requestId,
                _claimToken,
                lease.LeaseId,
                renew.Lease.FencingToken,
                _sessionA,
                [new ReservationScopeMessage((int)ReservationScopeKind.File, nameof(ReservationScopeKind.File), "src/PiCommandCenter.Domain/ReservationScope.cs")]));
        Assert.Null(expand.Error);
        Assert.Equal(2, expand.Lease!.Scopes.Length);

        var transfer = await _connection.InvokeAsync<ReservationOperationResultMessage>(
            "TransferReservation",
            new TransferReservationMessage(
                _projectId, _requestId, _claimToken, lease.LeaseId, _sessionA, _sessionB));
        Assert.Null(transfer.Error);
        Assert.Equal(_sessionB, transfer.Lease!.OwnerSessionId);
        Assert.True(transfer.Lease.FencingToken > renew.Lease.FencingToken);

        var release = await _connection.InvokeAsync<ReservationOperationResultMessage>(
            "ReleaseReservation",
            new ReleaseReservationMessage(
                _projectId, _requestId, _claimToken, lease.LeaseId, _sessionB));
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
                _projectId,
                _requestId,
                _claimToken,
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
                _projectId,
                _requestId,
                _claimToken,
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
                _projectId,
                _requestId,
                _claimToken,
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
            new ListReservationsMessage(_projectId, _requestId, _claimToken, IncludeReleased: true));
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
            new ReservationMutationMessage(
                _projectId, _requestId, _claimToken, Guid.NewGuid(), 1, _sessionA));
        Assert.Null(unknown.Lease);
        Assert.Equal(ReservationErrorCodes.NotFound, unknown.Error!.Code);

        var acquire = await AcquireAsync(_sessionA, ["tests/PiCommandCenter.Domain.Tests"]);
        var lease = acquire.Lease!;
        var premature = await _connection.InvokeAsync<ReservationOperationResultMessage>(
            "MarkReservationRecovery",
            new MarkRecoveryMessage(
                _projectId, _requestId, _claimToken, lease.LeaseId, "node crashed mid-mutation"));
        Assert.Null(premature.Lease);
        Assert.Equal(ReservationErrorCodes.InvalidState, premature.Error!.Code);
    }

    [Fact]
    public async Task Assignment_fence_rejects_foreign_node_token_project_request_and_session()
    {
        var valid = new AcquireReservationMessage(
            _projectId,
            _requestId,
            _claimToken,
            _sessionA,
            [new ReservationScopeMessage((int)ReservationScopeKind.Directory, nameof(ReservationScopeKind.Directory), "src")],
            "authorized");

        await AssertDeniedAsync(valid with { ClaimToken = "foreign-secret-token" }, "token_mismatch");
        await AssertDeniedAsync(valid with { ProjectId = Guid.NewGuid() }, "project_mismatch");
        await AssertDeniedAsync(valid with { RequestId = Guid.NewGuid() }, "assignment_missing");
        await AssertDeniedAsync(valid with { OwnerSessionId = "foreign-session" }, "session_mismatch");

        await using var foreignConnection = _fixture.CreateNodeHubConnection(_fixture.SecondaryNodeId);
        await foreignConnection.StartAsync();
        _ = await foreignConnection.InvokeAsync<NodeDto>(
            "Register",
            new NodeRegistrationMessage(_fixture.SecondaryNodeId, "foreign-node", "1.0.0", "{}"));
        var foreignNodeError = await Assert.ThrowsAnyAsync<HubException>(() =>
            foreignConnection.InvokeAsync<ReservationOperationResultMessage>("AcquireReservation", valid));
        Assert.Contains("node_mismatch", foreignNodeError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_claimToken, foreignNodeError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recovery_required_assignment_cannot_mutate_reservations()
    {
        await SetAssignmentStateAsync(assignment => assignment.MarkRecoveryRequired(DateTimeOffset.UtcNow));

        var error = await Assert.ThrowsAnyAsync<HubException>(() =>
            _connection.InvokeAsync<ReservationLeaseMessage[]>(
                "ListReservations",
                new ListReservationsMessage(_projectId, _requestId, _claimToken, IncludeReleased: true)));

        Assert.Contains("state_forbidden", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Terminal_assignment_cannot_mutate_reservations()
    {
        await SetAssignmentStateAsync(assignment =>
        {
            var now = DateTimeOffset.UtcNow;
            assignment.MarkRunning(now);
            assignment.BeginFinalizing(now);
            assignment.Complete(now);
        });

        var error = await Assert.ThrowsAnyAsync<HubException>(() =>
            _connection.InvokeAsync<ReservationOperationResultMessage>(
                "AcquireReservation",
                new AcquireReservationMessage(
                    _projectId,
                    _requestId,
                    _claimToken,
                    _sessionA,
                    [new ReservationScopeMessage((int)ReservationScopeKind.Directory, nameof(ReservationScopeKind.Directory), "src")],
                    "terminal mutation")));

        Assert.Contains("state_forbidden", error.Message, StringComparison.Ordinal);
    }

    private async Task AssertDeniedAsync(AcquireReservationMessage message, string code)
    {
        var error = await Assert.ThrowsAnyAsync<HubException>(() =>
            _connection.InvokeAsync<ReservationOperationResultMessage>("AcquireReservation", message));
        Assert.Contains(code, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(message.ClaimToken, error.Message, StringComparison.Ordinal);
    }

    private async Task SetAssignmentStateAsync(Action<ExecutionAssignment> transition)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var assignment = await db.ExecutionAssignments.SingleAsync(
            candidate => candidate.RequestId == new WorkRequestId(_requestId));
        transition(assignment);
        await db.SaveChangesAsync();
    }

    private Task<ReservationOperationResultMessage> AcquireAsync(string sessionId, string[] paths) =>
        _connection.InvokeAsync<ReservationOperationResultMessage>(
            "AcquireReservation",
            new AcquireReservationMessage(
                _projectId,
                _requestId,
                _claimToken,
                sessionId,
                paths.Select(path => new ReservationScopeMessage(
                    (int)ReservationScopeKind.Directory,
                    nameof(ReservationScopeKind.Directory),
                    path)).ToArray(),
                "running the pi worker"));

    private async Task<(Guid ProjectId, Guid RequestId, string ClaimToken)> SeedAssignmentAsync()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var now = DateTimeOffset.UtcNow;
        var nodeId = new NodeId(_fixture.AuthenticatedNodeId);
        var project = Project.Register(
            "Reservation transport " + Guid.NewGuid().ToString("N")[..6],
            "main", enabled: true, maxActiveWriteRequests: 2, maxReadOnlyRequests: 4,
            maxChildAgentsPerRequest: 2, requireCleanStart: false, createRequestBranch: false,
            createRequestCommit: false, autoMerge: false, now);
        var repositoryPath = _fixture.CreateGitRepository();
        var binding = WorkspaceBinding.Designate(project.Id, nodeId, repositoryPath, now);
        Assert.True(binding.ApplyValidationResult(
            nodeId,
            binding.ValidationRevision,
            WorkspaceBindingStatus.Valid,
            WorkspaceBinding.ValidValidationCode,
            "Seeded for reservation hub tests.",
            repositoryPath,
            now));
        var request = WorkRequest.Enqueue(
            project.Id,
            WorkRequestKind.Development,
            RequestPriority.Normal,
            RiskLevel.Standard,
            "Reservation transport",
            "Exercise reservation operations.",
            now);
        request.Start(now);
        var claimToken = "reservation-hub-" + Guid.NewGuid().ToString("N");
        var assignment = ExecutionAssignment.Create(
            request.Id,
            project.Id,
            binding.Id,
            nodeId,
            binding.CanonicalRepositoryPath!,
            project.DefaultBranch,
            binding.ValidationRevision,
            claimToken,
            now,
            TimeSpan.FromMinutes(5));

        db.Projects.Add(project);
        db.WorkspaceBindings.Add(binding);
        db.WorkRequests.Add(request);
        db.ExecutionAssignments.Add(assignment);
        SeedSession(db, project.Id.Value, request.Id.Value, _sessionA, now);
        SeedSession(db, project.Id.Value, request.Id.Value, _sessionB, now);
        await db.SaveChangesAsync();
        return (project.Id.Value, request.Id.Value, claimToken);
    }

    private static void SeedSession(
        ControlPlaneDbContext db,
        Guid projectId,
        Guid requestId,
        string sessionId,
        DateTimeOffset now) =>
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = sessionId,
            ProjectId = projectId,
            RequestId = requestId,
            AgentName = sessionId,
            Role = "implementer",
            Runtime = "pi",
            Model = "codex/gpt-5.6-sol",
            Liveness = nameof(AgentLiveness.Online),
            Activity = nameof(AgentActivity.Idle),
            Attention = "None",
            WorkState = nameof(AgentWorkState.Executing),
            StatusReason = "Seeded for reservation hub tests",
            StartedAtUtcTicks = now.UtcTicks,
            Version = 1,
        });
}
