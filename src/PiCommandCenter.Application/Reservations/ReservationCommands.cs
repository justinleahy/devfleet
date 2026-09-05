namespace PiCommandCenter.Application.Reservations;

/// <summary>All-or-nothing request to reserve one or more scopes for one agent session.</summary>
public sealed record AcquireReservationCommand(
    Guid ProjectId,
    Guid RequestId,
    string OwnerSessionId,
    IReadOnlyList<ReservationScopeDto> Scopes,
    string Reason);

/// <summary>Extends a lease deadline; requires owner session and current fencing token.</summary>
public sealed record RenewReservationCommand(
    Guid LeaseId,
    long FencingToken,
    string SessionId);

/// <summary>Atomically adds scopes to an existing lease; fails without change on any conflict.</summary>
public sealed record ExpandReservationCommand(
    Guid LeaseId,
    long FencingToken,
    string SessionId,
    IReadOnlyList<ReservationScopeDto> Scopes);

/// <summary>Releases an active lease owned by the session.</summary>
public sealed record ReleaseReservationCommand(
    Guid LeaseId,
    string SessionId);

/// <summary>Atomic handoff of an active lease to another session; issues a fresh fencing token.</summary>
public sealed record TransferReservationCommand(
    Guid LeaseId,
    string FromSessionId,
    string ToSessionId);

/// <summary>
/// Mutation authorization: the authority validates lease state, ownership, fencing token,
/// and scope coverage before the caller may touch <see cref="TargetPath"/>.
/// </summary>
public sealed record MutationAuthorizationCommand(
    Guid LeaseId,
    long FencingToken,
    string SessionId,
    string TargetPath,
    int Operation,
    string? TargetScopeKind = null);

/// <summary>Flags an expired lease for expired-ownership recovery inspection.</summary>
public sealed record MarkRecoveryRequiredCommand(
    Guid LeaseId,
    string Reason);

/// <summary>
/// Human-only administrator release: mandatory reason, current repository status snapshot
/// for the audit trail, and a fencing-token increment.
/// </summary>
public sealed record ForceReleaseReservationCommand(
    Guid LeaseId,
    string Reason,
    string RepositoryStatusSnapshot,
    string RequestedBy);
