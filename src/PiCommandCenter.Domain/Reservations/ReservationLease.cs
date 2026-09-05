using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Domain.Reservations;

/// <summary>
/// A reservation lease group: an exclusive, time-limited lease over one or more scopes,
/// owned by one agent session within one project. Every grant, renewal-side ownership
/// change (transfer), and forced release carries a project-scoped monotonic fencing token;
/// mutations require the exact current token. State transitions are validated here so
/// invalid lease state is unrepresentable.
/// </summary>
public sealed class ReservationLease
{
    private readonly List<ReservationScope> _scopes;

    private ReservationLease(
        Guid id,
        ProjectId projectId,
        WorkRequestId requestId,
        string ownerSessionId,
        string reason,
        long fencingToken,
        ReservationLeaseState state,
        IReadOnlyList<ReservationScope> scopes,
        DateTimeOffset acquiredAt,
        DateTimeOffset lastRenewedAt,
        DateTimeOffset expiresAt,
        DateTimeOffset? releasedAt,
        long version)
    {
        Id = id;
        ProjectId = projectId;
        RequestId = requestId;
        OwnerSessionId = ownerSessionId;
        Reason = reason;
        FencingToken = fencingToken;
        State = state;
        _scopes = [.. scopes];
        AcquiredAt = acquiredAt;
        LastRenewedAt = lastRenewedAt;
        ExpiresAt = expiresAt;
        ReleasedAt = releasedAt;
        Version = version;
    }

    public Guid Id { get; }

    public ProjectId ProjectId { get; }

    public WorkRequestId RequestId { get; }

    /// <summary>Current owner session id.</summary>
    public string OwnerSessionId { get; private set; }

    /// <summary>Non-empty acquisition reason.</summary>
    public string Reason { get; }

    /// <summary>Project-scoped monotonic token; every grant or ownership transfer increments it.</summary>
    public long FencingToken { get; private set; }

    public ReservationLeaseState State { get; private set; }

    public IReadOnlyList<ReservationScope> Scopes => _scopes;

    public DateTimeOffset AcquiredAt { get; }

    public DateTimeOffset LastRenewedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ReleasedAt { get; private set; }

    /// <summary>Optimistic concurrency token.</summary>
    public long Version { get; private set; }

    /// <summary>Creates a new active lease group over the given (already conflict-checked) scopes.</summary>
    public static ReservationLease Acquire(
        Guid id,
        ProjectId projectId,
        WorkRequestId requestId,
        string ownerSessionId,
        string reason,
        long fencingToken,
        IReadOnlyList<ReservationScope> scopes,
        DateTimeOffset now,
        TimeSpan leaseDuration)
    {
        ValidateOwner(ownerSessionId);
        ValidateReason(reason);
        ValidateScopes(scopes);
        if (fencingToken < 1)
        {
            throw new InvalidOperationException("Fencing tokens start at 1.");
        }

        return new ReservationLease(
            id != Guid.Empty ? id : Guid.NewGuid(),
            projectId,
            requestId,
            ownerSessionId,
            reason,
            fencingToken,
            ReservationLeaseState.Active,
            scopes,
            now,
            now,
            now + leaseDuration,
            releasedAt: null,
            version: 1);
    }

    /// <summary>Rehydrates a persisted lease without mutating anything.</summary>
    public static ReservationLease Rehydrate(
        Guid id,
        ProjectId projectId,
        WorkRequestId requestId,
        string ownerSessionId,
        string reason,
        long fencingToken,
        ReservationLeaseState state,
        IReadOnlyList<ReservationScope> scopes,
        DateTimeOffset acquiredAt,
        DateTimeOffset lastRenewedAt,
        DateTimeOffset expiresAt,
        DateTimeOffset? releasedAt,
        long version) => new(
            id,
            projectId,
            requestId,
            ownerSessionId,
            reason,
            fencingToken,
            state,
            scopes,
            acquiredAt,
            lastRenewedAt,
            expiresAt,
            releasedAt,
            version);

    /// <summary>Extends the lease deadline; requires the active owner and current token.</summary>
    public void Renew(string sessionId, long fencingToken, DateTimeOffset now, TimeSpan leaseDuration)
    {
        EnsureUsableBy(sessionId, fencingToken, now);
        LastRenewedAt = now;
        ExpiresAt = now + leaseDuration;
        Version++;
    }

    /// <summary>
    /// Adds already conflict-checked scopes atomically to the active lease; the deadline is
    /// refreshed to the full duration from now. Ownership and fencing token do not change.
    /// </summary>
    public void Expand(
        IReadOnlyList<ReservationScope> scopes,
        string sessionId,
        long fencingToken,
        DateTimeOffset now,
        TimeSpan leaseDuration)
    {
        ValidateScopes(scopes);
        EnsureUsableBy(sessionId, fencingToken, now);
        _scopes.AddRange(scopes);
        LastRenewedAt = now;
        ExpiresAt = now + leaseDuration;
        Version++;
    }

    /// <summary>Releases the lease; the owner may release at any time while it is usable.</summary>
    public void Release(string sessionId, DateTimeOffset now)
    {
        if (!string.Equals(OwnerSessionId, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Lease '{Id}' is not owned by session '{sessionId}'.");
        }

        if (State != ReservationLeaseState.Active)
        {
            throw new InvalidOperationException(
                $"Lease '{Id}' in state {State} cannot be released by its owner.");
        }

        State = ReservationLeaseState.Released;
        ReleasedAt = now;
        Version++;
    }

    /// <summary>
    /// Atomically hands ownership to another session: ownership changes and a fresh,
    /// higher fencing token is issued. The old token is invalidated immediately.
    /// </summary>
    public void Transfer(
        string fromSessionId,
        string toSessionId,
        long newFencingToken,
        DateTimeOffset now,
        TimeSpan leaseDuration)
    {
        ValidateOwner(toSessionId);
        if (!string.Equals(OwnerSessionId, fromSessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Lease '{Id}' is not owned by session '{fromSessionId}'; handoff rejected.");
        }

        if (State != ReservationLeaseState.Active)
        {
            throw new InvalidOperationException(
                $"Lease '{Id}' in state {State} cannot be transferred.");
        }

        if (newFencingToken <= FencingToken)
        {
            throw new InvalidOperationException(
                $"Fencing tokens must be strictly monotonic: {newFencingToken} does not exceed {FencingToken}.");
        }

        OwnerSessionId = toSessionId;
        FencingToken = newFencingToken;
        LastRenewedAt = now;
        ExpiresAt = now + leaseDuration;
        Version++;
    }

    /// <summary>
    /// Flags an expired active lease for expired-ownership recovery inspection; the scope is
    /// not re-grantable until recovery completes or an administrator force-releases.
    /// </summary>
    public void MarkRecoveryRequired(DateTimeOffset now)
    {
        if (State != ReservationLeaseState.Active)
        {
            throw new InvalidOperationException(
                $"Lease '{Id}' in state {State} cannot be marked recovery-required.");
        }

        if (ExpiresAt > now)
        {
            throw new InvalidLeaseStateException(
                $"Lease '{Id}' has not expired yet (expires {ExpiresAt:O}).");
        }

        State = ReservationLeaseState.RecoveryRequired;
        Version++;
    }

    /// <summary>
    /// Administrator force release: releases the lease regardless of owner liveness, with a
    /// mandatory reason and repository status snapshot, and increments the fencing token so
    /// any stale token held by the former owner is permanently rejected.
    /// </summary>
    public void ForceRelease(
        string reason,
        string repositoryStatusSnapshot,
        long newFencingToken,
        DateTimeOffset now)
    {
        ValidateReason(reason);
        if (string.IsNullOrWhiteSpace(repositoryStatusSnapshot))
        {
            throw new InvalidOperationException(
                "Force release requires a repository status snapshot for the audit trail.");
        }

        if (State == ReservationLeaseState.Released)
        {
            throw new InvalidOperationException($"Lease '{Id}' is already released.");
        }

        if (newFencingToken <= FencingToken)
        {
            throw new InvalidOperationException(
                $"Fencing tokens must be strictly monotonic: {newFencingToken} does not exceed {FencingToken}.");
        }

        FencingToken = newFencingToken;
        State = ReservationLeaseState.Released;
        ReleasedAt = now;
        Version++;
    }

    /// <summary>
    /// Validates a mutation request against this lease: state active, unexpired, correct
    /// owner, exact current fencing token, and a scope covering the target. The target must
    /// already be normalized.
    /// </summary>
    public void Authorize(
        MutationAuthorizationRequest request,
        DateTimeOffset now)
    {
        if (State != ReservationLeaseState.Active)
        {
            throw new InvalidOperationException(
                $"Lease '{Id}' is in state {State}; mutations are not authorized.");
        }

        if (ExpiresAt <= now)
        {
            throw new InvalidOperationException(
                $"Lease '{Id}' expired at {ExpiresAt:O}; mutations are not authorized until recovery completes.");
        }

        if (!string.Equals(OwnerSessionId, request.SessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Lease '{Id}' is not owned by session '{request.SessionId}'.");
        }

        if (FencingToken != request.FencingToken)
        {
            throw new PiCommandCenter.Domain.Reservations.InvalidFencingTokenException(Id, FencingToken, request.FencingToken);
        }

        var covered = false;
        foreach (var scope in _scopes)
        {
            if (scope.Covers(request.Target))
            {
                covered = true;
                break;
            }
        }

        if (!covered)
        {
            throw new InvalidOperationException(
                $"Lease '{Id}' does not cover target '{request.Target.Path}'.");
        }
    }

    private void EnsureUsableBy(string sessionId, long fencingToken, DateTimeOffset now)
    {
        if (State != ReservationLeaseState.Active)
        {
            throw new InvalidOperationException(
                $"Lease '{Id}' is in state {State}; the operation requires an active lease.");
        }

        if (ExpiresAt <= now)
        {
            throw new InvalidOperationException(
                $"Lease '{Id}' expired at {ExpiresAt:O}; renew recovery is required.");
        }

        if (!string.Equals(OwnerSessionId, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Lease '{Id}' is not owned by session '{sessionId}'.");
        }

        if (FencingToken != fencingToken)
        {
            throw new PiCommandCenter.Domain.Reservations.InvalidFencingTokenException(Id, FencingToken, fencingToken);
        }
    }

    private static void ValidateOwner(string ownerSessionId)
    {
        if (string.IsNullOrWhiteSpace(ownerSessionId))
        {
            throw new ArgumentException("Owner session id must not be empty.", nameof(ownerSessionId));
        }
    }

    private static void ValidateReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidOperationException("A non-empty reason is required.");
        }
    }

    private static void ValidateScopes(IReadOnlyList<ReservationScope> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        if (scopes.Count == 0)
        {
            throw new ArgumentException("At least one scope is required.", nameof(scopes));
        }
    }
}

/// <summary>Pre-validated mutation authorization input (target already normalized).</summary>
public sealed record MutationAuthorizationRequest(
    string SessionId,
    long FencingToken,
    ReservationScope Target,
    MutationOperation Operation);

/// <summary>Mutation operations that require reservation authorization.</summary>
public enum MutationOperation
{
    Create = 0,
    Write = 1,
    Edit = 2,
    Delete = 3,
    Move = 4,
}
