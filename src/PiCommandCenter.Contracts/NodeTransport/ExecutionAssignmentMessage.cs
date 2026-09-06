namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>
/// Transport snapshot of a durable execution assignment returned to its assigned node.
/// </summary>
public sealed record ExecutionAssignmentMessage(
    Guid RequestId,
    Guid ProjectId,
    Guid WorkspaceBindingId,
    Guid NodeIdSnapshot,
    string CanonicalRepositoryPathSnapshot,
    string DefaultBranchSnapshot,
    long BindingValidationRevisionSnapshot,
    string State,
    string ClaimToken,
    DateTimeOffset AssignedAt,
    DateTimeOffset LeaseExpiresAt,
    string RequestTitle,
    string RequestPrompt,
    string RequestKind,
    string RequestRiskLevel,
    bool CreateRequestBranch,
    bool CreateRequestCommit,
    string? VerificationPolicyRevision = null,
    string? BaselineVersion = null,
    string? TrustedVerificationProfileId = null,
    string? TrustedVerificationProfileRevision = null,
    string? MandatoryCommandIdsJson = null);
