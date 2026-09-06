using System.ComponentModel.DataAnnotations;

namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>Transport message: node acquires a new reservation lease.</summary>
public sealed record AcquireReservationMessage(
    Guid ProjectId,
    Guid RequestId,
    [property: Required, MaxLength(128)] string ClaimToken,
    string OwnerSessionId,
    ReservationScopeMessage[] Scopes,
    string Reason);

/// <summary>
/// Transport message: node-side call that addresses an existing lease with its fencing
/// token (renew, expand).
/// </summary>
public sealed record ReservationMutationMessage(
    Guid ProjectId,
    Guid RequestId,
    [property: Required, MaxLength(128)] string ClaimToken,
    Guid LeaseId,
    long FencingToken,
    string SessionId);

/// <summary>Transport message: node widens an existing lease with additional scopes.</summary>
public sealed record ExpandReservationMessage(
    Guid ProjectId,
    Guid RequestId,
    [property: Required, MaxLength(128)] string ClaimToken,
    Guid LeaseId,
    long FencingToken,
    string SessionId,
    ReservationScopeMessage[] Scopes);

/// <summary>Transport message: node voluntarily releases a lease it owns.</summary>
public sealed record ReleaseReservationMessage(
    Guid ProjectId,
    Guid RequestId,
    [property: Required, MaxLength(128)] string ClaimToken,
    Guid LeaseId,
    string SessionId);

/// <summary>Transport message: ownership of a lease moves between node sessions.</summary>
public sealed record TransferReservationMessage(
    Guid ProjectId,
    Guid RequestId,
    [property: Required, MaxLength(128)] string ClaimToken,
    Guid LeaseId,
    string FromSessionId,
    string ToSessionId);

/// <summary>
/// Transport message: node asks the control plane to authorize one mutation against a
/// lease immediately before performing it.
/// </summary>
public sealed record MutationAuthorizationMessage(
    Guid ProjectId,
    Guid RequestId,
    [property: Required, MaxLength(128)] string ClaimToken,
    Guid LeaseId,
    long FencingToken,
    string SessionId,
    string TargetPath,
    int Operation,
    string OperationName);

/// <summary>Transport message: node flags a lease as needing recovery.</summary>
public sealed record MarkRecoveryMessage(
    Guid ProjectId,
    Guid RequestId,
    [property: Required, MaxLength(128)] string ClaimToken,
    Guid LeaseId,
    string Reason);
/// <summary>Transport message: node lists a project's reservation leases.</summary>
public sealed record ListReservationsMessage(
    Guid ProjectId,
    Guid RequestId,
    [property: Required, MaxLength(128)] string ClaimToken,
    bool IncludeReleased);
