namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>
/// Transport mirror of <c>PiCommandCenter.Application.Reservations.ReservationScopeDto</c>.
/// <paramref name="Kind"/> is the numeric enum value; <paramref name="KindName"/> carries the
/// symbolic name (File/Directory/Resource) so the protocol stays readable across versions.
/// </summary>
public sealed record ReservationScopeMessage(int Kind, string KindName, string Path);

/// <summary>
/// Transport mirror of <c>ReservationLeaseDto</c>: every lease fact the control plane
/// tracks, minus internal bookkeeping.
/// </summary>
public sealed record ReservationLeaseMessage(
    Guid LeaseId,
    Guid ProjectId,
    Guid RequestId,
    string OwnerSessionId,
    long FencingToken,
    int State,
    string StateName,
    string? Reason,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ReleasedAt,
    ReservationScopeMessage[] Scopes);

/// <summary>
/// Transport mirror of <c>ReservationConflictDto</c>: the conflicting scope of a lease
/// that blocked an acquisition or expansion.
/// </summary>
public sealed record ReservationConflictMessage(
    Guid LeaseId,
    string OwnerSessionId,
    int ScopeKind,
    string ScopeKindName,
    string ScopePath);

/// <summary>
/// Typed reservation failure carried in-band instead of a raw hub exception string.
/// <paramref name="Code"/> is one of <see cref="ReservationErrorCodes"/>.
/// </summary>
public sealed record ReservationErrorMessage(
    string Code,
    string Message,
    ReservationConflictMessage[] Conflicts);

/// <summary>Stable error codes for <see cref="ReservationErrorMessage"/>.</summary>
public static class ReservationErrorCodes
{
    public const string Conflict = "conflict";
    public const string NotFound = "not_found";
    public const string InvalidFencingToken = "invalid_fencing_token";
    public const string InvalidState = "invalid_state";
    public const string Validation = "validation";
    public const string Unknown = "unknown";
}

/// <summary>
/// Result envelope for one-argument reservation hub methods: either the resulting lease
/// facts or a typed error; never both, never neither.
/// </summary>
public sealed record ReservationOperationResultMessage(
    ReservationLeaseMessage? Lease,
    ReservationErrorMessage? Error);

/// <summary>
/// Result of a mutation authorization attempt. Authorization failures surface as typed
/// errors, not as a bare <c>false</c>, so the node can distinguish fencing loss from
/// scope mismatch from recovery.
/// </summary>
public sealed record MutationAuthorizationResultMessage(
    bool Authorized,
    ReservationErrorMessage? Error);
