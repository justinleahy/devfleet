using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR.Client;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Child;

namespace PiCommandCenter.Node.Recovery;

/// <summary>
/// Production recovery seams: stop request-owned root/children, observe real
/// request-scoped activity or explicit unknown, publish spool through
/// <see cref="INodeHubOps"/> with the actual acknowledgement, and list only
/// reservations owned by assignment sessions.
/// </summary>
internal sealed class NodeRecoveryRuntimeGateway :
    IAssignmentRecoverySessionCanceller,
    IAssignmentRecoveryActivityObserver,
    IAssignmentRecoveryEventPublisher,
    IAssignmentRecoveryReservationCatalog
{
    private readonly IRootSessionSupervisor _roots;
    private readonly IAssignmentRecoveryChildSessions _children;
    private readonly INodeReservationGateway _reservations;
    private readonly INodeHubOps _hub;
    private readonly ConcurrentDictionary<Guid, IReadOnlySet<string>> _assignmentOwners = new();

    public NodeRecoveryRuntimeGateway(
        IRootSessionSupervisor roots,
        IAssignmentRecoveryChildSessions children,
        INodeReservationGateway reservations,
        INodeHubOps hub)
    {
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _children = children ?? throw new ArgumentNullException(nameof(children));
        _reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
    }

    public async Task CancelRootAndChildrenAsync(Guid requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CaptureOwners(requestId);

        var rootIds = _roots.ActiveSessionIds
            .Where(sessionId => _roots.FindRequestId(sessionId) == requestId)
            .ToArray();
        var childIds = _children.ListLiveSessionIds(requestId);

        foreach (var sessionId in rootIds)
        {
            await _roots.CancelSessionAsync(sessionId, "assignment_recovery").ConfigureAwait(false);
        }

        foreach (var sessionId in childIds)
        {
            await _children.CancelSessionAsync(sessionId, "assignment_recovery").ConfigureAwait(false);
        }
    }

    public Task<RecoveryKnownCountMessage> ObserveChildrenAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = _children.ListLiveSessionIds(requestId).Count;
        return Task.FromResult(new RecoveryKnownCountMessage(count, null));
    }

    public Task<RecoveryKnownCountMessage> ObserveOperationsAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = requestId;
        return Task.FromResult(
            new RecoveryKnownCountMessage(null, RecoveryReasonCodes.OperationDrainTimeout));
    }

    public async Task<AssignmentRecoveryEventAck> PublishAsync(
        IReadOnlyList<NodeEventMessage> events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (_hub.State != HubConnectionState.Connected)
        {
            return new AssignmentRecoveryEventAck(
                [],
                null,
                RecoveryReasonCodes.EventsUnacknowledged);
        }

        try
        {
            var acknowledgement = await _hub
                .PublishEventsAsync(events, cancellationToken)
                .ConfigureAwait(false);
            var ackedIds = acknowledgement.EventIds;
            if (ackedIds.Count == 0)
            {
                return new AssignmentRecoveryEventAck(
                    ackedIds,
                    null,
                    RecoveryReasonCodes.EventsUnacknowledged);
            }

            var byId = events.ToDictionary(static evt => evt.EventId, StringComparer.Ordinal);
            long? position = null;
            foreach (var eventId in ackedIds)
            {
                if (!byId.TryGetValue(eventId, out var evt))
                {
                    continue;
                }

                position = position is { } current && current > evt.Sequence
                    ? current
                    : evt.Sequence;
            }

            return position is null
                ? new AssignmentRecoveryEventAck(
                    ackedIds,
                    null,
                    RecoveryReasonCodes.EventsUnacknowledged)
                : new AssignmentRecoveryEventAck(ackedIds, position, null);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new AssignmentRecoveryEventAck(
                [],
                null,
                RecoveryReasonCodes.EventsUnacknowledged);
        }
    }

    public async Task<IReadOnlyList<ReservationLeaseInfo>> ListForAssignmentAsync(
        Guid projectId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        if (!TryGetOwners(requestId, out var owners))
        {
            throw new InvalidOperationException("assignment_reservation_owners_unknown");
        }

        var leases = await _reservations
            .ListAsync(projectId, includeReleased: true, cancellationToken)
            .ConfigureAwait(false);
        return
        [
            .. leases.Where(lease => owners.Contains(lease.OwnerSessionId)),
        ];
    }

    private void CaptureOwners(Guid requestId)
        => _assignmentOwners[requestId] = SnapshotOwners(requestId);

    private bool TryGetOwners(Guid requestId, out IReadOnlySet<string> owners)
    {
        if (_assignmentOwners.TryGetValue(requestId, out owners!))
        {
            return true;
        }

        var live = SnapshotOwners(requestId);
        if (live.Count == 0)
        {
            owners = live;
            return false;
        }

        _assignmentOwners[requestId] = live;
        owners = live;
        return true;
    }

    private HashSet<string> SnapshotOwners(Guid requestId)
    {
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sessionId in _roots.ActiveSessionIds)
        {
            if (_roots.FindRequestId(sessionId) == requestId)
            {
                owners.Add(sessionId);
            }
        }

        foreach (var sessionId in _children.ListLiveSessionIds(requestId))
        {
            owners.Add(sessionId);
        }

        return owners;
    }
}

/// <summary>Request-scoped child listing/cancel over the live child supervisor.</summary>
internal sealed class PiChildAssignmentRecoverySessions(PiChildSessionSupervisor children)
    : IAssignmentRecoveryChildSessions
{
    private readonly PiChildSessionSupervisor _children =
        children ?? throw new ArgumentNullException(nameof(children));

    public IReadOnlyList<string> ListLiveSessionIds(Guid requestId)
        => _children.ListLiveChildSessionIds(requestId);

    public Task CancelSessionAsync(string sessionId, string reason)
        => _children.CancelSessionAsync(sessionId, reason);
}
