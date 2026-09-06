using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Application.Requests;

/// <summary>
/// API-safe projection of a durable execution assignment and its immutable placement snapshot.
/// </summary>
public sealed record ExecutionAssignmentProjectionDto(
    Guid RequestId,
    Guid ProjectId,
    Guid WorkspaceBindingId,
    Guid NodeIdSnapshot,
    string CanonicalRepositoryPathSnapshot,
    string DefaultBranchSnapshot,
    long BindingValidationRevisionSnapshot,
    ExecutionAssignmentState State,
    DateTimeOffset AssignedAt,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset? LastRenewedAt,
    DateTimeOffset? LastReconciledAt,
    DateTimeOffset? TerminalAt);
