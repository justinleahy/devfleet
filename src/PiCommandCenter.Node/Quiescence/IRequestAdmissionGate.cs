namespace PiCommandCenter.Node.Quiescence;

/// <summary>Kinds of request-scoped activity tracked by <see cref="IRequestAdmissionGate"/>.</summary>
public enum NodeActivityKind
{
    /// <summary>A running child agent (and its runtime process) owned by the request.</summary>
    Child,

    /// <summary>An in-flight mutation, verification, or Git operation.</summary>
    Operation,

    /// <summary>A bounded external process spawned on behalf of the request.</summary>
    Process,
}

/// <summary>
/// Node-local observation of one request's quiescence. Exact only when admission is closed,
/// every activity count is zero, no request-scoped spool events are pending, no reservation
/// is active, and the repository was successfully inspected.
/// </summary>
public sealed record AssignmentQuiescenceProof(
    bool AdmissionClosed,
    int ActiveChildren,
    int ActiveOperations,
    int ActiveProcesses,
    int PendingEvents,
    int ActiveReservations,
    bool RepositoryInspected,
    DateTimeOffset ObservedAt)
{
    /// <summary>True only for the exact all-zero/all-true barrier state.</summary>
    public bool IsExact =>
        AdmissionClosed
        && ActiveChildren == 0
        && ActiveOperations == 0
        && ActiveProcesses == 0
        && PendingEvents == 0
        && ActiveReservations == 0
        && RepositoryInspected;
}

/// <summary>
/// Outcome of a quiescence probe: either an exact proof, or explicit uncertainty carrying the
/// real observed snapshot. A proof is never fabricated from incomplete observations.
/// </summary>
public abstract record QuiescenceOutcome
{
    private QuiescenceOutcome()
    {
    }

    /// <summary>Every requirement was observed satisfied; the proof is exact.</summary>
    public sealed record Proven(AssignmentQuiescenceProof Proof) : QuiescenceOutcome;

    /// <summary>The barrier could not be proven; <see cref="Observed"/> is the real last snapshot.</summary>
    public sealed record Uncertain(string Reason, AssignmentQuiescenceProof Observed) : QuiescenceOutcome;
}

/// <summary>
/// External observations the gate cannot perform itself: active reservation count, pending
/// request-scoped spool events, and repository inspection. Each returns the live value;
/// exceptions are converted to uncertainty by the gate.
/// </summary>
public sealed record QuiescenceObservation(
    Func<CancellationToken, Task<int>> CountActiveReservations,
    Func<CancellationToken, Task<int>> CountPendingEvents,
    Func<CancellationToken, Task<bool>> InspectRepository);

/// <summary>One admitted unit of request-scoped activity; disposing ends it exactly once.</summary>
public sealed class NodeActivityLease : IDisposable
{
    private Action? _release;

    internal NodeActivityLease(NodeActivityKind kind, string description, Action release)
    {
        Kind = kind;
        Description = description;
        _release = release;
    }

    public NodeActivityKind Kind { get; }

    public string Description { get; }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>
/// One live runtime that can issue request callbacks. Terminal fences remain until every
/// callback lease is disposed, even after terminalization commits.
/// </summary>
public sealed class RequestCallbackLease : IDisposable
{
    private Action? _release;

    internal RequestCallbackLease(string sessionId, Action release)
    {
        SessionId = sessionId;
        _release = release;
    }

    public string SessionId { get; }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>
/// Assignment/request-scoped admission and activity barrier. Closing admission atomically
/// rejects all new spawn/mutation/verification/Git work for the request while already-admitted
/// activity drains; <see cref="ProveQuiescenceAsync"/> waits for the drain and combines it with
/// reservation, spool, and repository observations into an exact proof — or reports explicit
/// uncertainty. Terminalization preflight is tracked until admission closes and the proof begins.
/// </summary>
public interface IRequestAdmissionGate
{
    /// <summary>True once admission for the request has been closed; closed is irreversible.</summary>
    bool IsAdmissionClosed(Guid requestId);

    /// <summary>Admits one mutation/verification/Git operation, or returns null once admission is closed.</summary>
    NodeActivityLease? TryEnterOperation(Guid requestId, string operation);

    /// <summary>Admits one child agent, or returns null once admission is closed.</summary>
    NodeActivityLease? TryAdmitChild(Guid requestId, string childSessionId);

    /// <summary>
    /// Exclusively admits one terminalization preflight. Returns null while another preflight
    /// is active or after admission closes.
    /// </summary>
    NodeActivityLease? TryEnterTerminalization(Guid requestId, string operation);

    /// <summary>
    /// Registers a root runtime that can issue callbacks, or returns null after admission closes.
    /// The returned lease must be held until that runtime is stopped and callback dispatch drained.
    /// </summary>
    RequestCallbackLease? TryRegisterCallbackSource(Guid requestId, string sessionId);

    /// <summary>
    /// Tracks one bounded process for the request. Tracking is not admission: processes spawned
    /// by already-admitted work must stay visible to the barrier even after closure.
    /// </summary>
    NodeActivityLease TrackProcess(Guid requestId, string description);

    /// <summary>Atomically closes admission for the request; idempotent.</summary>
    void CloseAdmission(Guid requestId);

    /// <summary>
    /// Waits until the request is drained and all external observations are clean, then returns
    /// an exact proof. On timeout, cancellation, or observation failure returns
    /// <see cref="QuiescenceOutcome.Uncertain"/> with the real observed values.
    /// </summary>
    Task<QuiescenceOutcome> ProveQuiescenceAsync(
        Guid requestId,
        QuiescenceObservation observation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that authoritative terminalization committed. The terminal tombstone is removed
    /// only after all activity and runtime callback leases have also drained.
    /// </summary>
    void CommitTerminalization(Guid requestId);
}
