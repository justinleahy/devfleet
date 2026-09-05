using PiCommandCenter.Domain;

namespace PiCommandCenter.Infrastructure.Reservations;

/// <summary>Persisted reservation lease group row (SQLite).</summary>
public sealed class ReservationLeaseRow
{
    public Guid Id { get; init; }

    public Guid ProjectId { get; init; }

    public Guid RequestId { get; init; }

    public string OwnerSessionId { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public long FencingToken { get; set; }

    public string State { get; set; } = string.Empty;

    public long AcquiredAtUtcTicks { get; init; }

    public long LastRenewedAtUtcTicks { get; set; }

    public long ExpiresAtUtcTicks { get; set; }

    public long? ReleasedAtUtcTicks { get; set; }

    public long Version { get; set; }

    public List<ReservationScopeRow> Scopes { get; } = [];
}

/// <summary>One normalized scope of a lease group.</summary>
public sealed class ReservationScopeRow
{
    public Guid Id { get; init; }

    public Guid LeaseId { get; init; }

    public int Kind { get; init; }

    /// <summary>Normalized path; directory scopes end with '/'; resources are plain names.</summary>
    public string Path { get; init; } = string.Empty;
}

/// <summary>
/// Project-scoped monotonic fencing token counter; every grant, transfer, and forced
/// release increments it inside the acquisition transaction.
/// </summary>
public sealed class ProjectFencingTokenRow
{
    public Guid ProjectId { get; init; }

    public long LastFencingToken { get; set; }
}

/// <summary>Append-only audit fact for recovery and forced-release decisions.</summary>
public sealed class ReservationAuditFactRow
{
    public Guid Id { get; init; }

    public Guid LeaseId { get; init; }

    public Guid ProjectId { get; init; }

    /// <summary>Expired / ForceReleased / Transferred / Released.</summary>
    public string Kind { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string? RepositoryStatusSnapshot { get; init; }

    public string? Actor { get; init; }

    public long AtUtcTicks { get; init; }
}
