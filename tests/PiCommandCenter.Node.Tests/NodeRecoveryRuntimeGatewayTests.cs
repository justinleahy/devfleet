using Microsoft.AspNetCore.SignalR.Client;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Child;
using PiCommandCenter.Node.Recovery;

namespace PiCommandCenter.Node.Tests;

public sealed class NodeRecoveryRuntimeGatewayTests
{
    [Fact]
    public async Task Cancel_stops_only_request_owned_root_and_children()
    {
        var roots = new FakeRootSessionSupervisor();
        var children = new FakeChildSessions();
        var requestId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var other = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        await roots.StartForAssignmentAsync(
            NodeWorkerTestHarness.Assignment(requestId, DateTimeOffset.UtcNow.AddMinutes(1)),
            CancellationToken.None);
        await roots.StartForAssignmentAsync(
            NodeWorkerTestHarness.Assignment(other, DateTimeOffset.UtcNow.AddMinutes(1)),
            CancellationToken.None);
        children.Live[requestId] = ["child-a"];
        children.Live[other] = ["child-b"];
        var gateway = Create(roots, children);

        await gateway.CancelRootAndChildrenAsync(requestId, CancellationToken.None);

        Assert.Equal([($"root-{requestId:N}", "assignment_recovery")], roots.Cancelled);
        Assert.Equal([("child-a", "assignment_recovery")], children.Cancelled);
    }

    [Fact]
    public async Task Children_are_known_operations_are_unknown()
    {
        var children = new FakeChildSessions();
        var requestId = Guid.NewGuid();
        children.Live[requestId] = ["c1", "c2"];
        var gateway = Create(children: children);

        var observedChildren = await gateway.ObserveChildrenAsync(requestId, CancellationToken.None);
        var operations = await gateway.ObserveOperationsAsync(requestId, CancellationToken.None);

        Assert.True(observedChildren.IsKnown);
        Assert.Equal(2, observedChildren.Value);
        Assert.True(operations.IsUnknown);
        Assert.Equal(RecoveryReasonCodes.OperationDrainTimeout, operations.UnknownReasonCode);
    }

    [Fact]
    public async Task Publish_reports_actual_ack_position_from_event_sequence()
    {
        var hub = new FakeNodeHub();
        hub.AcknowledgementsToReturn.Add(new NodeEventAcknowledgementMessage(["e2"]));
        var gateway = Create(hub: hub);
        var events = new[]
        {
            Event("e1", 3),
            Event("e2", 9),
        };

        var ack = await gateway.PublishAsync(events, CancellationToken.None);

        Assert.Equal(["e2"], ack.AcknowledgedEventIds);
        Assert.Equal(9, ack.AcknowledgementPosition);
        Assert.Null(ack.UnknownReasonCode);
    }

    [Fact]
    public async Task Publish_offline_is_unknown_not_known_zero()
    {
        var hub = new FakeNodeHub { State = HubConnectionState.Disconnected };
        var gateway = Create(hub: hub);

        var ack = await gateway.PublishAsync([Event("e1", 1)], CancellationToken.None);

        Assert.Empty(ack.AcknowledgedEventIds);
        Assert.Null(ack.AcknowledgementPosition);
        Assert.Equal(RecoveryReasonCodes.EventsUnacknowledged, ack.UnknownReasonCode);
    }

    [Fact]
    public async Task Reservations_filter_by_assignment_session_ownership()
    {
        var roots = new FakeRootSessionSupervisor();
        var requestId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var assignment = NodeWorkerTestHarness.Assignment(requestId, DateTimeOffset.UtcNow.AddMinutes(1));
        await roots.StartForAssignmentAsync(assignment, CancellationToken.None);
        var rootSession = $"root-{requestId:N}";
        var reservations = new FakeReservationGateway();
        var owned = new ReservationLeaseInfo(Guid.NewGuid(), 1, "Active", DateTimeOffset.UtcNow, [], rootSession);
        var other = new ReservationLeaseInfo(Guid.NewGuid(), 2, "Active", DateTimeOffset.UtcNow, [], "other");
        reservations.Leases.AddRange([owned, other]);
        var gateway = Create(roots, reservations: reservations);

        await gateway.CancelRootAndChildrenAsync(requestId, CancellationToken.None);
        var listed = await gateway.ListForAssignmentAsync(
            assignment.ProjectId,
            requestId,
            CancellationToken.None);

        Assert.Equal([owned.LeaseId], listed.Select(lease => lease.LeaseId));
    }

    [Fact]
    public async Task Reservations_without_owners_stay_unknown()
    {
        var gateway = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.ListForAssignmentAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));
    }

    private static NodeRecoveryRuntimeGateway Create(
        FakeRootSessionSupervisor? roots = null,
        FakeChildSessions? children = null,
        FakeReservationGateway? reservations = null,
        FakeNodeHub? hub = null)
        => new(
            roots ?? new FakeRootSessionSupervisor(),
            children ?? new FakeChildSessions(),
            reservations ?? new FakeReservationGateway(),
            hub ?? new FakeNodeHub());

    private static NodeEventMessage Event(string id, long sequence) =>
        new(
            id,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "claim",
            null,
            sequence,
            "x",
            DateTimeOffset.UnixEpoch,
            "{}");

    private sealed class FakeChildSessions : IAssignmentRecoveryChildSessions
    {
        public Dictionary<Guid, List<string>> Live { get; } = [];
        public List<(string SessionId, string Reason)> Cancelled { get; } = [];

        public IReadOnlyList<string> ListLiveSessionIds(Guid requestId)
            => Live.TryGetValue(requestId, out var ids) ? ids : [];

        public Task CancelSessionAsync(string sessionId, string reason)
        {
            Cancelled.Add((sessionId, reason));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReservationGateway : INodeReservationGateway
    {
        public List<ReservationLeaseInfo> Leases { get; } = [];

        public Task<ReservationOperationResult> AcquireAsync(
            Guid projectId,
            Guid requestId,
            string ownerSessionId,
            IReadOnlyList<ReservationScopeSpec> scopes,
            string reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ReservationOperationResult> ExpandAsync(
            Guid leaseId,
            Guid projectId,
            long fencingToken,
            string sessionId,
            IReadOnlyList<ReservationScopeSpec> scopes,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ReservationOperationResult> ReleaseAsync(
            Guid leaseId,
            Guid projectId,
            string sessionId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ReservationOperationResult> TransferAsync(
            Guid leaseId,
            string fromSessionId,
            string toSessionId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ReservationOperationResult> RenewAsync(
            Guid leaseId,
            long fencingToken,
            string sessionId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<MutationAuthorizationResult> AuthorizeAsync(
            Guid leaseId,
            long fencingToken,
            string sessionId,
            string targetPath,
            string operation,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ReservationLeaseInfo>> ListAsync(
            Guid projectId,
            bool includeReleased,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ReservationLeaseInfo>>(Leases);

        public Task<ReservationOperationResult> MarkRecoveryRequiredAsync(
            Guid leaseId,
            string reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
