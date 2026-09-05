using System.Text.Json;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Child;

namespace PiCommandCenter.Node.Repository;

/// <summary>
/// On a runtime crash, marks owned active leases recovery-required and emits notification facts.
/// Does not release leases (SPEC: crash preserves leases as RecoveryRequired).
/// </summary>
public interface IRuntimeCrashRecovery
{
    Task MarkOwnedLeasesRecoveryRequiredAsync(
        Guid nodeId,
        Guid projectId,
        Guid? requestId,
        string ownerSessionId,
        string reason,
        CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class RuntimeCrashRecovery : IRuntimeCrashRecovery
{
    public const string EventType = "reservation.recovery_required";

    private readonly INodeReservationGateway _reservations;
    private readonly INodeEventSpool _spool;
    private readonly TimeProvider _time;

    public RuntimeCrashRecovery(
        INodeReservationGateway reservations,
        INodeEventSpool spool,
        TimeProvider time)
    {
        _reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
        _spool = spool ?? throw new ArgumentNullException(nameof(spool));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task MarkOwnedLeasesRecoveryRequiredAsync(
        Guid nodeId,
        Guid projectId,
        Guid? requestId,
        string ownerSessionId,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSessionId);
        var leases = await _reservations.ListAsync(projectId, includeReleased: false, cancellationToken)
            .ConfigureAwait(false);

        foreach (var lease in leases)
        {
            if (!string.Equals(lease.OwnerSessionId, ownerSessionId, StringComparison.Ordinal)
                || !string.Equals(lease.State, "Active", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await _reservations.MarkRecoveryRequiredAsync(lease.LeaseId, reason, cancellationToken)
                .ConfigureAwait(false);

            var payload = JsonSerializer.Serialize(new
            {
                leaseId = lease.LeaseId,
                ownerSessionId,
                reason,
                fencingToken = lease.FencingToken,
            });

            await _spool.AppendAsync(
                new NodeEventMessage(
                    EventId: Guid.NewGuid().ToString("N"),
                    NodeId: nodeId,
                    ProjectId: projectId,
                    RequestId: requestId,
                    SessionId: ownerSessionId,
                    Sequence: 0,
                    Type: EventType,
                    OccurredAt: _time.GetUtcNow(),
                    PayloadJson: payload),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
