namespace PiCommandCenter.Infrastructure.Recovery;

/// <summary>Durable recovery operation for one captured project inventory.</summary>
public sealed class RecoveryOperationRow
{
    public Guid Id { get; init; }

    public Guid ProjectId { get; init; }

    /// <summary>Pending / Running / NeedsIntervention / Recovered.</summary>
    public string Status { get; set; } = string.Empty;

    public int Attempt { get; set; }

    public string InventoryRevision { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string Actor { get; init; } = string.Empty;

    public string? Stage { get; set; }

    public string? BlockerCodesJson { get; set; }

    public string? EvidenceJson { get; set; }

    public long CreatedAtUtcTicks { get; init; }

    public long UpdatedAtUtcTicks { get; set; }

    public long? CompletedAtUtcTicks { get; set; }

    public long? DeadlineUtcTicks { get; set; }

    public long LastProgressUtcTicks { get; set; }

    public long Version { get; set; }

    public List<RecoveryTargetRow> AssignmentTargets { get; } = [];

    public List<RecoveryReservationTargetRow> ReservationTargets { get; } = [];
}

/// <summary>Captured nonterminal assignment target of one recovery operation.</summary>
public sealed class RecoveryTargetRow
{
    public Guid Id { get; init; }

    public Guid OperationId { get; init; }

    public Guid RequestId { get; init; }

    public long CapturedVersion { get; init; }

    public string CapturedState { get; init; } = string.Empty;

    public long BindingRevision { get; init; }

    public string? Outcome { get; set; }

    public string? EvidenceJson { get; set; }
}

/// <summary>Captured unresolved reservation target of one recovery operation.</summary>
public sealed class RecoveryReservationTargetRow
{
    public Guid Id { get; init; }

    public Guid OperationId { get; init; }

    public Guid LeaseId { get; init; }

    public long CapturedVersion { get; init; }

    public string CapturedState { get; init; } = string.Empty;

    public string? Outcome { get; set; }

    public string? EvidenceJson { get; set; }
}

/// <summary>
/// Project-wide scheduling hold. Distinct from operation success; survives Recovered until resume.
/// At most one row per project.
/// </summary>
public sealed class RecoveryHoldRow
{
    public Guid ProjectId { get; init; }

    public Guid OperationId { get; set; }

    public long EstablishedAtUtcTicks { get; init; }

    public long Version { get; set; }
}

/// <summary>Project-and-action scoped idempotency key with accepted input hash.</summary>
public sealed class RecoveryIdempotencyRow
{
    public Guid ProjectId { get; init; }

    public string Action { get; init; } = string.Empty;

    public string Key { get; init; } = string.Empty;

    public string InputHash { get; init; } = string.Empty;

    public Guid? OperationId { get; init; }

    public long CreatedAtUtcTicks { get; init; }
}

/// <summary>Append-only recovery audit fact.</summary>
public sealed class RecoveryAuditFactRow
{
    public Guid Id { get; init; }

    public Guid OperationId { get; init; }

    public Guid ProjectId { get; init; }

    public string Kind { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string? Actor { get; init; }

    public string? PayloadJson { get; init; }

    public long AtUtcTicks { get; init; }
}
