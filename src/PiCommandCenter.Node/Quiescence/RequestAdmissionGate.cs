using System.Collections.Concurrent;

namespace PiCommandCenter.Node.Quiescence;

/// <summary>
/// Default <see cref="IRequestAdmissionGate"/>: one node-wide registry keyed by request id.
/// Admission closure, activity admission, and drain signalling are atomic per request under
/// that request's lock; proofs combine the local drain with caller-supplied external
/// observations and fail closed on any deviation, timeout, or error.
/// </summary>
public sealed class RequestAdmissionGate : IRequestAdmissionGate
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private readonly ConcurrentDictionary<Guid, RequestState> _requests = new();
    private readonly ConcurrentDictionary<Guid, byte> _terminalRequests = new();
    private readonly TimeProvider _timeProvider;

    public RequestAdmissionGate(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    private sealed class RequestState
    {
        public readonly object Sync = new();
        public int ActiveChildren;
        public int ActiveOperations;
        public int ActiveProcesses;
        public int CallbackSources;
        public bool TerminalizationInProgress;
        public bool TerminalizationCommitted;
        public TaskCompletionSource Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsDrained => ActiveChildren == 0 && ActiveOperations == 0 && ActiveProcesses == 0;

        /// <summary>Wakes drain waiters; callers hold <see cref="Sync"/>.</summary>
        public void SignalLocked()
        {
            Drained.TrySetResult();
            Drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public bool IsAdmissionClosed(Guid requestId)
        => _terminalRequests.ContainsKey(requestId);

    public NodeActivityLease? TryEnterOperation(Guid requestId, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return TryEnterActivity(requestId, NodeActivityKind.Operation, operation);
    }

    public NodeActivityLease? TryAdmitChild(Guid requestId, string childSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childSessionId);
        return TryEnterActivity(requestId, NodeActivityKind.Child, childSessionId);
    }

    public NodeActivityLease? TryEnterTerminalization(Guid requestId, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return TryEnterActivity(
            requestId,
            NodeActivityKind.Operation,
            operation,
            exclusiveTerminalization: true);
    }

    private NodeActivityLease? TryEnterActivity(
        Guid requestId,
        NodeActivityKind kind,
        string description,
        bool exclusiveTerminalization = false)
    {
        while (true)
        {
            var state = _requests.GetOrAdd(requestId, static _ => new RequestState());
            lock (state.Sync)
            {
                if (!IsCurrentState(requestId, state))
                {
                    continue;
                }

                if (_terminalRequests.ContainsKey(requestId)
                    || exclusiveTerminalization && state.TerminalizationInProgress)
                {
                    return null;
                }

                switch (kind)
                {
                    case NodeActivityKind.Child:
                        state.ActiveChildren++;
                        break;
                    case NodeActivityKind.Operation:
                        state.ActiveOperations++;
                        break;
                    case NodeActivityKind.Process:
                        state.ActiveProcesses++;
                        break;
                }

                state.TerminalizationInProgress |= exclusiveTerminalization;
                return new NodeActivityLease(
                    kind,
                    description,
                    () => ReleaseActivity(
                        requestId,
                        state,
                        kind,
                        exclusiveTerminalization));
            }
        }
    }

    public RequestCallbackLease? TryRegisterCallbackSource(Guid requestId, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        while (true)
        {
            var state = _requests.GetOrAdd(requestId, static _ => new RequestState());
            lock (state.Sync)
            {
                if (!IsCurrentState(requestId, state))
                {
                    continue;
                }

                if (_terminalRequests.ContainsKey(requestId))
                {
                    return null;
                }

                state.CallbackSources++;
                return new RequestCallbackLease(
                    sessionId,
                    () => ReleaseCallbackSource(requestId, state));
            }
        }
    }

    public NodeActivityLease TrackProcess(Guid requestId, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        while (true)
        {
            var state = _requests.GetOrAdd(requestId, static _ => new RequestState());
            lock (state.Sync)
            {
                if (!IsCurrentState(requestId, state))
                {
                    continue;
                }

                state.ActiveProcesses++;
                return new NodeActivityLease(
                    NodeActivityKind.Process,
                    description,
                    () => ReleaseActivity(
                        requestId,
                        state,
                        NodeActivityKind.Process,
                        exclusiveTerminalization: false));
            }
        }
    }

    private void ReleaseActivity(
        Guid requestId,
        RequestState state,
        NodeActivityKind kind,
        bool exclusiveTerminalization)
    {
        lock (state.Sync)
        {
            switch (kind)
            {
                case NodeActivityKind.Child:
                    state.ActiveChildren--;
                    break;
                case NodeActivityKind.Operation:
                    state.ActiveOperations--;
                    break;
                case NodeActivityKind.Process:
                    state.ActiveProcesses--;
                    break;
            }

            if (exclusiveTerminalization)
            {
                state.TerminalizationInProgress = false;
            }

            state.SignalLocked();
            TryCleanupLocked(requestId, state);
        }
    }

    private void ReleaseCallbackSource(Guid requestId, RequestState state)
    {
        lock (state.Sync)
        {
            state.CallbackSources--;
            TryCleanupLocked(requestId, state);
        }
    }

    public void CloseAdmission(Guid requestId)
    {
        while (true)
        {
            var state = _requests.GetOrAdd(requestId, static _ => new RequestState());
            lock (state.Sync)
            {
                if (!IsCurrentState(requestId, state))
                {
                    continue;
                }

                _terminalRequests.TryAdd(requestId, 0);
                state.SignalLocked();
                return;
            }
        }
    }

    public async Task<QuiescenceOutcome> ProveQuiescenceAsync(
        Guid requestId,
        QuiescenceObservation observation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(observation.CountActiveReservations);
        ArgumentNullException.ThrowIfNull(observation.CountPendingEvents);
        ArgumentNullException.ThrowIfNull(observation.InspectRepository);

        var state = _requests.GetOrAdd(requestId, static _ => new RequestState());
        var deadline = _timeProvider.GetUtcNow() + timeout;

        while (true)
        {
            Task drainedSignal;
            AssignmentQuiescenceProof local;
            lock (state.Sync)
            {
                local = new AssignmentQuiescenceProof(
                    _terminalRequests.ContainsKey(requestId),
                    state.ActiveChildren,
                    state.ActiveOperations,
                    state.ActiveProcesses,
                    PendingEvents: 0,
                    ActiveReservations: 0,
                    RepositoryInspected: false,
                    _timeProvider.GetUtcNow());
                drainedSignal = state.Drained.Task;
            }

            if (!local.AdmissionClosed)
            {
                return new QuiescenceOutcome.Uncertain(
                    "admission_open",
                    local with { RepositoryInspected = false });
            }

            if (local.ActiveChildren == 0 && local.ActiveOperations == 0 && local.ActiveProcesses == 0)
            {
                QuiescenceOutcome? terminal = await TryObserveExternalAsync(
                    requestId, state, observation, cancellationToken).ConfigureAwait(false);
                if (terminal is not null)
                {
                    return terminal;
                }
            }

            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return new QuiescenceOutcome.Uncertain(
                    "timeout",
                    await SnapshotAsync(requestId, state, observation, cancellationToken).ConfigureAwait(false));
            }

            var delay = Task.Delay(
                remaining < PollInterval ? remaining : PollInterval, cancellationToken);
            try
            {
                await Task.WhenAny(drainedSignal, delay).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new QuiescenceOutcome.Uncertain(
                    "cancelled",
                    await SnapshotAsync(requestId, state, observation, CancellationToken.None).ConfigureAwait(false));
            }
            if (cancellationToken.IsCancellationRequested)
            {
                return new QuiescenceOutcome.Uncertain(
                    "cancelled",
                    await SnapshotAsync(requestId, state, observation, CancellationToken.None).ConfigureAwait(false));
            }
        }
    }

    /// <summary>
    /// Runs the external observations once local activity is drained. Returns null to keep
    /// waiting (observations not clean yet), or the terminal outcome. Re-reads the local
    /// counts while assembling the proof so the result is exact at observation time.
    /// </summary>
    private async Task<QuiescenceOutcome?> TryObserveExternalAsync(
        Guid requestId,
        RequestState state,
        QuiescenceObservation observation,
        CancellationToken cancellationToken)
    {
        int pendingEvents;
        int activeReservations;
        bool repositoryInspected;
        try
        {
            pendingEvents = await observation.CountPendingEvents(cancellationToken).ConfigureAwait(false);
            activeReservations = await observation.CountActiveReservations(cancellationToken).ConfigureAwait(false);
            repositoryInspected = await observation.InspectRepository(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new QuiescenceOutcome.Uncertain(
                "cancelled",
                await SnapshotAsync(
                    requestId,
                    state,
                    observation,
                    CancellationToken.None).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return new QuiescenceOutcome.Uncertain(
                $"observation_failed:{ex.GetType().Name}",
                LocalOnly(
                    requestId,
                    state,
                    pendingEvents: -1,
                    activeReservations: -1,
                    repositoryInspected: false));
        }

        AssignmentQuiescenceProof proof;
        lock (state.Sync)
        {
            proof = new AssignmentQuiescenceProof(
                _terminalRequests.ContainsKey(requestId),
                state.ActiveChildren,
                state.ActiveOperations,
                state.ActiveProcesses,
                pendingEvents,
                activeReservations,
                repositoryInspected,
                _timeProvider.GetUtcNow());
        }

        if (proof.IsExact)
        {
            return new QuiescenceOutcome.Proven(proof);
        }

        // Any deviation (resumed activity, pending events, active reservations, failed
        // inspection) keeps waiting until the caller's deadline in the outer loop.
        return null;
    }

    private async Task<AssignmentQuiescenceProof> SnapshotAsync(
        Guid requestId,
        RequestState state,
        QuiescenceObservation observation,
        CancellationToken cancellationToken)
    {
        var pendingEvents = -1;
        var activeReservations = -1;
        var repositoryInspected = false;
        try
        {
            pendingEvents = await observation.CountPendingEvents(cancellationToken).ConfigureAwait(false);
            activeReservations = await observation.CountActiveReservations(cancellationToken).ConfigureAwait(false);
            repositoryInspected = await observation.InspectRepository(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Snapshot is best-effort for the uncertainty report; -1 marks an unreadable value.
        }

        return LocalOnly(
            requestId,
            state,
            pendingEvents,
            activeReservations,
            repositoryInspected);
    }

    private AssignmentQuiescenceProof LocalOnly(
        Guid requestId,
        RequestState state,
        int pendingEvents,
        int activeReservations,
        bool repositoryInspected)
    {
        lock (state.Sync)
        {
            return new AssignmentQuiescenceProof(
                _terminalRequests.ContainsKey(requestId),
                state.ActiveChildren,
                state.ActiveOperations,
                state.ActiveProcesses,
                pendingEvents,
                activeReservations,
                repositoryInspected,
                _timeProvider.GetUtcNow());
        }
    }

    public void CommitTerminalization(Guid requestId)
    {
        while (true)
        {
            var state = _requests.GetOrAdd(requestId, static _ => new RequestState());
            lock (state.Sync)
            {
                if (!IsCurrentState(requestId, state))
                {
                    continue;
                }

                _terminalRequests.TryAdd(requestId, 0);
                state.TerminalizationCommitted = true;
                TryCleanupLocked(requestId, state);
                return;
            }
        }
    }

    private bool IsCurrentState(Guid requestId, RequestState state)
        => _requests.TryGetValue(requestId, out var current) && ReferenceEquals(current, state);

    private void TryCleanupLocked(Guid requestId, RequestState state)
    {
        if (!state.TerminalizationCommitted
            || state.CallbackSources != 0
            || !state.IsDrained
            || !_requests.TryRemove(new KeyValuePair<Guid, RequestState>(requestId, state)))
        {
            return;
        }

        _terminalRequests.TryRemove(requestId, out _);
    }
}
