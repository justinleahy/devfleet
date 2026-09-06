using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Child;

namespace PiCommandCenter.Node.Tests;

public sealed class NodeTransportReservationGatewayTests
{
    [Fact]
    public async Task Acquire_and_cached_lease_operations_propagate_assignment_credential()
    {
        var credential = Credential();
        var source = SourceWith(credential);
        var transport = new RecordingReservationTransport();
        var leaseId = Guid.NewGuid();
        transport.AcquireResult = Success(Lease(
            leaseId,
            credential.ProjectId,
            credential.RequestId));
        var gateway = new NodeTransportReservationGateway(transport, source);

        await gateway.AcquireAsync(
            credential.ProjectId,
            credential.RequestId,
            "owner-session",
            [new ReservationScopeSpec("directory", "/workspace")],
            "editing",
            CancellationToken.None);
        await gateway.RenewAsync(leaseId, 11, "owner-session", CancellationToken.None);
        await gateway.TransferAsync(
            leaseId,
            "owner-session",
            "next-session",
            CancellationToken.None);
        await gateway.AuthorizeAsync(
            leaseId,
            11,
            "next-session",
            "/workspace/file.cs",
            "write",
            CancellationToken.None);
        await gateway.MarkRecoveryRequiredAsync(
            leaseId,
            "transport interrupted",
            CancellationToken.None);

        AssertCredential(Assert.Single(transport.Acquires), credential);
        AssertCredential(Assert.Single(transport.Renewals), credential);
        AssertCredential(Assert.Single(transport.Transfers), credential);
        AssertCredential(Assert.Single(transport.Authorizations), credential);
        AssertCredential(Assert.Single(transport.Recoveries), credential);
    }

    [Fact]
    public async Task Project_operations_and_list_cached_lease_propagate_assignment_credential()
    {
        var credential = Credential();
        var source = SourceWith(credential);
        var transport = new RecordingReservationTransport();
        var listedLeaseId = Guid.NewGuid();
        transport.ListResult =
        [
            Lease(listedLeaseId, credential.ProjectId, Guid.NewGuid()),
        ];
        var gateway = new NodeTransportReservationGateway(transport, source);

        await gateway.ExpandAsync(
            Guid.NewGuid(),
            credential.ProjectId,
            17,
            "owner-session",
            [new ReservationScopeSpec("resource", "build")],
            CancellationToken.None);
        await gateway.ReleaseAsync(
            Guid.NewGuid(),
            credential.ProjectId,
            "owner-session",
            CancellationToken.None);
        await gateway.ListAsync(
            credential.ProjectId,
            includeReleased: true,
            CancellationToken.None);
        await gateway.RenewAsync(
            listedLeaseId,
            17,
            "owner-session",
            CancellationToken.None);

        AssertCredential(Assert.Single(transport.Expansions), credential);
        AssertCredential(Assert.Single(transport.Releases), credential);
        AssertCredential(Assert.Single(transport.Lists), credential);
        AssertCredential(Assert.Single(transport.Renewals), credential);
    }

    [Fact]
    public async Task Missing_request_or_project_credential_fails_before_transport_invocation()
    {
        var transport = new RecordingReservationTransport();
        var gateway = new NodeTransportReservationGateway(
            transport,
            new NodeAssignmentCredentialSource());
        var leaseId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.AcquireAsync(
            projectId,
            Guid.NewGuid(),
            "owner-session",
            [],
            "editing",
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ExpandAsync(
            leaseId,
            projectId,
            1,
            "owner-session",
            [],
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ReleaseAsync(
            leaseId,
            projectId,
            "owner-session",
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ListAsync(
            projectId,
            includeReleased: false,
            CancellationToken.None));

        Assert.Equal(0, transport.InvocationCount);
    }

    [Fact]
    public async Task Acquire_with_a_project_outside_the_request_credential_fails_before_transport()
    {
        var credential = Credential();
        var transport = new RecordingReservationTransport();
        var gateway = new NodeTransportReservationGateway(
            transport,
            SourceWith(credential));

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.AcquireAsync(
            Guid.NewGuid(),
            credential.RequestId,
            "owner-session",
            [],
            "editing",
            CancellationToken.None));

        Assert.Equal(0, transport.InvocationCount);
    }

    [Fact]
    public async Task Unknown_lease_fails_closed_for_every_lease_credential_operation()
    {
        var transport = new RecordingReservationTransport();
        var gateway = new NodeTransportReservationGateway(
            transport,
            new NodeAssignmentCredentialSource());
        var leaseId = Guid.NewGuid();
        var operations = new Func<Task>[]
        {
            () => gateway.RenewAsync(leaseId, 1, "owner-session", CancellationToken.None),
            () => gateway.TransferAsync(
                leaseId,
                "owner-session",
                "next-session",
                CancellationToken.None),
            () => gateway.AuthorizeAsync(
                leaseId,
                1,
                "owner-session",
                "/workspace/file.cs",
                "write",
                CancellationToken.None),
            () => gateway.MarkRecoveryRequiredAsync(
                leaseId,
                "transport interrupted",
                CancellationToken.None),
        };

        foreach (var operation in operations)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(operation);
        }

        Assert.Equal(0, transport.InvocationCount);
    }

    [Fact]
    public async Task Expand_result_does_not_establish_a_lease_credential_cache_entry()
    {
        var credential = Credential();
        var leaseId = Guid.NewGuid();
        var transport = new RecordingReservationTransport
        {
            ExpandResult = Success(Lease(
                leaseId,
                credential.ProjectId,
                credential.RequestId)),
        };
        var gateway = new NodeTransportReservationGateway(
            transport,
            SourceWith(credential));

        await gateway.ExpandAsync(
            leaseId,
            credential.ProjectId,
            1,
            "owner-session",
            [],
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.RenewAsync(
            leaseId,
            1,
            "owner-session",
            CancellationToken.None));
        Assert.Equal(1, transport.InvocationCount);
    }

    private static NodeAssignmentCredential Credential()
        => new(Guid.NewGuid(), Guid.NewGuid(), "opaque-claim-token");

    private static NodeAssignmentCredentialSource SourceWith(NodeAssignmentCredential credential)
    {
        var source = new NodeAssignmentCredentialSource();
        source.Track(credential);
        return source;
    }

    private static ReservationOperationResultMessage Success(ReservationLeaseMessage lease)
        => new(lease, null);

    private static ReservationOperationResultMessage Failure()
        => new(null, new ReservationErrorMessage(
            ReservationErrorCodes.Conflict,
            "The requested scope conflicts with an active lease.",
            []));

    private static ReservationLeaseMessage Lease(
        Guid leaseId,
        Guid projectId,
        Guid requestId)
        => new(
            leaseId,
            projectId,
            requestId,
            "owner-session",
            11,
            0,
            "Active",
            "editing",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(2),
            null,
            []);

    private static void AssertCredential(
        AcquireReservationMessage message,
        NodeAssignmentCredential credential)
        => AssertCredential(
            message.ProjectId,
            message.RequestId,
            message.ClaimToken,
            credential);

    private static void AssertCredential(
        ReservationMutationMessage message,
        NodeAssignmentCredential credential)
        => AssertCredential(
            message.ProjectId,
            message.RequestId,
            message.ClaimToken,
            credential);

    private static void AssertCredential(
        ExpandReservationMessage message,
        NodeAssignmentCredential credential)
        => AssertCredential(
            message.ProjectId,
            message.RequestId,
            message.ClaimToken,
            credential);

    private static void AssertCredential(
        ReleaseReservationMessage message,
        NodeAssignmentCredential credential)
        => AssertCredential(
            message.ProjectId,
            message.RequestId,
            message.ClaimToken,
            credential);

    private static void AssertCredential(
        TransferReservationMessage message,
        NodeAssignmentCredential credential)
        => AssertCredential(
            message.ProjectId,
            message.RequestId,
            message.ClaimToken,
            credential);

    private static void AssertCredential(
        MutationAuthorizationMessage message,
        NodeAssignmentCredential credential)
        => AssertCredential(
            message.ProjectId,
            message.RequestId,
            message.ClaimToken,
            credential);

    private static void AssertCredential(
        MarkRecoveryMessage message,
        NodeAssignmentCredential credential)
        => AssertCredential(
            message.ProjectId,
            message.RequestId,
            message.ClaimToken,
            credential);

    private static void AssertCredential(
        ListReservationsMessage message,
        NodeAssignmentCredential credential)
        => AssertCredential(
            message.ProjectId,
            message.RequestId,
            message.ClaimToken,
            credential);

    private static void AssertCredential(
        Guid projectId,
        Guid requestId,
        string claimToken,
        NodeAssignmentCredential credential)
    {
        Assert.Equal(credential.ProjectId, projectId);
        Assert.Equal(credential.RequestId, requestId);
        Assert.Equal(credential.ClaimToken, claimToken);
    }

    private sealed class RecordingReservationTransport : INodeReservationTransport
    {
        public List<AcquireReservationMessage> Acquires { get; } = [];
        public List<ReservationMutationMessage> Renewals { get; } = [];
        public List<ExpandReservationMessage> Expansions { get; } = [];
        public List<ReleaseReservationMessage> Releases { get; } = [];
        public List<TransferReservationMessage> Transfers { get; } = [];
        public List<MutationAuthorizationMessage> Authorizations { get; } = [];
        public List<MarkRecoveryMessage> Recoveries { get; } = [];
        public List<ListReservationsMessage> Lists { get; } = [];

        public ReservationOperationResultMessage AcquireResult { get; set; } = Failure();

        public ReservationOperationResultMessage ExpandResult { get; set; } = Failure();

        public ReservationLeaseMessage[] ListResult { get; set; } = [];

        public int InvocationCount =>
            Acquires.Count
            + Renewals.Count
            + Expansions.Count
            + Releases.Count
            + Transfers.Count
            + Authorizations.Count
            + Recoveries.Count
            + Lists.Count;

        public Task<ReservationOperationResultMessage> AcquireReservationAsync(
            AcquireReservationMessage message,
            CancellationToken cancellationToken)
        {
            Acquires.Add(message);
            return Task.FromResult(AcquireResult);
        }

        public Task<ReservationOperationResultMessage> RenewReservationAsync(
            ReservationMutationMessage message,
            CancellationToken cancellationToken)
        {
            Renewals.Add(message);
            return Task.FromResult(Failure());
        }

        public Task<ReservationOperationResultMessage> ExpandReservationAsync(
            ExpandReservationMessage message,
            CancellationToken cancellationToken)
        {
            Expansions.Add(message);
            return Task.FromResult(ExpandResult);
        }

        public Task<ReservationOperationResultMessage> ReleaseReservationAsync(
            ReleaseReservationMessage message,
            CancellationToken cancellationToken)
        {
            Releases.Add(message);
            return Task.FromResult(Failure());
        }

        public Task<ReservationOperationResultMessage> TransferReservationAsync(
            TransferReservationMessage message,
            CancellationToken cancellationToken)
        {
            Transfers.Add(message);
            return Task.FromResult(Failure());
        }

        public Task<MutationAuthorizationResultMessage> AuthorizeMutationAsync(
            MutationAuthorizationMessage message,
            CancellationToken cancellationToken)
        {
            Authorizations.Add(message);
            return Task.FromResult(new MutationAuthorizationResultMessage(true, null));
        }

        public Task<ReservationOperationResultMessage> MarkRecoveryRequiredAsync(
            MarkRecoveryMessage message,
            CancellationToken cancellationToken)
        {
            Recoveries.Add(message);
            return Task.FromResult(Failure());
        }

        public Task<ReservationLeaseMessage[]> ListReservationsAsync(
            ListReservationsMessage message,
            CancellationToken cancellationToken)
        {
            Lists.Add(message);
            return Task.FromResult(ListResult);
        }
    }
}
