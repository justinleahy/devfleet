using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Application.Requests;

/// <summary>
/// A durable execution assignment together with the immutable request and placement snapshot
/// required by the assigned node.
/// </summary>
public sealed record ExecutionAssignmentDto(
    WorkRequestId RequestId,
    ProjectId ProjectId,
    WorkspaceBindingId WorkspaceBindingId,
    NodeId NodeIdSnapshot,
    string CanonicalRepositoryPathSnapshot,
    string DefaultBranchSnapshot,
    long BindingValidationRevisionSnapshot,
    ExecutionAssignmentState State,
    string ClaimToken,
    DateTimeOffset AssignedAt,
    DateTimeOffset LeaseExpiresAt,
    string RequestTitle,
    string RequestPrompt,
    WorkRequestKind RequestKind,
    RiskLevel RequestRiskLevel,
    bool CreateRequestBranch,
    bool CreateRequestCommit,
    string? VerificationPolicyRevision,
    string? BaselineVersion,
    string? TrustedVerificationProfileId,
    string? TrustedVerificationProfileRevision,
    string? MandatoryCommandIdsJson);

/// <summary>One durable node inventory entry with its current local execution evidence.</summary>
public sealed record ExecutionAssignmentInventoryDto(
    WorkRequestId RequestId,
    ProjectId ProjectId,
    WorkspaceBindingId WorkspaceBindingId,
    NodeId NodeIdSnapshot,
    string CanonicalRepositoryPathSnapshot,
    string DefaultBranchSnapshot,
    long BindingValidationRevisionSnapshot,
    ExecutionAssignmentState? State,
    string ClaimToken,
    DateTimeOffset AssignedAt,
    AssignmentSupervisorState SupervisorState,
    bool RepositoryKnown,
    int PendingEventCount);

/// <summary>The authoritative control-plane disposition for one reconciled assignment.</summary>
public sealed record AssignmentReconciliationResultDto(
    WorkRequestId RequestId,
    AssignmentReconciliationDisposition Disposition,
    ExecutionAssignmentDto? Assignment);
