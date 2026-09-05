using PiCommandCenter.Domain.Reservations;

namespace PiCommandCenter.Application.Reservations;

/// <summary>Wire/read-model view of one reservation scope.</summary>
public sealed record ReservationScopeDto(
    int Kind,
    string KindName,
    string Path);

/// <summary>Read-model view of a reservation lease group.</summary>
public sealed record ReservationLeaseDto(
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
    IReadOnlyList<ReservationScopeDto> Scopes);

/// <summary>One deterministic conflict between a requested scope and an existing lease scope.</summary>
public sealed record ReservationConflictDto(
    Guid LeaseId,
    string OwnerSessionId,
    int ScopeKind,
    string ScopeKindName,
    string ScopePath);
