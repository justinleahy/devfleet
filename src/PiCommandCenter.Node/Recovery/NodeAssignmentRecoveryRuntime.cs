using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Child;
using PiCommandCenter.Node.Quiescence;
using PiCommandCenter.Node.Repository;
using PiCommandCenter.Node.Runtime;

namespace PiCommandCenter.Node.Recovery;

/// <summary>
/// Request-scoped cooperative cancellation for root and child sessions.
/// Production DI wires <c>NodeRecoveryRuntimeGateway</c>.
/// </summary>
internal interface IAssignmentRecoverySessionCanceller
{
    Task CancelRootAndChildrenAsync(Guid requestId, CancellationToken cancellationToken);
}

/// <summary>
/// Live children hosted for one assignment. Production wraps
/// <see cref="PiChildSessionSupervisor"/>.
/// </summary>
internal interface IAssignmentRecoveryChildSessions
{
    IReadOnlyList<string> ListLiveSessionIds(Guid requestId);

    Task CancelSessionAsync(string sessionId, string reason);
}


/// <summary>
/// Live children/operations. Unknown when request-local facts cannot be proven.
/// Production DI wires <c>NodeRecoveryRuntimeGateway</c>.
/// </summary>
internal interface IAssignmentRecoveryActivityObserver
{
    Task<RecoveryKnownCountMessage> ObserveChildrenAsync(
        Guid requestId,
        CancellationToken cancellationToken);

    Task<RecoveryKnownCountMessage> ObserveOperationsAsync(
        Guid requestId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Publishes spool events through <see cref="INodeHubOps"/> and reports the
/// actual acknowledgement. Production DI wires <c>NodeRecoveryRuntimeGateway</c>.
/// </summary>
internal interface IAssignmentRecoveryEventPublisher
{
    Task<AssignmentRecoveryEventAck> PublishAsync(
        IReadOnlyList<NodeEventMessage> events,
        CancellationToken cancellationToken);
}

internal sealed record AssignmentRecoveryEventAck(
    IReadOnlyList<string> AcknowledgedEventIds,
    long? AcknowledgementPosition,
    string? UnknownReasonCode);

/// <summary>
/// Assignment-scoped reservation listing filtered by assignment session
/// ownership. Production DI wires <c>NodeRecoveryRuntimeGateway</c>.
/// </summary>
internal interface IAssignmentRecoveryReservationCatalog
{
    Task<IReadOnlyList<ReservationLeaseInfo>> ListForAssignmentAsync(
        Guid projectId,
        Guid requestId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Production <see cref="IAssignmentRecoveryRuntime"/>: journals intent and process
/// identities before stop, closes admission, cooperatively cancels, stops the
/// request registry with NodeOptions budgets, drains, publishes spool without
/// deleting unacknowledged events, inspects the canonical workspace, and resolves
/// only assignment reservations after stop proof.
/// </summary>
internal sealed class NodeAssignmentRecoveryRuntime : IAssignmentRecoveryRuntime
{
    private readonly INodeAssignmentJournal _journal;
    private readonly AssignmentProcessRegistry _processes;
    private readonly IRequestAdmissionGate _admission;
    private readonly IAssignmentRecoverySessionCanceller _sessions;
    private readonly IAssignmentRecoveryActivityObserver _activities;
    private readonly INodeEventSpool _spool;
    private readonly IAssignmentRecoveryEventPublisher _events;
    private readonly IRepositoryInspector _repository;
    private readonly INodeReservationGateway _reservations;
    private readonly IAssignmentRecoveryReservationCatalog _reservationCatalog;
    private readonly IOptions<NodeOptions> _options;
    private readonly TimeProvider _time;
    private readonly object _proofLock = new();
    private readonly Dictionary<Guid, ProcessStopProof> _stopProofs = [];

    public NodeAssignmentRecoveryRuntime(
        INodeAssignmentJournal journal,
        AssignmentProcessRegistry processes,
        IRequestAdmissionGate admission,
        IAssignmentRecoverySessionCanceller sessions,
        IAssignmentRecoveryActivityObserver activities,
        INodeEventSpool spool,
        IAssignmentRecoveryEventPublisher events,
        IRepositoryInspector repository,
        INodeReservationGateway reservations,
        IAssignmentRecoveryReservationCatalog reservationCatalog,
        IOptions<NodeOptions> options,
        TimeProvider time)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _activities = activities ?? throw new ArgumentNullException(nameof(activities));
        _spool = spool ?? throw new ArgumentNullException(nameof(spool));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
        _reservationCatalog = reservationCatalog
            ?? throw new ArgumentNullException(nameof(reservationCatalog));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task JournalRecoveryIntentAsync(
        RecoverAssignmentCommandMessage command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var identities = _processes.Snapshot(command.RequestId);
        var loaded = await _journal.LoadAsync(cancellationToken).ConfigureAwait(false);
        var current = loaded.FirstOrDefault(entry => entry.Assignment.RequestId == command.RequestId);
        if (current is null)
        {
            return;
        }

        var pending = await _spool.CountPendingForRequestAsync(command.RequestId, cancellationToken)
            .ConfigureAwait(false);
        await _journal.UpsertAsync(
            current with
            {
                SupervisorState = AssignmentSupervisorState.Unknown,
                ProcessIdentities = identities,
                PendingEventCount = pending,
            },
            cancellationToken).ConfigureAwait(false);
    }

    public Task CloseAdmissionAsync(
        RecoverAssignmentCommandMessage command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        _admission.CloseAdmission(command.RequestId);
        return Task.CompletedTask;
    }

    public Task RequestCooperativeStopAsync(
        RecoverAssignmentCommandMessage command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _sessions.CancelRootAndChildrenAsync(command.RequestId, cancellationToken);
    }

    public async Task<AssignmentRecoveryProcessSnapshot> StopIsolatedProcessesAsync(
        RecoverAssignmentCommandMessage command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var options = _options.Value;
        var result = await _processes.StopAsync(
                command.RequestId,
                TimeSpan.FromSeconds(options.RecoveryCooperativeStopSeconds),
                TimeSpan.FromSeconds(options.RecoveryTerminationSeconds),
                cancellationToken)
            .ConfigureAwait(false);

        var identities = MapIdentities(result.DiscoveredProcesses);
        RecoveryKnownCountMessage processes;
        var proven = result.Proven && identities.All(static identity => !identity.EscapedDescendant);
        if (proven)
        {
            processes = Known(0);
        }
        else
        {
            processes = Unknown(string.IsNullOrWhiteSpace(result.BlockerCode)
                ? RecoveryReasonCodes.ProcessStopUnproven
                : result.BlockerCode);
        }

        lock (_proofLock)
        {
            _stopProofs[command.RequestId] = new ProcessStopProof(proven, processes, identities);
        }

        return new AssignmentRecoveryProcessSnapshot(processes, identities);
    }

    public async Task DrainSupervisedOperationsAsync(
        RecoverAssignmentCommandMessage command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var options = _options.Value;
        var timeout = TimeSpan.FromSeconds(
            options.RecoveryCooperativeStopSeconds + options.RecoveryTerminationSeconds);
        await _admission.WaitUntilDrainedAsync(command.RequestId, timeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AssignmentRecoveryEventFlushResult> FlushAcknowledgedEventsAsync(
        RecoverAssignmentCommandMessage command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var pending = await _spool.PeekPendingAsync(int.MaxValue, cancellationToken)
            .ConfigureAwait(false);
        var scoped = pending
            .Where(evt => evt.RequestId == command.RequestId)
            .ToArray();

        if (scoped.Length == 0)
        {
            var remainingEmpty = await _spool.CountPendingForRequestAsync(
                    command.RequestId,
                    cancellationToken)
                .ConfigureAwait(false);
            return new AssignmentRecoveryEventFlushResult(
                Known(remainingEmpty),
                EventAcknowledgementPosition: 0,
                EventAcknowledgementUnknownReasonCode: null);
        }

        AssignmentRecoveryEventAck ack;
        try
        {
            ack = await _events.PublishAsync(scoped, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            var remainingUnknown = await _spool.CountPendingForRequestAsync(
                    command.RequestId,
                    cancellationToken)
                .ConfigureAwait(false);
            return new AssignmentRecoveryEventFlushResult(
                remainingUnknown > 0
                    ? Known(remainingUnknown)
                    : Unknown(RecoveryReasonCodes.EventsUnacknowledged),
                EventAcknowledgementPosition: null,
                EventAcknowledgementUnknownReasonCode: RecoveryReasonCodes.EventsUnacknowledged);
        }

        if (ack.AcknowledgedEventIds.Count > 0)
        {
            await _spool.DeleteAsync(ack.AcknowledgedEventIds, cancellationToken)
                .ConfigureAwait(false);
        }

        var remaining = await _spool.CountPendingForRequestAsync(command.RequestId, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(ack.UnknownReasonCode) || ack.AcknowledgementPosition is null)
        {
            return new AssignmentRecoveryEventFlushResult(
                remaining > 0 ? Known(remaining) : Unknown(RecoveryReasonCodes.EventsUnacknowledged),
                ack.AcknowledgementPosition,
                string.IsNullOrWhiteSpace(ack.UnknownReasonCode)
                    ? RecoveryReasonCodes.EventsUnacknowledged
                    : ack.UnknownReasonCode);
        }

        return new AssignmentRecoveryEventFlushResult(
            Known(remaining),
            ack.AcknowledgementPosition,
            null);
    }

    public async Task<RecoveryRepositoryStatusMessage> InspectRepositoryAsync(
        RecoverAssignmentCommandMessage command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var observedAt = _time.GetUtcNow();
        var loaded = await _journal.LoadAsync(cancellationToken).ConfigureAwait(false);
        var entry = loaded.FirstOrDefault(item => item.Assignment.RequestId == command.RequestId);
        var path = entry?.Assignment.CanonicalRepositoryPathSnapshot;
        if (string.IsNullOrWhiteSpace(path))
        {
            return Unavailable(observedAt);
        }

        try
        {
            var baseline = await _repository.CaptureBaselineAsync(
                    path,
                    requireCleanStart: false,
                    allowUntrackedFiles: true,
                    cancellationToken)
                .ConfigureAwait(false);
            return MapBaseline(baseline, observedAt);
        }
        catch (RepositoryDirtyException dirty)
        {
            return new RecoveryRepositoryStatusMessage(
                Available: true,
                Head: null,
                Branch: null,
                IndexSummary: "dirty",
                WorktreeSummary: "modified",
                UntrackedCount: Known(dirty.DirtyPaths.Count),
                InterruptedOperationIndicators: [],
                ObservedAt: observedAt);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable(observedAt);
        }
    }

    public async Task<IReadOnlyList<RecoveryReservationDispositionMessage>> ResolveReservationsAsync(
        RecoverAssignmentCommandMessage command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!TryGetStopProof(command.RequestId, out var proof) || !proof.Proven)
        {
            return [];
        }

        IReadOnlyList<ReservationLeaseInfo> leases;
        try
        {
            leases = await _reservationCatalog.ListForAssignmentAsync(
                    command.ProjectId,
                    command.RequestId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }

        var dispositions = new List<RecoveryReservationDispositionMessage>(leases.Count);
        foreach (var lease in leases)
        {
            if (!string.Equals(lease.State, "Active", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(lease.State, "RecoveryRequired", StringComparison.OrdinalIgnoreCase))
            {
                dispositions.Add(new RecoveryReservationDispositionMessage(lease.LeaseId, "none", null));
                continue;
            }

            var marked = await _reservations.MarkRecoveryRequiredAsync(
                    lease.LeaseId,
                    "assignment_recovery",
                    cancellationToken)
                .ConfigureAwait(false);
            if (marked.Ok)
            {
                dispositions.Add(
                    new RecoveryReservationDispositionMessage(lease.LeaseId, "resolved", null));
            }
            else
            {
                dispositions.Add(
                    new RecoveryReservationDispositionMessage(
                        lease.LeaseId,
                        "unresolved",
                        RecoveryReasonCodes.ReservationUnresolved));
            }
        }

        return dispositions;
    }

    public async Task<AssignmentRecoveryInventorySnapshot> ObserveInventoryAsync(
        RecoverAssignmentCommandMessage command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var children = await ObserveSafeAsync(
                () => _activities.ObserveChildrenAsync(command.RequestId, cancellationToken),
                RecoveryReasonCodes.ProcessStopUnproven)
            .ConfigureAwait(false);
        var operations = await ObserveSafeAsync(
                () => _activities.ObserveOperationsAsync(command.RequestId, cancellationToken),
                RecoveryReasonCodes.OperationDrainTimeout)
            .ConfigureAwait(false);

        RecoveryKnownCountMessage processes;
        if (TryGetStopProof(command.RequestId, out var proof))
        {
            processes = proof.Processes;
        }
        else
        {
            processes = Unknown(RecoveryReasonCodes.ProcessStopUnproven);
        }

        RecoveryKnownCountMessage pendingEvents;
        try
        {
            var count = await _spool.CountPendingForRequestAsync(command.RequestId, cancellationToken)
                .ConfigureAwait(false);
            pendingEvents = Known(count);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            pendingEvents = Unknown(RecoveryReasonCodes.EventsUnacknowledged);
        }

        RecoveryKnownCountMessage reservations;
        try
        {
            var leases = await _reservationCatalog.ListForAssignmentAsync(
                    command.ProjectId,
                    command.RequestId,
                    cancellationToken)
                .ConfigureAwait(false);
            reservations = Known(leases.Count);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            reservations = Unknown(RecoveryReasonCodes.ReservationUnresolved);
        }

        return new AssignmentRecoveryInventorySnapshot(
            children,
            operations,
            processes,
            pendingEvents,
            reservations);
    }

    private bool TryGetStopProof(Guid requestId, out ProcessStopProof proof)
    {
        lock (_proofLock)
        {
            return _stopProofs.TryGetValue(requestId, out proof!);
        }
    }

    private static async Task<RecoveryKnownCountMessage> ObserveSafeAsync(
        Func<Task<RecoveryKnownCountMessage>> observe,
        string unknownCode)
    {
        try
        {
            var count = await observe().ConfigureAwait(false);
            return count.IsValid ? count : Unknown(unknownCode);
        }
        catch (Exception)
        {
            return Unknown(unknownCode);
        }
    }

    private static RecoveryRepositoryStatusMessage MapBaseline(
        RepositoryBaseline baseline,
        DateTimeOffset observedAt)
    {
        var unborn = string.IsNullOrWhiteSpace(baseline.BaseCommit)
            || string.Equals(baseline.BaseCommit, "HEAD", StringComparison.Ordinal);
        if (unborn)
        {
            return new RecoveryRepositoryStatusMessage(
                Available: true,
                Head: null,
                Branch: string.IsNullOrWhiteSpace(baseline.Branch) ? null : baseline.Branch,
                IndexSummary: "empty",
                WorktreeSummary: "empty",
                UntrackedCount: Known(CountUntracked(baseline)),
                InterruptedOperationIndicators: [],
                ObservedAt: observedAt);
        }

        var dirty = !baseline.IsClean || baseline.DirtyPaths.Count > 0;
        return new RecoveryRepositoryStatusMessage(
            Available: true,
            Head: baseline.BaseCommit,
            Branch: baseline.Branch,
            IndexSummary: dirty ? "dirty" : "clean",
            WorktreeSummary: dirty ? "modified" : "clean",
            UntrackedCount: Known(CountUntracked(baseline)),
            InterruptedOperationIndicators: [],
            ObservedAt: observedAt);
    }

    private static int CountUntracked(RepositoryBaseline baseline)
        => baseline.DirtyPaths.Count(static path =>
            path.StartsWith("?? ", StringComparison.Ordinal)
            || path.StartsWith("??", StringComparison.Ordinal));

    private static RecoveryRepositoryStatusMessage Unavailable(DateTimeOffset observedAt) =>
        new(
            Available: false,
            Head: null,
            Branch: null,
            IndexSummary: null,
            WorktreeSummary: null,
            UntrackedCount: Unknown(RecoveryReasonCodes.RepositoryStatusUnknown),
            InterruptedOperationIndicators: [],
            ObservedAt: observedAt);

    private static IReadOnlyList<RecoveryProcessIdentityMessage> MapIdentities(
        IReadOnlyList<AssignmentProcessIdentity> identities)
    {
        var mapped = new RecoveryProcessIdentityMessage[identities.Count];
        for (var i = 0; i < identities.Count; i++)
        {
            var identity = identities[i];
            mapped[i] = new RecoveryProcessIdentityMessage(
                identity.ProcessId,
                DateTimeOffset.UnixEpoch.AddTicks(Math.Max(identity.StartTimeTicks, 0)),
                identity.ScopeName ?? identity.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                EscapedDescendant: identity.SessionId <= 0 || identity.ProcessGroupId <= 0);
        }

        return mapped;
    }

    private static RecoveryKnownCountMessage Known(int value) => new(value, null);

    private static RecoveryKnownCountMessage Unknown(string code) => new(null, code);

    private sealed record ProcessStopProof(
        bool Proven,
        RecoveryKnownCountMessage Processes,
        IReadOnlyList<RecoveryProcessIdentityMessage> Identities);
}
